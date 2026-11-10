namespace RgbManager;

/// <summary>
/// Barrette de RAM Corsair RGB (Vengeance RGB Pro, Dominator Platinum…) sur le SMBus.
/// On utilise uniquement les effets matériels : une seule écriture, la barrette garde l'effet.
///
/// SÉCURITÉ : on ne parle QU'AUX adresses 0x58–0x5F (contrôleurs RGB Corsair).
/// Les adresses 0x50–0x57 (puces SPD, qui décrivent la RAM au BIOS) ne sont jamais touchées.
/// </summary>
sealed class CorsairDram
{
    public const byte ModeColorShift = 0x00, ModeColorPulse = 0x01, ModeRainbowWave = 0x03, ModeStatic = 0x10;

    const byte RegResetBuffer = 0x0B, RegSetBinaryData = 0x20, RegBinaryStart = 0x21, RegStatus = 0x30,
               RegGetBinaryData = 0x40, RegGetChecksum = 0x42, RegDeviceInfo = 0x61, RegWriteConfig = 0x82;
    const byte ConfigEffect = 0x01;
    const ushort CorsairVid = 0x1B1C;

    public byte Address { get; }
    public ushort Pid { get; private set; }
    public int Protocol { get; private set; }
    public string Name { get; private set; } = "Corsair DRAM";
    public string FirmwareVersion { get; private set; } = "";

    CorsairDram(byte address) => Address = address;

    static void Guard(byte address)
    {
        if (address < 0x58 || address > 0x5F)
            throw new InvalidOperationException($"Adresse SMBus 0x{address:X2} interdite");
    }

    public static List<CorsairDram> Detect(PawnSmbus bus)
    {
        var found = new List<CorsairDram>();
        for (byte addr = 0x58; addr <= 0x5F; addr++)
        {
            byte a = addr;
            var dram = bus.Locked(() =>
            {
                int r = bus.ReadByte(a, 0x43);
                if (r != 0x1A && r != 0x1B && r != 0x1C) return null;
                r = bus.ReadByte(a, 0x44);
                if (r != 0x01 && r != 0x03 && r != 0x04) return null;

                var d = new CorsairDram(a);
                return d.ReadInfo(bus) ? d : null;
            });
            if (dram != null)
            {
                found.Add(dram);
                Log.W($"RAM : {dram.Name} à 0x{dram.Address:X2}, PID 0x{dram.Pid:X4}, protocole {dram.Protocol}, firmware {dram.FirmwareVersion}");
            }
            Thread.Sleep(10);
        }
        return found;
    }

    bool ReadInfo(PawnSmbus bus)
    {
        Guard(Address);
        bus.WriteByte(Address, RegDeviceInfo, 0x00);
        bus.WriteByte(Address, RegBinaryStart, 0x00);
        var info = new byte[32];
        for (int i = 0; i < info.Length; i++)
        {
            int v = bus.ReadByte(Address, RegGetBinaryData);
            if (v < 0) return false;
            info[i] = (byte)v;
        }
        int deviceCrc = bus.ReadByte(Address, RegGetChecksum);
        if (deviceCrc != Crc8(info))
        {
            Log.W($"RAM 0x{Address:X2} : CRC des infos incorrect ({Log.Hex(info, 0, 32)})");
            return false;
        }

        ushort vid = (ushort)(info[1] << 8 | info[0]);
        if (vid != CorsairVid)
        {
            Log.W($"RAM 0x{Address:X2} : VID 0x{vid:X4} inattendu, ignorée");
            return false;
        }
        Pid = (ushort)(info[3] << 8 | info[2]);
        Protocol = info[28];
        FirmwareVersion = $"{info[9]}.{info[8]}.{info[11] << 8 | info[10]}";
        Name = (Pid & 0xFF00) switch
        {
            0x0100 => "Corsair Vengeance RGB Pro",
            0x0200 => "Corsair Dominator Platinum RGB",
            0x0300 => "Corsair Vengeance RGB Pro SL",
            0x0400 => "Corsair Vengeance RGB RS",
            _ => $"Corsair RGB (PID 0x{Pid:X4})",
        };
        return true;
    }

    /// <summary>Applique un effet matériel. speed : 0 lent, 1 moyen, 2 rapide. brightness : 0–255.</summary>
    public bool SetEffect(PawnSmbus bus, byte mode, byte speed, bool randomColors, byte brightness,
                          (byte R, byte G, byte B) color1, (byte R, byte G, byte B) color2)
    {
        var effect = new byte[20];
        effect[0] = mode;
        effect[1] = speed;
        effect[2] = (byte)(randomColors ? 0x00 : 0x01);
        effect[3] = 0x01; // direction : vers le bas
        effect[4] = color1.R; effect[5] = color1.G; effect[6] = color1.B; effect[7] = brightness;
        effect[8] = color2.R; effect[9] = color2.G; effect[10] = color2.B; effect[11] = brightness;

        return bus.Locked(() =>
        {
            Guard(Address);
            bus.WriteByte(Address, RegResetBuffer, 0x00);
            bus.WriteByte(Address, RegBinaryStart, 0x00);
            foreach (byte b in effect) bus.WriteByte(Address, RegSetBinaryData, b);

            int deviceCrc = bus.ReadByte(Address, RegGetChecksum);
            if (deviceCrc != Crc8(effect))
            {
                Log.W($"RAM 0x{Address:X2} : CRC de l'effet incorrect, effet non appliqué");
                return false;
            }
            bus.WriteByte(Address, RegWriteConfig, ConfigEffect);

            for (int i = 0; i < 10; i++) // attendre que la barrette ait fini
            {
                int status = bus.ReadByte(Address, RegStatus);
                if (status >= 0 && (status & 0x08) == 0) break;
                Thread.Sleep(10);
            }
            return true;
        });
    }

    /// <summary>Nombre de LED de la barrette (12 sur les Dominator, 6 sur les RS, 10 sinon).</summary>
    public int LedCount => (Pid & 0xFF00) switch { 0x0200 or 0x0600 or 0x0800 => 12, 0x0400 or 0x0B00 => 6, _ => 10 };

    /// <summary>Les Dominator numérotent leurs LED à l'envers.</summary>
    bool Reversed => (Pid & 0xFF00) is 0x0200 or 0x0600 or 0x0800;

    bool _blockWriteFailed;

    /// <summary>
    /// Mode direct (protocole 4+) : couleurs LED par LED, affichées immédiatement.
    /// Paquet : [nombre de LED][R G B × n][CRC-8], envoyé en blocs sur les registres 0x31 puis 0x32.
    /// </summary>
    public bool SetDirect(PawnSmbus bus, byte[] rgb)
    {
        if (Protocol < 4) return false;
        int n = LedCount;
        var packet = new byte[n * 3 + 2];
        packet[0] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            int src = (Reversed ? n - 1 - i : i) * 3;
            if (src + 2 < rgb.Length) Array.Copy(rgb, src, packet, 1 + i * 3, 3);
        }
        packet[packet.Length - 1] = (byte)Crc8(Compat.Slice(packet, 0, packet.Length - 1));

        return bus.Locked(() =>
        {
            Guard(Address);
            if (!_blockWriteFailed)
            {
                bool ok = bus.WriteBlock(Address, 0x31, Compat.Slice(packet, 0, Math.Min(32, packet.Length)));
                if (ok && packet.Length > 32) ok = bus.WriteBlock(Address, 0x32, Compat.From(packet, 32));
                if (ok) return true;
                _blockWriteFailed = true;
                Log.W($"RAM 0x{Address:X2} : écriture par blocs refusée, passage aux écritures par mots");
            }

            // Repli : données découpées en mots de 16 bits, certains octets glissés dans le registre
            int idx = 0;
            bool all = true;
            while (idx < packet.Length)
            {
                byte reg0 = (byte)(idx == 0 ? 0x90 : 0xA0), reg1 = 0;
                ushort w0 = packet[idx++], w1 = 0;
                if (idx < packet.Length) w0 |= (ushort)(packet[idx++] << 8);
                if (idx < packet.Length)
                {
                    reg1 = 0xA0;
                    reg0 |= (byte)(packet[idx] & 0x0F);
                    reg1 |= (byte)((packet[idx] & 0xF0) >> 4);
                    idx++;
                }
                if (idx < packet.Length) w1 = packet[idx++];
                if (idx < packet.Length) w1 |= (ushort)(packet[idx++] << 8);
                all &= bus.WriteWord(Address, reg0, w0);
                if (reg1 > 0) all &= bus.WriteWord(Address, reg1, w1);
            }
            return all;
        });
    }

    /// <summary>CRC-8 (polynôme 0x07, init 0), celui du SMBus.</summary>
    static int Crc8(byte[] data)
    {
        byte crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
        }
        return crc;
    }
}
