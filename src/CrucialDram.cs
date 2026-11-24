using System.Management;
using System.Text;

namespace RgbManager;

/// <summary>
/// Barrette Crucial Ballistix RGB (contrôleur Micron) sur le SMBus. Couleur fixe uniquement : chaque LED est
/// réglée par une suite de registres, trop lente pour animer. Protocole repris d'OpenRGB (Controllers/CrucialController).
///
/// SÉCURITÉ : on ne parle qu'aux adresses des contrôleurs Crucial (0x27 au démarrage à froid, puis 0x20–0x23
/// et 0x39–0x3C), jamais aux puces SPD (0x50–0x57). Et seulement si Windows annonce de la RAM Ballistix :
/// sur un autre PC, ces adresses ne sont même pas lues.
/// </summary>
sealed class CrucialDram
{
    /// <summary>Au démarrage à froid, toutes les barrettes répondent en 0x27 et doivent être réadressées.</summary>
    const byte RemapAddress = 0x27;
    static readonly byte[] Addresses = { 0x39, 0x3A, 0x3B, 0x3C, 0x20, 0x21, 0x22, 0x23 };

    const ushort RegMicronCheck1 = 0x1025, RegMicronCheck2 = 0x1030;
    const ushort RegMode = 0x820F, RegLedMask = 0x82E9, RegCommand = 0x82F0;
    const byte ModeStatic = 0xCF, EffectSpeed = 0x10;
    const int Leds = 8;

    public byte Address { get; }

    CrucialDram(byte address) => Address = address;

    static void Guard(byte address)
    {
        if (address != RemapAddress && Array.IndexOf(Addresses, address) < 0)
            throw new InvalidOperationException($"Adresse SMBus 0x{address:X2} interdite");
    }

    /// <summary>De la RAM Crucial Ballistix est-elle installée ? (références « BL… », lues par WMI, sans toucher au bus)</summary>
    public static bool Installed()
    {
        try
        {
            using var search = new ManagementObjectSearcher("SELECT PartNumber FROM Win32_PhysicalMemory");
            foreach (ManagementBaseObject o in search.Get())
            {
                using (o)
                {
                    if ((o["PartNumber"] as string ?? "").Trim().StartsWith("BL", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.W($"RAM : lecture WMI impossible ({ex.Message})");
        }
        return false;
    }

    public static List<CrucialDram> Detect(PawnSmbus bus)
    {
        var found = new List<CrucialDram>();
        bus.Locked(() =>
        {
            Remap(bus);
            foreach (byte addr in Addresses)
            {
                if (IsCrucial(bus, addr))
                {
                    found.Add(new CrucialDram(addr));
                    Log.W($"RAM : Crucial Ballistix RGB à 0x{addr:X2}");
                }
                Thread.Sleep(1);
            }
            return 0;
        });
        return found;
    }

    /// <summary>Barrettes encore en 0x27 (démarrage à froid) : chacune reçoit une adresse libre.</summary>
    static void Remap(PawnSmbus bus)
    {
        // Comme OpenRGB et LED Control : pas de signature à vérifier en 0x27 (pas sûr qu'elle réponde avant
        // le réadressage). Detect() n'est appelée que si de la RAM Ballistix est installée (Installed()).
        if (bus.ReadByte(RemapAddress) < 0) return;
        int idx = 0;
        for (int slot = 0; slot < 4; slot++)
        {
            if (bus.ReadByte(RemapAddress) < 0) break;
            while (idx < Addresses.Length && bus.ReadByte(Addresses[idx]) >= 0) idx++;
            if (idx >= Addresses.Length) break;
            RegisterWrite(bus, RemapAddress, 0x82EE, (byte)slot);
            RegisterWrite(bus, RemapAddress, 0x82EF, (byte)(Addresses[idx] << 1));
            RegisterWrite(bus, RemapAddress, 0x82F0, 0xF0);
            Log.W($"RAM : barrette {slot} réadressée en 0x{Addresses[idx]:X2}");
            Thread.Sleep(1);
        }
    }

    /// <summary>Les contrôleurs Micron renvoient 0, 1, 2… 15 aux registres 0xA0–0xAF.</summary>
    static bool HasSignature(PawnSmbus bus, byte addr)
    {
        for (int i = 0; i < 16; i++)
            if (bus.ReadByte(addr, (byte)(0xA0 + i)) != i) return false;
        return true;
    }

    static bool IsCrucial(PawnSmbus bus, byte addr)
    {
        if (bus.ReadByte(addr) < 0 || !HasSignature(bus, addr)) return false;
        return ReadString(bus, addr, RegMicronCheck1).StartsWith("Micron") || ReadString(bus, addr, RegMicronCheck2).StartsWith("Micron");
    }

    /// <summary>Met les 8 LED de la barrette dans une couleur fixe (noir = éteint).</summary>
    public void SetStatic(PawnSmbus bus, byte r, byte g, byte b)
    {
        bus.Locked(() =>
        {
            SetMode(bus, ModeStatic);
            for (int led = 0; led < Leds; led++)
            {
                RegisterWrite(bus, Address, RegLedMask, (byte)(1 << led));
                RegisterWrite(bus, Address, 0x82EA, 0);
                RegisterWrite(bus, Address, 0x82EB, 0);
                RegisterWrite(bus, Address, 0x82EC, 0);
                RegisterWrite(bus, Address, 0x82ED, r);
                RegisterWrite(bus, Address, 0x82EE, g);
                RegisterWrite(bus, Address, 0x82EF, b);
                RunCommand(bus, Address, 0x01);
            }
            SetMode(bus, ModeStatic);
            return 0;
        });
    }

    void SetMode(PawnSmbus bus, byte mode)
    {
        RegisterWrite(bus, Address, RegMode, mode);
        RegisterWrite(bus, Address, 0x82EE, 0x00);
        RegisterWrite(bus, Address, 0x82EF, EffectSpeed);
        RunCommand(bus, Address, 0x84);
    }

    /// <summary>
    /// Les registres 0x82E9–0x82EF sont les paramètres de la commande écrite en 0x82F0 :
    /// on attend que le contrôleur l'ait traitée avant d'en préparer une autre.
    /// </summary>
    static void RunCommand(PawnSmbus bus, byte addr, byte command)
    {
        RegisterWrite(bus, addr, RegCommand, command);
        for (int waited = 0; waited < 100; waited++)
        {
            if (RegisterRead(bus, addr, RegCommand) != command) return;
            Thread.Sleep(1);
        }
    }

    static string ReadString(PawnSmbus bus, byte addr, ushort reg)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 16; i++)
        {
            int c = RegisterRead(bus, addr, (ushort)(reg + i));
            if (c <= 0) break;
            sb.Append((char)c);
        }
        return sb.ToString();
    }

    // Les registres internes 16 bits se sélectionnent par un mot (octets inversés) sur la commande 0x00,
    // puis se lisent en 0x81 ou s'écrivent en 0x01.
    static ushort Select(ushort reg) => (ushort)((reg << 8) | (reg >> 8));

    static int RegisterRead(PawnSmbus bus, byte addr, ushort reg)
    {
        Guard(addr);
        if (!Retry(() => bus.WriteWord(addr, 0x00, Select(reg)))) return -1;
        return bus.ReadByte(addr, 0x81);
    }

    static void RegisterWrite(PawnSmbus bus, byte addr, ushort reg, byte value)
    {
        Guard(addr);
        if (!Retry(() => bus.WriteWord(addr, 0x00, Select(reg))) || !Retry(() => bus.WriteByte(addr, 0x01, value)))
            throw new IOException($"écriture du registre 0x{reg:X4} en 0x{addr:X2} refusée");
    }

    static bool Retry(Func<bool> write)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (write()) return true;
            Thread.Sleep(2);
        }
        return false;
    }
}
