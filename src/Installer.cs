using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace RgbManager;

/// <summary>
/// L'exe téléchargé s'installe tout seul (une seule fenêtre admin) : copie dans C:\Program Files\RgbManager,
/// tâche de démarrage, raccourcis Bureau et menu Démarrer, entrée dans « Applications installées »,
/// et propose d'installer PawnIO s'il manque. Une mise à jour (--updated) fait la même chose sans rien demander.
/// </summary>
static class Installer
{
    const string Title = "Gestionnaire RGB";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RgbManager";
    const string Page = "https://ghermin.github.io/rgb-manager/";
    const string PawnIOPage = "https://pawnio.eu";

    static string ShortcutName => "Gestionnaire RGB.lnk";
    static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);
    static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ShortcutName);
    static string PawnIOSetup => Path.Combine(Startup.InstallDir, "PawnIO_setup.exe");

    /// <summary>À lancer en admin. <paramref name="silent"/> : mise à jour automatique, aucune fenêtre.</summary>
    public static int Install(bool silent)
    {
        bool first = !File.Exists(Startup.InstalledExe);
        Log.W($"{(first ? "Installation" : "Mise à jour")} v{Updater.Current} depuis {Application.ExecutablePath}");
        try
        {
            // Une mise à jour garde le choix « Lancer au démarrage de Windows » ; une installation l'active.
            bool startup = first || !silent || Startup.IsEnabled();
            StopRunning();
            Directory.CreateDirectory(Startup.InstallDir);
            CopySelf();
            TryDelete(Path.Combine(Startup.InstallDir, "SmbusPIIX4.bin")); // embarqué dans l'exe depuis la 1.3
            bool task = startup && Startup.Enable(Startup.InstalledExe);
            CreateShortcut(DesktopShortcut);
            CreateShortcut(StartMenuShortcut);
            RegisterUninstall();
            RefreshIcons();
            if (first && !silent && !PawnSmbus.IsInstalled()) OfferPawnIO();
            if (!task || !Startup.RunTask())
                Process.Start(new ProcessStartInfo(Startup.InstalledExe) { UseShellExecute = false });
            Log.W($"Installé dans {Startup.InstallDir}");
        }
        catch (Exception ex)
        {
            Log.W($"Installation impossible : {ex}");
            if (!silent) MessageBox.Show($"Installation impossible : {ex.Message}", Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        if (!silent)
            MessageBox.Show(first
                    ? "Gestionnaire RGB est installé.\n\nSon icône est près de l'horloge : un clic l'ouvre. Il se lance avec Windows et se met à jour tout seul."
                    : $"Gestionnaire RGB est à jour (version {Updater.Current}).",
                Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    /// <summary>Appelé par « Applications installées » (UninstallString). À lancer en admin.</summary>
    public static int Uninstall()
    {
        if (MessageBox.Show("Désinstaller Gestionnaire RGB ?\n\nPawnIO reste installé : il s'enlève à part, dans Paramètres → Applications.",
                Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return 1;
        Log.W("Désinstallation");
        StopRunning();
        Startup.Disable();
        TryDelete(DesktopShortcut);
        TryDelete(StartMenuShortcut);
        try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch { }
        // Cet exe ne peut pas s'effacer lui-même : cmd s'en charge une fois qu'il est fermé (réglages et journal compris).
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{Startup.InstallDir}\" & rmdir /s /q \"{Log.Dir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        return 0;
    }

    /// <summary>
    /// Ferme la copie installée qui tourne : d'abord proprement (le watercooling repasse en mode matériel),
    /// puis de force si elle ne répond pas (versions avant la 1.3, qui ne connaissent pas le signal).
    /// </summary>
    static void StopRunning()
    {
        if (Running().Count == 0) return;
        SingleInstance.SignalQuit();
        var until = DateTime.Now.AddSeconds(8);
        while (Running().Count > 0 && DateTime.Now < until) Thread.Sleep(200);
        Startup.EndTask();
        foreach (var p in Running())
        {
            try
            {
                p.Kill();
                p.WaitForExit(3000);
            }
            catch { }
        }
    }

    static List<Process> Running()
    {
        int self = Process.GetCurrentProcess().Id;
        var list = new List<Process>();
        foreach (var p in Process.GetProcessesByName("RgbManager"))
        {
            try
            {
                if (p.Id != self && string.Equals(p.MainModule?.FileName, Startup.InstalledExe, StringComparison.OrdinalIgnoreCase))
                    list.Add(p);
            }
            catch { }
        }
        return list;
    }

    static void CopySelf()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(Application.ExecutablePath, Startup.InstalledExe, overwrite: true);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 40)
            {
                Thread.Sleep(250); // l'ancienne version finit de se fermer, ou l'antivirus analyse le fichier
            }
        }
        // File.Copy recopie la marque « téléchargé depuis Internet » : sans elle, pas d'avertissement à chaque lancement.
        DeleteFile(Startup.InstalledExe + ":Zone.Identifier");
    }

    static void CreateShortcut(string path)
    {
        object shell = null, link = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            shell = Activator.CreateInstance(type);
            link = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            var lt = link.GetType();
            void Set(string name, string value) => lt.InvokeMember(name, BindingFlags.SetProperty, null, link, new object[] { value });
            Set("TargetPath", Startup.InstalledExe);
            Set("WorkingDirectory", Startup.InstallDir);
            Set("IconLocation", Startup.InstalledExe + ",0");
            Set("Description", "Gestionnaire RGB : carte mère, RAM, watercooling, boîtier");
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
        catch (Exception ex)
        {
            Log.W($"Raccourci {path} : {ex.Message}");
        }
        finally
        {
            if (link != null) Marshal.FinalReleaseComObject(link);
            if (shell != null) Marshal.FinalReleaseComObject(shell);
        }
    }

    static void RegisterUninstall()
    {
        using var k = Registry.LocalMachine.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "Gestionnaire RGB");
        k.SetValue("DisplayVersion", Updater.Current.ToString());
        k.SetValue("Publisher", "Ghermin");
        k.SetValue("DisplayIcon", Startup.InstalledExe);
        k.SetValue("InstallLocation", Startup.InstallDir);
        k.SetValue("URLInfoAbout", Page);
        k.SetValue("UninstallString", $"\"{Startup.InstalledExe}\" --uninstall");
        k.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(Startup.InstalledExe).Length / 1024), RegistryValueKind.DWord);
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>
    /// PawnIO (pilote signé, nécessaire seulement pour la RAM) : on télécharge son installateur officiel depuis GitHub,
    /// on vérifie sa signature, et on le lance : l'utilisateur voit et valide sa propre fenêtre.
    /// </summary>
    static void OfferPawnIO()
    {
        if (MessageBox.Show(
                "Pour piloter la RAM, l'appli a besoin de PawnIO, un pilote signé et open source (le remplaçant sûr de WinRing0). Il n'est pas installé.\n\n" +
                "Le télécharger et l'installer maintenant ?\n\nSans lui, la carte mère, le watercooling et le boîtier marchent quand même.",
                Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        try
        {
            var asset = GitHub.Latest("namazso/PawnIO.Setup", "PawnIO_setup.exe") ?? throw new IOException("installateur introuvable sur GitHub");
            GitHub.Download(asset, PawnIOSetup); // dans Program Files : personne d'autre qu'un admin ne peut le remplacer
            if (!IsSignedBy(PawnIOSetup, "CN=namazso.eu")) throw new IOException("signature de l'installateur invalide");
            using var p = Process.Start(new ProcessStartInfo(PawnIOSetup) { UseShellExecute = false });
            p.WaitForExit();
            Log.W($"PawnIO {asset.Version} : installateur terminé (code {p.ExitCode}), installé : {PawnSmbus.IsInstalled()}");
        }
        catch (Exception ex)
        {
            Log.W($"PawnIO : {ex.Message}");
            MessageBox.Show($"PawnIO n'a pas pu être installé automatiquement ({ex.Message}).\n\nSa page va s'ouvrir : installez-le, puis relancez Gestionnaire RGB.",
                Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Process.Start("explorer.exe", PawnIOPage); // ouvert sans les droits admin
        }
        finally
        {
            TryDelete(PawnIOSetup);
        }
    }

    /// <summary>Signature Authenticode valide (chaîne de confiance Windows) et émise pour <paramref name="subject"/>.</summary>
    static bool IsSignedBy(string path, string subject)
    {
        try
        {
            var signer = X509Certificate.CreateFromSignedFile(path);
            if (!signer.Subject.Split(',').Any(part => part.Trim() == subject)) return false;
        }
        catch
        {
            return false;
        }

        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,      // WTD_UI_NONE
                dwUnionChoice = 1,   // WTD_CHOICE_FILE
                pFile = pFile,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    /// <summary>Windows garde les icônes en cache : on lui demande de relire celles de l'exe et des raccourcis.</summary>
    static void RefreshIcons()
    {
        try
        {
            foreach (var path in new[] { Startup.InstalledExe, DesktopShortcut, StartMenuShortcut })
                if (File.Exists(path)) SHChangeNotifyPath(0x00002000 /* SHCNE_UPDATEITEM */, 0x0005 | 0x3000 /* PATHW, FLUSHNOWAIT */, path, IntPtr.Zero);
            string ie4uinit = Path.Combine(Environment.SystemDirectory, "ie4uinit.exe");
            if (File.Exists(ie4uinit))
                Process.Start(new ProcessStartInfo(ie4uinit, "-show") { CreateNoWindow = true, UseShellExecute = false })?.Dispose();
        }
        catch { }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool DeleteFile(string path);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHChangeNotify")]
    static extern void SHChangeNotifyPath(int eventId, uint flags, string item1, IntPtr item2);
}
