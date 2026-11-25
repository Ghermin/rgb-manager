using System.Diagnostics;

namespace RgbManager;

/// <summary>
/// Pilote tous les appareils depuis un seul fil d'exécution dédié. Le même exe sert sur plusieurs PC :
/// il détecte ce qui est branché (carte ASUS ou MSI, RAM Corsair ou Crucial, watercooling, clavier).
/// - Appareils en « mode direct » (AURA, RAM Corsair, Commander CORE) : l'appli calcule la couleur de chaque LED
///   et la renvoie régulièrement (toutes les 3 s en statique, en continu pour les animations).
/// - Appareils à couleur fixe (MSI, RAM Crucial, clavier Logitech) : une seule écriture à chaque changement ;
///   les effets animés y donnent la couleur choisie, fixe.
/// Commander CORE : surveillance de la pompe en plus.
/// </summary>
sealed class Engine
{
    const int PumpMinRpm = 400;
    const int StaticRefreshMs = 3000;   // renvoi des couleurs fixes (le Commander CORE exige < 10 s)
    const int AnimationMs = 50;         // ~20 images/s pour la carte mère et le watercooling
    const int RamAnimationMs = 100;     // ~10 images/s pour la RAM (bus SMBus plus lent)
    const int FixedRetryMs = 30_000;    // nouvel essai si un appareil à couleur fixe a refusé
    const int FixedMinGapMs = 200;      // écart minimal entre deux écritures (curseur qu'on fait glisser)

    readonly Settings _settings;
    readonly object _gate = new();
    readonly bool _admin;

    Thread _thread;
    volatile bool _running;
    bool _applyPending = true;
    bool _logPending = true;

    AuraMainboard _aura;
    bool _auraDirect;
    PawnSmbus _bus;
    List<CorsairDram> _rams = new();
    CommanderCore _cc;
    bool _ccDisabledBySafety;
    MsiMysticLight _msi;
    List<CrucialDram> _crucials = new();
    bool? _ballistix;
    LogitechHidpp _keyboard;
    int _errors;

    public string BoardStatus { get; private set; } = "Carte mère : détection…";
    public string RamStatus { get; private set; } = "RAM : détection…";
    public string CommanderStatus { get; private set; } = "Watercooling : détection…";
    public string KeyboardStatus { get; private set; } = "Clavier : détection…";
    public bool HasBoard => _aura != null || _msi != null;
    public bool HasRam => _rams.Count > 0 || _crucials.Count > 0;
    public bool HasCommander => _cc != null; // reste affiché s'il est coupé par sécurité (statut)
    public bool HasKeyboard => _keyboard != null;

    public event Action StatusChanged;
    public event Action<string> Warning;

    public Engine(Settings settings, bool admin)
    {
        _settings = settings;
        _admin = admin;
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "RGB" };
        _thread.Start();
    }

    /// <summary>
    /// Modifie les réglages (depuis l'interface) et les applique. <paramref name="save"/> = false pendant qu'on fait
    /// glisser un curseur : rendu en direct, sans écrire le fichier ni le journal à chaque pas.
    /// </summary>
    public void Update(Action<Settings> change, bool save = true)
    {
        lock (_gate)
        {
            change(_settings);
            _applyPending = true;
            if (save) _logPending = true;
        }
        if (save) _settings.Save();
    }

    /// <summary>Force un renvoi immédiat des couleurs (ex. sortie de veille).</summary>
    public void RequestApply(bool commit)
    {
        lock (_gate)
        {
            _applyPending = true;
            _logPending = true;
            if (commit) _auraDirect = false;
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _thread?.Join(5000);
    }

    // ------------------------------------------------------------------

    void Loop()
    {
        Detect();
        var clock = Stopwatch.StartNew();
        const long Never = long.MinValue / 2;
        long lastAura = Never, lastRam = Never, lastCc = Never, lastPumpCheck = 0, lastPumpLog = Never, lastDetect = 0, lastFixed = Never, lastFixedWrite = Never;
        int lowPumpReadings = 0;
        bool fixedPending = false, logFixed = false;

        while (_running)
        {
            Settings s;
            bool apply, log;
            lock (_gate)
            {
                s = _settings.Clone();
                apply = _applyPending;
                log = _logPending;
                _applyPending = _logPending = false;
            }

            long now = clock.ElapsedMilliseconds;

            // Appareil pas prêt au démarrage de Windows : on réessaie toutes les 10 s pendant 5 min
            if (Missing && now < 300_000 && now - lastDetect >= 10_000)
            {
                lastDetect = now;
                if (Detect()) apply = true;
            }

            if (apply)
            {
                lastAura = lastRam = lastCc = lastFixed = Never;
                fixedPending = true;
                logFixed |= log;
                if (log)
                    Log.W($"Réglages : {s.Effect}, couleur {s.R},{s.G},{s.B}, luminosité {s.Brightness} % (carte mère {s.MainboardBrightness} %, " +
                          $"RAM {s.RamBrightness} %, watercooling {s.CommanderBrightness} %, clavier {s.KeyboardBrightness} %), vitesse {s.Speed}");
            }

            // --- Appareils à couleur fixe (MSI, RAM Crucial, clavier) : une écriture par changement ---
            if (fixedPending && Due(now, lastFixed, FixedRetryMs) && Due(now, lastFixedWrite, FixedMinGapMs))
            {
                lastFixed = lastFixedWrite = now;
                fixedPending = !ApplyFixed(s, logFixed);
                logFixed = false;
            }

            bool animated = s.Effect is Effect.Breathing or Effect.Rainbow;

            // --- Carte mère (AURA) ---
            if (_aura != null && s.Mainboard && Due(now, lastAura, animated ? AnimationMs : StaticRefreshMs))
            {
                lastAura = now;
                Guarded("AURA", () =>
                {
                    if (!_auraDirect)
                    {
                        _aura.EnterDirectMode();
                        _auraDirect = true;
                        Log.W("AURA : mode direct activé");
                    }
                    foreach (var ch in _aura.Channels)
                        _aura.SendDirect(ch, Frame(s, s.Level(s.MainboardBrightness), now, ch.DirectLeds, ch.DirectLeds));
                });
            }

            // --- RAM ---
            if (_rams.Count > 0 && s.Ram && Due(now, lastRam, animated ? RamAnimationMs : StaticRefreshMs))
            {
                lastRam = now;
                foreach (var ram in _rams)
                {
                    var r = ram;
                    Guarded($"RAM 0x{r.Address:X2}", () =>
                    {
                        if (!r.SetDirect(_bus, Frame(s, s.Level(s.RamBrightness), now, r.LedCount, r.LedCount)))
                            throw new IOException("écriture SMBus refusée");
                    });
                }
            }

            // --- Watercooling (Commander CORE) ---
            if (_cc != null && !_ccDisabledBySafety)
            {
                try
                {
                    if (s.Commander && !_cc.SoftwareMode)
                    {
                        int baseline = _cc.ReadPumpRpm();
                        Log.W($"Commander CORE : pompe à {baseline} tr/min avant la prise de contrôle");
                        if (baseline >= 0 && baseline < PumpMinRpm)
                        {
                            DisableCommander($"La pompe du watercooling ne tourne qu'à {baseline} tr/min : je n'y touche pas.");
                            continue;
                        }
                        _cc.EnterSoftwareMode();
                        lastCc = Never;
                        lastPumpCheck = now - 4000; // premier relevé ~1 s après la prise de contrôle
                    }
                    else if (!s.Commander && _cc.SoftwareMode)
                    {
                        _cc.EnterHardwareMode();
                    }

                    if (_cc.SoftwareMode)
                    {
                        if (Due(now, lastCc, animated ? AnimationMs : StaticRefreshMs))
                        {
                            _cc.SetColors(CommanderFrame(s, now));
                            lastCc = now;
                        }

                        if (now - lastPumpCheck >= 5000)
                        {
                            lastPumpCheck = now;
                            int rpm = _cc.ReadPumpRpm();
                            if (now - lastPumpLog >= 600_000)
                            {
                                lastPumpLog = now;
                                Log.W($"Commander CORE : pompe à {rpm} tr/min");
                            }
                            if (rpm >= 0 && rpm < PumpMinRpm)
                            {
                                lowPumpReadings++;
                                Log.W($"Commander CORE : pompe basse ({rpm} tr/min), alerte {lowPumpReadings}/2");
                                if (lowPumpReadings >= 2)
                                    DisableCommander($"La pompe est tombée à {rpm} tr/min : le watercooling a été rendu à son mode automatique.");
                            }
                            else
                            {
                                lowPumpReadings = 0;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogError("Commander CORE", ex);
                    Thread.Sleep(1000);
                }
            }

            Thread.Sleep(animated ? 15 : 100);
        }

        Shutdown();
    }

    static bool Due(long now, long last, int interval) => now - last >= interval;

    void Guarded(string device, Action action)
    {
        try { action(); }
        catch (Exception ex) { LogError(device, ex); }
    }

    void LogError(string device, Exception ex)
    {
        // Pas plus de 50 erreurs dans le journal, pour ne pas le remplir en cas de souci permanent
        if (++_errors <= 50) Log.W($"{device} : erreur ({ex.GetType().Name} : {ex.Message})");
    }

    void DisableCommander(string message)
    {
        _ccDisabledBySafety = true;
        try { _cc.EnterHardwareMode(); } catch (Exception ex) { Log.W($"Commander CORE : retour au mode matériel impossible ({ex.Message})"); }
        CommanderStatus = "Watercooling : désactivé par sécurité (pompe)";
        Log.W(message);
        Warning?.Invoke(message);
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Couleur fixe sur la carte MSI, la RAM Crucial et le clavier. Renvoie false si l'un a refusé (nouvel essai plus tard).
    /// <paramref name="log"/> = false pendant qu'on fait glisser un curseur : seules les erreurs vont au journal.
    /// </summary>
    bool ApplyFixed(Settings s, bool log)
    {
        (byte, byte, byte) Scaled(int deviceBrightness) => s.Effect == Effect.Off ? ((byte)0, (byte)0, (byte)0) : s.ScaledColor(deviceBrightness);
        bool ok = true;

        if (_msi != null && s.Mainboard)
        {
            var pc = Scaled(s.MainboardBrightness);
            try
            {
                if (_msi.SetStatic(pc.Item1, pc.Item2, pc.Item3)) { if (log) Log.W($"{_msi.Name} : couleur {pc.Item1},{pc.Item2},{pc.Item3}"); }
                else { ok = false; Log.W($"{_msi.Name} : la carte n'a pas confirmé la couleur"); }
            }
            catch (Exception ex) { ok = false; LogError(_msi.Name, ex); }
        }

        if (_crucials.Count > 0 && s.Ram)
        {
            var pc = Scaled(s.RamBrightness);
            bool ramOk = true;
            foreach (var ram in _crucials)
            {
                try { ram.SetStatic(_bus, pc.Item1, pc.Item2, pc.Item3); }
                catch (Exception ex) { ramOk = false; LogError($"RAM 0x{ram.Address:X2}", ex); }
            }
            if (ramOk && log) Log.W($"RAM Crucial : couleur {pc.Item1},{pc.Item2},{pc.Item3} sur {_crucials.Count} barrettes");
            ok &= ramOk;
        }

        if (_keyboard != null && s.Keyboard)
        {
            var kb = Scaled(s.KeyboardBrightness);
            try
            {
                int zones = _keyboard.SetStatic(kb.Item1, kb.Item2, kb.Item3);
                if (zones > 0) { if (log) Log.W($"{_keyboard.Name} : couleur {kb.Item1},{kb.Item2},{kb.Item3} sur {zones} zones"); }
                else { ok = false; Log.W($"{_keyboard.Name} : aucune zone n'accepte la couleur fixe"); }
            }
            catch (Exception ex) { ok = false; LogError(_keyboard.Name, ex); }
        }
        return ok;
    }

    bool Missing => (_aura == null && _msi == null) || (_admin && _rams.Count == 0 && _crucials.Count == 0) || _cc == null || _keyboard == null;

    /// <summary>Détecte les appareils pas encore trouvés. Renvoie true si un nouvel appareil a été trouvé.</summary>
    bool Detect()
    {
        bool found = false;

        if (_aura == null && _msi == null)
        {
            try
            {
                _aura = AuraMainboard.Open();
                _auraDirect = false;
                found = true;
                BoardStatus = $"Carte mère ASUS ({_aura.Channels.Count} zones)";
                Log.W($"AURA : firmware {_aura.Firmware}, table {Log.Hex(_aura.ConfigTable, 0, 60)}");
                foreach (var ch in _aura.Channels)
                    Log.W($"AURA : zone effet {ch.EffectChannel}, direct 0x{ch.DirectChannel:X2}, {ch.DirectLeds} LED, {(ch.Addressable ? "prise ARGB" : "carte mère")}");
            }
            catch (Exception ex)
            {
                BoardStatus = "Carte mère : non détectée";
                Log.W($"AURA : {ex.GetType().Name} : {ex.Message}");
            }

            if (_aura == null)
            {
                try
                {
                    _msi = MsiMysticLight.Open();
                    found = true;
                    BoardStatus = $"Carte mère {_msi.Name}";
                    Log.W($"{_msi.Name} : contrôleur Mystic Light trouvé");
                }
                catch (Exception ex)
                {
                    Log.W($"MSI : {ex.GetType().Name} : {ex.Message}");
                }
            }
        }

        if (!_admin)
        {
            RamStatus = "RAM : droits admin nécessaires";
        }
        else if (_rams.Count == 0 && _crucials.Count == 0)
        {
            try
            {
                if (_bus == null)
                {
                    _bus = PawnSmbus.Open(port: 0);
                    var id = _bus.Identity();
                    Log.W($"SMBus : {id.Name} ({id.Vendor:X4}:{id.Device:X4})");
                }
                // Une seule famille de RAM sondée : les adresses Crucial ne sont lues que si Windows annonce de la Ballistix.
                _ballistix ??= CrucialDram.Installed();
                if (_ballistix == true)
                {
                    _crucials = CrucialDram.Detect(_bus);
                    if (_crucials.Count > 0) found = true;
                    RamStatus = _crucials.Count > 0 ? $"RAM Crucial Ballistix ({_crucials.Count} barrettes)" : "RAM : aucune barrette Crucial trouvée";
                }
                else
                {
                    _rams = CorsairDram.Detect(_bus);
                    if (_rams.Count > 0) found = true;
                    RamStatus = _rams.Count > 0 ? $"RAM Corsair ({_rams.Count} barrettes)" : "RAM : aucune barrette Corsair trouvée";
                }
            }
            catch (Exception ex)
            {
                RamStatus = "RAM : PawnIO indisponible";
                Log.W($"RAM : {ex.GetType().Name} : {ex.Message}");
            }
        }

        if (_cc == null)
        {
            try
            {
                _cc = CommanderCore.Open();
                found = true;
                CommanderStatus = "Watercooling Corsair (Commander CORE)";
            }
            catch (Exception ex)
            {
                CommanderStatus = "Watercooling : non détecté";
                Log.W($"Commander CORE : {ex.GetType().Name} : {ex.Message}");
            }
        }

        if (_keyboard == null)
        {
            try
            {
                _keyboard = LogitechHidpp.Open();
                found = true;
                KeyboardStatus = $"Clavier {_keyboard.Name}";
            }
            catch (Exception ex)
            {
                KeyboardStatus = "Clavier : non détecté";
                Log.W($"Clavier : {ex.GetType().Name} : {ex.Message}");
            }
        }

        StatusChanged?.Invoke();
        return found;
    }

    /// <summary>Image pour le Commander CORE : pompe puis 6 ports × 34 LED.</summary>
    byte[] CommanderFrame(Settings s, long ms)
    {
        int pump = _cc.PumpLeds;
        double bri = s.Level(s.CommanderBrightness);
        var buf = new byte[_cc.TotalLeds * 3];
        Array.Copy(Frame(s, bri, ms, pump, pump), 0, buf, 0, pump * 3);
        var fan = Frame(s, bri, ms, CommanderCore.LedsPerFanPort, CommanderCore.LedsPerFanPort);
        for (int port = 0; port < CommanderCore.FanPorts; port++)
            Array.Copy(fan, 0, buf, (pump + port * CommanderCore.LedsPerFanPort) * 3, fan.Length);
        return buf;
    }

    /// <summary>Couleurs de <paramref name="count"/> LED (3 octets chacune) à l'instant <paramref name="ms"/>, luminosité <paramref name="bri"/> (0 à 1).</summary>
    static byte[] Frame(Settings s, double bri, long ms, int count, int zoneLength)
    {
        var buf = new byte[count * 3];
        double period = s.Speed switch { 0 => 8000.0, 2 => 2500.0, _ => 5000.0 };
        double t = ms / period;

        for (int i = 0; i < count; i++)
        {
            double r, g, b;
            switch (s.Effect)
            {
                case Effect.Static:
                    (r, g, b) = (s.R * bri, s.G * bri, s.B * bri);
                    break;
                case Effect.Breathing:
                    double k = (1 - Math.Cos(2 * Math.PI * t)) / 2 * bri;
                    (r, g, b) = (s.R * k, s.G * k, s.B * k);
                    break;
                case Effect.Rainbow:
                    double hue = (t + (double)i / Math.Max(1, zoneLength)) % 1.0 * 360.0;
                    (r, g, b) = Hsv(hue, bri);
                    break;
                default:
                    (r, g, b) = (0, 0, 0);
                    break;
            }
            buf[i * 3] = (byte)r;
            buf[i * 3 + 1] = (byte)g;
            buf[i * 3 + 2] = (byte)b;
        }
        return buf;
    }

    static (double, double, double) Hsv(double hue, double value)
    {
        double c = 255 * value;
        double x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        return (int)(hue / 60) switch
        {
            0 => (c, x, 0),
            1 => (x, c, 0),
            2 => (0, c, x),
            3 => (0, x, c),
            4 => (x, 0, c),
            _ => (c, 0, x),
        };
    }

    void Shutdown()
    {
        try
        {
            if (_cc != null && _cc.SoftwareMode)
            {
                _cc.EnterHardwareMode();
                Log.W("Commander CORE : rendu au mode matériel");
            }
        }
        catch (Exception ex)
        {
            Log.W($"Commander CORE : retour au mode matériel impossible ({ex.Message})");
        }
        _cc?.Dispose();
        _aura?.Dispose();
        _msi?.Dispose();
        _keyboard?.Dispose();
        _bus?.Dispose();
    }
}
