using System.ServiceProcess;

namespace RgbManager;

/// <summary>
/// Carte mère MSI à contrôleur Mystic Light « 185 octets » (B550-A PRO, USB 1462:7C56).
/// Couleur fixe uniquement : on envoie la configuration complète des zones (rapport feature 0x52),
/// sans jamais demander l'écriture en flash. L'état est donc volatil et réappliqué à chaque lancement.
/// Protocole repris d'OpenRGB (Controllers/MSIMysticLightController, MSIMysticLight185Controller).
/// </summary>
sealed class MsiMysticLight : IDisposable
{
    /// <summary>Cartes connues : PID USB → nom. Les autres cartes MSI ont d'autres zones, on n'y touche pas.</summary>
    static readonly Dictionary<string, string> Boards = new() { ["7c56"] = "MSI B550-A PRO" };

    /// <summary>Services MSI qui réécrivent l'éclairage derrière nous.</summary>
    static readonly string[] MsiServices = { "Mystic_Light_Service", "MSI_Case_Service" };

    const byte ReportId = 0x52;
    const int PacketSize = 185;
    const int SaveFlag = 184;

    // Zones de cette carte (configuration « zones_set2 » d'OpenRGB, 6 LED intégrées). Une zone = 10 octets :
    // effet, R, G, B, vitesse|luminosité, R2, G2, B2, drapeaux couleur, bourrage.
    const int ZoneJRgb1 = 1, ZoneJRainbow1 = 31, ZoneJRainbow2 = 42, ZoneOnBoard = 74, ZoneOnBoard1 = 84;
    const int OnBoardLeds = 6;

    const byte ModeStatic = 1, BrightnessMax = 10;
    const byte FlagCustomColor = 0x80, FlagSyncJPipe = 0x30, FlagSyncJRgb = 0x80;

    readonly HidDevice _dev;
    readonly List<int> _zones = new() { ZoneJRgb1, ZoneJRainbow1, ZoneJRainbow2, ZoneOnBoard };

    public string Name { get; }

    MsiMysticLight(HidDevice dev, string name)
    {
        _dev = dev;
        Name = name;
        for (int i = 0; i < OnBoardLeds; i++) _zones.Add(ZoneOnBoard1 + 10 * i);
    }

    public static MsiMysticLight Open()
    {
        foreach (var board in Boards)
        {
            foreach (string path in HidDevice.Find($"vid_1462&pid_{board.Key}"))
            {
                HidDevice dev;
                try { dev = HidDevice.Open(path); }
                catch (Exception ex) { Log.W($"MSI : {path} : {ex.Message}"); continue; }
                if (dev.FeatureLength >= PacketSize)
                {
                    StopMsiServices();
                    return new MsiMysticLight(dev, board.Value);
                }
                dev.Dispose();
            }
        }
        throw new IOException("aucun contrôleur Mystic Light connu");
    }

    static void StopMsiServices()
    {
        foreach (string name in MsiServices)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status == ServiceControllerStatus.Stopped) continue;
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(5));
                Log.W($"MSI : service {name} arrêté (il réécrit l'éclairage)");
            }
            catch (InvalidOperationException)
            {
                // service absent, ou arrêt refusé sans droits admin
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                Log.W($"MSI : le service {name} ne s'arrête pas");
            }
        }
    }

    /// <summary>
    /// Met toutes les zones en couleur fixe (noir = éteint). Renvoie false si la carte ne confirme pas.
    /// Comme Mystic Light : l'état actuel puis le nouveau, sans jamais le drapeau de sauvegarde en flash.
    /// </summary>
    public bool SetStatic(byte r, byte g, byte b)
    {
        byte[] current = _dev.GetFeature(ReportId);
        byte[] packet = (byte[])current.Clone();
        foreach (int zone in _zones) SetZone(packet, zone, r, g, b);
        packet[ZoneOnBoard + 4] &= unchecked((byte)~FlagSyncJRgb);
        packet[ZoneOnBoard + 8] &= unchecked((byte)~FlagSyncJPipe);

        current[SaveFlag] = 0;
        packet[SaveFlag] = 0;
        _dev.SetFeature(Compat.Slice(current, 0, PacketSize));
        Thread.Sleep(20);
        _dev.SetFeature(Compat.Slice(packet, 0, PacketSize));
        Thread.Sleep(20);

        // Relecture : effet et couleur principale de nos zones seulement. Le firmware réécrit lui-même d'autres
        // octets (zone Corsair, et la seconde couleur de la 5e LED intégrée, remise à 0 sur la B550-A PRO).
        byte[] check = _dev.GetFeature(ReportId);
        var diffs = new List<string>();
        foreach (int zone in _zones)
            for (int i = zone; i < zone + 4; i++)
                if (check[i] != packet[i]) diffs.Add($"[{i}] {packet[i]:X2}→{check[i]:X2}");
        if (diffs.Count == 0) return true;
        Log.W($"{Name} : relu différent : {string.Join(", ", diffs.Take(12))}");
        return false;
    }

    static void SetZone(byte[] packet, int offset, byte r, byte g, byte b)
    {
        packet[offset] = ModeStatic;
        packet[offset + 1] = r;
        packet[offset + 2] = g;
        packet[offset + 3] = b;
        packet[offset + 4] = (byte)((packet[offset + 4] & FlagSyncJRgb) | (BrightnessMax << 2));
        packet[offset + 5] = r;
        packet[offset + 6] = g;
        packet[offset + 7] = b;
        packet[offset + 8] |= FlagCustomColor;
        packet[offset + 9] = 0;
    }

    public void Dispose() => _dev.Dispose();
}
