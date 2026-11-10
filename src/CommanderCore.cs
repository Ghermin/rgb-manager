namespace RgbManager;

/// <summary>
/// Corsair iCUE Commander CORE (1B1C:0C1C) : tête de pompe du watercooling + 6 ports ventilateurs.
///
/// Les couleurs ne peuvent être envoyées qu'en « mode logiciel » : il faut les renvoyer
/// au moins toutes les 10 s, sinon le boîtier repasse en mode matériel.
/// En quittant, on le remet explicitement en mode matériel.
/// On ne touche JAMAIS aux vitesses de la pompe ou des ventilateurs : on les lit seulement,
/// pour surveiller la pompe.
/// </summary>
sealed class CommanderCore : IDisposable
{
    public const int FanPorts = 6, LedsPerFanPort = 34;

    public string Firmware { get; private set; } = "";
    public int PumpLeds { get; private set; } = 29;
    public int TotalLeds => PumpLeds + FanPorts * LedsPerFanPort;
    public bool SoftwareMode { get; private set; }

    readonly HidDevice _dev;
    readonly int _packet;

    CommanderCore(HidDevice dev)
    {
        _dev = dev;
        _packet = dev.OutputLength;
    }

    public static CommanderCore Open()
    {
        var paths = HidDevice.Find("vid_1b1c&pid_0c1c&mi_00");
        if (paths.Count == 0) throw new IOException("Commander CORE introuvable");
        var cc = new CommanderCore(HidDevice.Open(paths[0]));
        try
        {
            var res = cc.Command(0x02, 0x13);
            cc.Firmware = $"{res[3]}.{res[4]}.{res[5]}";
            Log.W($"Commander CORE : firmware {cc.Firmware}, rapports {cc._dev.OutputLength}/{cc._dev.InputLength} octets");
            return cc;
        }
        catch
        {
            cc.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Envoie une commande et renvoie la réponse « à la hidapi » (sans l'ID de rapport) :
    /// [0]=0x00, [1]=écho de la commande, [2]=statut, [3..]=données.
    /// </summary>
    byte[] Command(byte c0, byte c1, byte[] data = null, bool read = true)
    {
        var buf = new byte[_packet];
        buf[0] = 0x00; // ID de rapport
        buf[1] = 0x08;
        buf[2] = c0;
        buf[3] = c1;
        if (data != null) Array.Copy(data, 0, buf, 4, Math.Min(data.Length, _packet - 4));

        _dev.Flush();
        _dev.Write(buf);
        if (!read) return null;

        for (int i = 0; i < 20; i++)
        {
            var raw = _dev.Read(500) ?? throw new IOException($"Commander CORE : pas de réponse à {c0:X2} {c1:X2}");
            if (raw.Length > 3 && raw[1] == 0x00 && raw[2] == c0) return Compat.From(raw, 1);
        }
        throw new IOException($"Commander CORE : réponse inattendue à {c0:X2} {c1:X2}");
    }

    void OpenEndpoint(byte e0, byte e1) => Command(0x0D, 0x00, new byte[] { e0, e1 });
    void CloseEndpoint() => Command(0x05, 0x01, new byte[] { 0x00 });

    byte[] ReadEndpoint(byte e0, byte e1)
    {
        CloseEndpoint();
        OpenEndpoint(e0, e1);
        var res = Command(0x08, 0x00);
        CloseEndpoint();
        return res;
    }

    void WriteEndpoint(byte e0, byte e1, byte t0, byte t1, byte[] data)
    {
        OpenEndpoint(e0, e1);
        int index = 0;
        int first = Math.Min(data.Length, _packet - 10);
        var head = new byte[first + 6];
        int realLength = data.Length + 2;
        head[0] = (byte)(realLength & 0xFF);
        head[1] = (byte)(realLength >> 8);
        head[4] = t0;
        head[5] = t1;
        Array.Copy(data, 0, head, 6, first);
        Command(0x06, 0x00, head);
        index += first;

        while (index < data.Length)
        {
            int n = Math.Min(data.Length - index, _packet - 4);
            Command(0x07, 0x00, Compat.Slice(data, index, n));
            index += n;
        }
        CloseEndpoint();
    }

    /// <summary>Vitesse de la pompe en tr/min (lecture seule), ou -1 si illisible.</summary>
    public int ReadPumpRpm()
    {
        try
        {
            var r = ReadEndpoint(0x17, 0x00);
            if (r[3] != 0x06 || r[4] != 0x00 || r[5] < 1) return -1;
            return r[6] | r[7] << 8;
        }
        catch (Exception ex)
        {
            Log.W($"Commander CORE : lecture de la pompe impossible ({ex.Message})");
            return -1;
        }
    }

    /// <summary>Passe en mode logiciel pour pouvoir envoyer des couleurs.</summary>
    public void EnterSoftwareMode()
    {
        // Configuration des ports LED : tête de pompe + 6 ports à 34 LED (même réglage qu'OpenRGB utilisait sur ce PC).
        var portConfig = new byte[15];
        portConfig[0] = 0x07;
        portConfig[1] = 0x01;
        portConfig[2] = 0x08;
        for (int i = 3; i < 15; i += 2)
        {
            portConfig[i] = 0x01;
            portConfig[i + 1] = 0x06;
        }
        WriteEndpoint(0x1E, 0x00, 0x0D, 0x00, portConfig);

        Command(0x01, 0x03, new byte[] { 0x00, 0x02 }); // mode logiciel
        SoftwareMode = true;

        try
        {
            var r = ReadEndpoint(0x20, 0x00);
            int channels = r[5];
            if (channels > 0)
            {
                // Entrées de 4 octets à partir de r[6] : (statut u16, nombre de LED u16)
                int pump = r[9] << 8 | r[8];
                if (pump is > 0 and <= 64) PumpLeds = pump;
            }
            Log.W($"Commander CORE : {channels} canaux LED, {PumpLeds} LED sur la pompe ({Log.Hex(r, 0, 40)})");
        }
        catch (Exception ex)
        {
            Log.W($"Commander CORE : nombre de LED illisible, 29 par défaut ({ex.Message})");
        }
    }

    /// <summary>Rend la main au boîtier (mode matériel).</summary>
    public void EnterHardwareMode()
    {
        Command(0x01, 0x03, new byte[] { 0x00, 0x01 });
        SoftwareMode = false;
    }

    /// <summary>Envoie les couleurs : pompe d'abord, puis 6 ports × 34 LED. rgb = 3 octets par LED.</summary>
    public void SetColors(byte[] rgb) => WriteEndpoint(0x22, 0x00, 0x12, 0x00, rgb);

    public void Dispose() => _dev.Dispose();
}
