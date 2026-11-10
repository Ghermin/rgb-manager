using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RgbManager;

/// <summary>
/// Accès HID brut via les API Windows (setupapi + hid.dll), sans bibliothèque externe.
/// Les buffers lus/écrits incluent l'ID de rapport en position 0, comme le fait Windows.
/// </summary>
sealed class HidDevice : IDisposable
{
    public string DevicePath { get; }
    public int InputLength { get; }
    public int OutputLength { get; }

    readonly SafeFileHandle _handle;

    HidDevice(string path, SafeFileHandle handle, int inputLength, int outputLength)
    {
        DevicePath = path;
        _handle = handle;
        InputLength = inputLength;
        OutputLength = outputLength;
    }

    /// <summary>Chemins des interfaces HID présentes dont le chemin contient <paramref name="filter"/> (ex. "vid_0b05&amp;pid_1939&amp;mi_02").</summary>
    public static List<string> Find(string filter)
    {
        var result = new List<string>();
        filter = filter.ToLowerInvariant();
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return result;
        try
        {
            for (int i = 0; ; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref data)) break;

                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int required, IntPtr.Zero);
                if (required <= 0) continue;
                IntPtr detail = Marshal.AllocHGlobal(required);
                try
                {
                    // cbSize de SP_DEVICE_INTERFACE_DETAIL_DATA_W : 8 en 64 bits
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out _, IntPtr.Zero))
                    {
                        string path = Marshal.PtrToStringUni(detail + 4);
                        if (path != null && path.ToLowerInvariant().Contains(filter)) result.Add(path);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    public static HidDevice Open(string path)
    {
        var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Impossible d'ouvrir le périphérique HID");

        if (!HidD_GetPreparsedData(handle, out IntPtr preparsed))
        {
            int err = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(err, "HidD_GetPreparsedData a échoué");
        }
        try
        {
            HidP_GetCaps(preparsed, out HIDP_CAPS caps);
            return new HidDevice(path, handle, caps.InputReportByteLength, caps.OutputReportByteLength);
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    /// <summary>Vide les rapports d'entrée en attente.</summary>
    public void Flush() => HidD_FlushQueue(_handle);

    /// <summary>Écrit un rapport de sortie (complété par des zéros jusqu'à la taille du rapport).</summary>
    public void Write(byte[] data, int timeoutMs = 1000)
    {
        var report = new byte[OutputLength];
        Array.Copy(data, report, Math.Min(data.Length, OutputLength));
        if (!Overlapped(write: true, report, timeoutMs, out _))
            throw new IOException("Écriture HID expirée");
    }

    /// <summary>Lit un rapport d'entrée, ou null si rien n'arrive avant le délai.</summary>
    public byte[] Read(int timeoutMs = 500)
    {
        var report = new byte[InputLength];
        return Overlapped(write: false, report, timeoutMs, out int n) && n > 0 ? report : null;
    }

    bool Overlapped(bool write, byte[] buffer, int timeoutMs, out int transferred)
    {
        transferred = 0;
        IntPtr buf = Marshal.AllocHGlobal(buffer.Length);
        IntPtr ov = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        using var done = new ManualResetEvent(false);
        try
        {
            if (write) Marshal.Copy(buffer, 0, buf, buffer.Length);
            Marshal.StructureToPtr(new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() }, ov, false);

            bool ok = write
                ? WriteFile(_handle, buf, buffer.Length, IntPtr.Zero, ov)
                : ReadFile(_handle, buf, buffer.Length, IntPtr.Zero, ov);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING) throw new Win32Exception(err, write ? "WriteFile HID" : "ReadFile HID");
                if (!done.WaitOne(timeoutMs))
                {
                    CancelIoEx(_handle, ov);
                    GetOverlappedResult(_handle, ov, out _, true); // attendre l'annulation avant de libérer les buffers
                    return false;
                }
            }
            if (!GetOverlappedResult(_handle, ov, out transferred, false)) return false;
            if (!write) Marshal.Copy(buf, buffer, 0, Math.Min(transferred, buffer.Length));
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            Marshal.FreeHGlobal(ov);
        }
    }

    public void Dispose() => _handle.Dispose();

    // ---- P/Invoke ----

    const int DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    const int ERROR_IO_PENDING = 997;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                      NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                      NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr preparsed);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);
    [DllImport("hid.dll")] static extern bool HidD_FlushQueue(SafeFileHandle h);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, IntPtr buffer, int length, IntPtr read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle h, IntPtr buffer, int length, IntPtr written, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr overlapped, out int transferred, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
}
