using System.Text;

namespace RgbManager;

/// <summary>
/// Contrôleur AURA USB de la carte mère ASUS (0B05:1939, interface 2).
/// Utilise les effets matériels : la carte garde l'effet même quand l'appli est fermée.
/// Protocole documenté par le projet OpenRGB.
/// </summary>
sealed class AuraMainboard : IDisposable
{
    public const byte ModeOff = 0, ModeStatic = 1, ModeBreathing = 2, ModeSpectrumCycle = 4, ModeRainbow = 5;

    public const byte ModeDirect = 0xFF;

    /// <summary>
    /// Nombre de LED du ruban ARGB du boîtier (14, d'après l'ancien profil OpenRGB de ce PC).
    /// Ne pas surdimensionner : le contrôleur valide les couleurs sur le dernier paquet,
    /// qui est ignoré s'il vise des LED au-delà du ruban.
    /// </summary>
    public const int ArgbLeds = 14;

    public sealed class Channel
    {
        public Channel(byte effectChannel, int startLed, int ledCount, bool addressable, byte directChannel)
        {
            EffectChannel = effectChannel;
            StartLed = startLed;
            LedCount = ledCount;
            Addressable = addressable;
            DirectChannel = directChannel;
        }

        public byte EffectChannel { get; }
        public int StartLed { get; }
        public int LedCount { get; }
        public bool Addressable { get; }
        /// <summary>Canal du mode direct : 0x04 pour les LED de la carte, n° de prise pour l'ARGB.</summary>
        public byte DirectChannel { get; }
        /// <summary>Nombre de LED pilotées en mode direct.</summary>
        public int DirectLeds => Addressable ? ArgbLeds : LedCount;
    }

    public string Firmware { get; private set; } = "";
    public byte[] ConfigTable { get; private set; } = Array.Empty<byte>();
    public List<Channel> Channels { get; } = new();

    readonly HidDevice _dev;

    AuraMainboard(HidDevice dev) => _dev = dev;

    public static AuraMainboard Open(bool readOnly = false)
    {
        var paths = HidDevice.Find("vid_0b05&pid_1939&mi_02");
        if (paths.Count == 0) throw new IOException("Contrôleur AURA USB introuvable");

        var aura = new AuraMainboard(HidDevice.Open(paths[0]));
        try
        {
            aura.Init(readOnly);
            return aura;
        }
        catch
        {
            aura.Dispose();
            throw;
        }
    }

    void Init(bool readOnly)
    {
        // Version du firmware
        var r = Request(0x82, 0x02);
        Firmware = Encoding.ASCII.GetString(r, 2, 16).TrimEnd('\0', ' ');

        // Table de configuration (nombre de LED, de prises ARGB…)
        r = Request(0xB0, 0x30);
        ConfigTable = r.Skip(4).Take(60).ToArray();

        int boardLeds = ConfigTable[0x1B];
        int argbHeaders = ConfigTable[0x02];
        bool argbFromZero = Firmware.StartsWith("AULA3-AR32", StringComparison.Ordinal);

        byte effect = 0;
        int start = 0;
        if (boardLeds > 0)
        {
            Channels.Add(new Channel(argbFromZero ? (byte)argbHeaders : effect, 0, boardLeds, false, 0x04));
            start += boardLeds;
            if (!argbFromZero) effect++;
        }
        for (int i = 0; i < argbHeaders; i++)
        {
            Channels.Add(new Channel(effect, start, 1, true, (byte)i));
            start++;
            effect++;
        }

        if (!readOnly)
            Send(0xEC, 0x52, 0x53, 0x00, 0x01); // passage en mode « Gen1 », comme OpenRGB
    }

    byte[] Request(byte command, byte expectedReply)
    {
        _dev.Flush();
        Send(0xEC, command);
        for (int i = 0; i < 8; i++)
        {
            var r = _dev.Read(500);
            if (r == null) break;
            if (r[0] == 0xEC && r[1] == expectedReply) return r;
        }
        throw new IOException($"Pas de réponse AURA à la commande 0x{command:X2}");
    }

    void Send(params byte[] data) => _dev.Write(data);

    /// <summary>Applique un effet matériel à toutes les zones.</summary>
    public void SetMode(Func<Channel, byte> modeFor, byte r, byte g, byte b)
    {
        foreach (var ch in Channels)
        {
            byte mode = modeFor(ch);

            // Effet
            Send(0xEC, 0x35, ch.EffectChannel, 0x00, 0x00, mode);

            // Couleur de l'effet
            int mask = ((1 << ch.LedCount) - 1) << ch.StartLed;
            var buf = new byte[65];
            buf[0] = 0xEC;
            buf[1] = 0x36;
            buf[2] = (byte)(mask >> 8);
            buf[3] = (byte)(mask & 0xFF);
            buf[4] = 0x00;
            for (int i = 0; i < ch.LedCount; i++)
            {
                int o = 5 + 3 * (ch.StartLed + i);
                if (o + 2 >= buf.Length) break;
                buf[o] = r;
                buf[o + 1] = g;
                buf[o + 2] = b;
            }
            Send(buf);
        }
    }

    /// <summary>Met toutes les zones en mode direct : les couleurs viennent ensuite de <see cref="SendDirect"/>.</summary>
    public void EnterDirectMode()
    {
        foreach (var ch in Channels)
            Send(0xEC, 0x35, ch.EffectChannel, 0x00, 0x00, ModeDirect);
    }

    /// <summary>Envoie un paquet brut (diagnostic).</summary>
    public void SendRaw(params byte[] data) => Send(data);

    /// <summary>Envoie les couleurs d'une zone en mode direct. rgb = 3 octets par LED (ch.DirectLeds LED).</summary>
    public void SendDirect(Channel ch, byte[] rgb) => SendDirectRaw(ch.DirectChannel, rgb);

    /// <summary>Mode direct sur un canal donné (0x04 = LED de la carte, 0..n = prises ARGB).</summary>
    public void SendDirectRaw(byte directChannel, byte[] rgb)
    {
        int total = rgb.Length / 3;
        int done = 0;
        while (done < total)
        {
            int n = Math.Min(20, total - done);
            bool last = done + n >= total;
            var buf = new byte[65];
            buf[0] = 0xEC;
            buf[1] = 0x40;
            buf[2] = (byte)(directChannel | (last ? 0x80 : 0x00) | (done > 255 ? 0x10 : 0x00));
            buf[3] = (byte)(done & 0xFF);
            buf[4] = (byte)n;
            Array.Copy(rgb, done * 3, buf, 5, n * 3);
            Send(buf);
            done += n;
        }
    }

    /// <summary>Enregistre l'effet dans la mémoire de la carte (il survit au redémarrage).</summary>
    public void Commit() => Send(0xEC, 0x3F, 0x55);

    public void Dispose() => _dev.Dispose();
}
