namespace RgbManager;

/// <summary>
/// Clavier Logitech à éclairage HID++ 2.0 (G513, USB 046D:C33C), fonction Color LED Effects (0x8070) :
/// effet « couleur fixe » sur chaque zone, sans persistance (le clavier reprend son effet à la prochaine mise sous tension).
/// Protocole repris d'OpenRGB (Controllers/LogitechController/LogitechHIDPP20Controller).
/// </summary>
sealed class LogitechHidpp : IDisposable
{
    /// <summary>Claviers connus : PID USB → nom.</summary>
    static readonly Dictionary<string, string> Keyboards = new() { ["c33c"] = "Logitech G513" };

    // Rapport HID++ long : [0x11, périphérique, index de fonction, (fonction | id logiciel), 16 octets de paramètres].
    const ushort HidppUsagePage = 0xFF43;
    const byte LongReport = 0x11;
    const int LongReportLength = 20;
    const byte DeviceIndex = 0xFF, SoftwareId = 0x0E, ErrorFeature = 0xFF;

    const ushort FeatureColorLedEffects = 0x8070;
    const byte FnGetInfo = 0x00, FnGetZoneInfo = 0x10, FnGetEffectInfo = 0x20, FnSetEffect = 0x30;
    const ushort EffectOff = 0x0000, EffectFixed = 0x0001;

    readonly HidDevice _dev;
    byte _feature;

    public string Name { get; }

    LogitechHidpp(HidDevice dev, string name)
    {
        _dev = dev;
        Name = name;
    }

    /// <summary>Le clavier expose plusieurs collections HID : on garde celle des messages HID++ longs.</summary>
    public static LogitechHidpp Open()
    {
        foreach (var kb in Keyboards)
        {
            foreach (string path in HidDevice.Find($"vid_046d&pid_{kb.Key}"))
            {
                HidDevice dev;
                try { dev = HidDevice.Open(path); }
                catch (Exception) { continue; } // collections clavier réservées par Windows
                if (dev.UsagePage == HidppUsagePage && dev.OutputLength == LongReportLength)
                {
                    var k = new LogitechHidpp(dev, kb.Value);
                    try
                    {
                        k._feature = k.Request(0, 0x00, FeatureColorLedEffects >> 8, FeatureColorLedEffects & 0xFF)[0];
                        if (k._feature != 0) return k;
                        Log.W($"{kb.Value} : pas de fonction d'éclairage 0x8070");
                    }
                    catch (Exception ex)
                    {
                        Log.W($"{kb.Value} : {ex.Message}");
                    }
                    k.Dispose();
                    continue;
                }
                dev.Dispose();
            }
        }
        throw new IOException("aucun clavier Logitech connu");
    }

    /// <summary>Met toutes les zones en couleur fixe (noir = effet « éteint »). Renvoie le nombre de zones réglées.</summary>
    public int SetStatic(byte r, byte g, byte b)
    {
        bool off = r == 0 && g == 0 && b == 0;
        ushort wanted = off ? EffectOff : EffectFixed;
        int zones = Request(_feature, FnGetInfo)[0];
        int done = 0;
        for (byte zone = 0; zone < zones; zone++)
        {
            int effects = Request(_feature, FnGetZoneInfo, zone, 0)[3];
            for (byte effect = 0; effect < effects; effect++)
            {
                byte[] info = Request(_feature, FnGetEffectInfo, zone, effect, 0, 0);
                if ((info[2] << 8 | info[3]) != wanted) continue;

                // Effet fixe : R, G, B puis 0x02 (« couleur fixe ») ; l'octet 12 (persistance) reste à 0.
                var p = new byte[13];
                p[0] = zone;
                p[1] = effect;
                if (!off) { p[2] = r; p[3] = g; p[4] = b; p[5] = 0x02; }
                Request(_feature, FnSetEffect, p);
                done++;
                break;
            }
        }
        return done;
    }

    /// <summary>Envoie une requête et attend la réponse qui lui correspond ; renvoie ses 16 octets de paramètres.</summary>
    byte[] Request(byte feature, byte function, params byte[] parameters)
    {
        byte tag = (byte)(function | SoftwareId);
        var request = new byte[LongReportLength];
        request[0] = LongReport;
        request[1] = DeviceIndex;
        request[2] = feature;
        request[3] = tag;
        Array.Copy(parameters, 0, request, 4, parameters.Length);
        _dev.Flush();
        _dev.Write(request);

        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < deadline)
        {
            byte[] r = _dev.Read(Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
            if (r == null) break;
            if (r[0] != LongReport || r[1] != DeviceIndex) continue;
            if (r[2] == ErrorFeature && r[3] == feature && r[4] == tag)
                throw new IOException($"commande 0x{function:X2} refusée (erreur HID++ {r[5]})");
            if (r[2] == feature && r[3] == tag) return Compat.Slice(r, 4, 16);
        }
        throw new System.TimeoutException("le clavier ne répond pas");
    }

    public void Dispose() => _dev.Dispose();
}
