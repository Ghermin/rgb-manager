using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RgbManager;

/// <summary>
/// Accès au bus SMBus AMD (PIIX4) via le pilote PawnIO et son module signé SmbusPIIX4.bin (embarqué dans l'exe).
/// Toutes les opérations prennent le mutex SMBus global partagé par les outils de monitoring
/// (HWiNFO, LibreHardwareMonitor…), et remettent le port SMBus d'origine après usage.
/// </summary>
sealed class PawnSmbus : IDisposable
{
    const string Lib = "PawnIOLib";
    const string SmbusMutexName = @"Global\Access_SMBUS.HTP.Method";
    const int I2C_SMBUS_READ = 1, I2C_SMBUS_WRITE = 0, I2C_SMBUS_BYTE_DATA = 2, I2C_SMBUS_WORD_DATA = 3, I2C_SMBUS_BLOCK_DATA = 5;

    static PawnSmbus()
    {
        // Précharge PawnIOLib.dll depuis son dossier d'installation : les DllImport la retrouvent ensuite par son nom.
        foreach (var candidate in LibCandidates())
            if (File.Exists(candidate) && LoadLibrary(candidate) != IntPtr.Zero)
                break;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibrary(string path);

    static IEnumerable<string> LibCandidates()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll");
        yield return Path.Combine(AppContext.BaseDirectory, "PawnIOLib.dll");
    }

    /// <summary>PawnIO est-il installé ? (sa bibliothèque est présente)</summary>
    public static bool IsInstalled() => LibCandidates().Any(File.Exists);

    readonly IntPtr _handle;
    readonly Mutex _mutex;
    readonly int _port;

    PawnSmbus(IntPtr handle, int port)
    {
        _handle = handle;
        _port = port;
        _mutex = new Mutex(false, SmbusMutexName);
    }

    /// <summary>Ouvre PawnIO, charge le module PIIX4 et cible le port SMBus indiqué (0 = bus principal, celui de la RAM).</summary>
    public static PawnSmbus Open(int port = 0)
    {
        int hr;
        try
        {
            hr = pawnio_open(out IntPtr h);
            if (hr != 0) throw new Win32Exception(hr, $"PawnIO : ouverture impossible (0x{hr:X8})");
            byte[] blob = Embedded.SmbusPIIX4; // vendor\pawnio\SmbusPIIX4.bin, embarqué par build.ps1
            hr = pawnio_load(h, blob, (UIntPtr)blob.Length);
            if (hr != 0)
            {
                pawnio_close(h);
                throw new Win32Exception(hr, $"PawnIO : chargement du module impossible (0x{hr:X8})");
            }
            return new PawnSmbus(h, port);
        }
        catch (DllNotFoundException)
        {
            throw new IOException("PawnIO n'est pas installé (PawnIOLib.dll introuvable)");
        }
    }

    /// <summary>Identité du contrôleur SMBus : (nom, vendor PCI, device PCI).</summary>
    public (string Name, int Vendor, int Device) Identity()
    {
        var o = new ulong[3];
        int hr = Exec("ioctl_identity", Array.Empty<ulong>(), o);
        if (hr != 0) return ("?", 0, 0);
        var name = new string(BitConverter.GetBytes(o[0]).TakeWhile(b => b != 0).Select(b => (char)b).ToArray());
        return (name, (int)(o[2] & 0xFFFF), (int)((o[2] >> 16) & 0xFFFF));
    }

    /// <summary>
    /// Exécute <paramref name="action"/> avec le bus verrouillé et le bon port sélectionné.
    /// </summary>
    public T Locked<T>(Func<T> action)
    {
        bool owned = false;
        try
        {
            try { owned = _mutex.WaitOne(3000); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new TimeoutException("Bus SMBus occupé par un autre logiciel");

            long previous = PortSelect(_port);
            try
            {
                return action();
            }
            finally
            {
                if (previous >= 0 && previous != _port) PortSelect((int)previous);
            }
        }
        finally
        {
            if (owned) _mutex.ReleaseMutex();
        }
    }

    long PortSelect(int port)
    {
        var o = new ulong[1];
        int hr = Exec("ioctl_piix4_port_sel", new[] { unchecked((ulong)(long)port) }, o);
        if (hr != 0) throw new IOException($"PawnIO : sélection du port SMBus {port} impossible (0x{hr:X8})");
        return unchecked((long)o[0]);
    }

    /// <summary>Lecture d'un octet (SMBus « read byte data »). Retourne -1 si l'adresse ne répond pas.</summary>
    public int ReadByte(byte address, byte command)
    {
        var o = new ulong[5];
        int hr = Exec("ioctl_smbus_xfer", new ulong[] { address, I2C_SMBUS_READ, command, I2C_SMBUS_BYTE_DATA }, o);
        return hr == 0 ? (int)(o[0] & 0xFF) : -1;
    }

    /// <summary>Écriture d'un octet (SMBus « write byte data »).</summary>
    public bool WriteByte(byte address, byte command, byte value)
    {
        var o = new ulong[5];
        return Exec("ioctl_smbus_xfer", new ulong[] { address, I2C_SMBUS_WRITE, command, I2C_SMBUS_BYTE_DATA, value }, o) == 0;
    }

    /// <summary>Écriture d'un mot de 16 bits (SMBus « write word data »).</summary>
    public bool WriteWord(byte address, byte command, ushort value)
    {
        var o = new ulong[5];
        return Exec("ioctl_smbus_xfer", new ulong[] { address, I2C_SMBUS_WRITE, command, I2C_SMBUS_WORD_DATA, value }, o) == 0;
    }

    /// <summary>Écriture d'un bloc de 1 à 32 octets (SMBus « block write »).</summary>
    public bool WriteBlock(byte address, byte command, byte[] data)
    {
        if (data.Length < 1 || data.Length > 32) throw new ArgumentOutOfRangeException(nameof(data));
        // Données : longueur puis octets, rangés en little-endian dans 5 cellules de 64 bits
        var bytes = new byte[40];
        bytes[0] = (byte)data.Length;
        Array.Copy(data, 0, bytes, 1, data.Length);
        var input = new ulong[9];
        input[0] = address;
        input[1] = I2C_SMBUS_WRITE;
        input[2] = command;
        input[3] = I2C_SMBUS_BLOCK_DATA;
        for (int i = 0; i < 5; i++) input[4 + i] = BitConverter.ToUInt64(bytes, i * 8);
        return Exec("ioctl_smbus_xfer", input, new ulong[5]) == 0;
    }

    int Exec(string function, ulong[] input, ulong[] output) =>
        pawnio_execute(_handle, function, input, (UIntPtr)input.Length, output, (UIntPtr)output.Length, out _);

    public void Dispose()
    {
        pawnio_close(_handle);
        _mutex.Dispose();
    }

    [DllImport(Lib)] static extern int pawnio_open(out IntPtr handle);
    [DllImport(Lib)] static extern int pawnio_load(IntPtr handle, byte[] blob, UIntPtr size);
    [DllImport(Lib, CharSet = CharSet.Ansi)]
    static extern int pawnio_execute(IntPtr handle, string name, ulong[] input, UIntPtr inputSize,
                                     [Out] ulong[] output, UIntPtr outputSize, out UIntPtr returned);
    [DllImport(Lib)] static extern int pawnio_close(IntPtr handle);
}
