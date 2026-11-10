using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace RgbManager;

/// <summary>
/// Installation dans Program Files + lancement à l'ouverture de session via une tâche planifiée
/// « avec les privilèges les plus élevés » (pas de fenêtre admin à chaque démarrage).
/// </summary>
static class Startup
{
    const string TaskName = "RgbManager";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RgbManager");

    public static bool IsAdmin() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static bool IsInstalledCopy() =>
        string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), InstallDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static bool IsEnabled() => Run("schtasks.exe", $"/Query /TN \"{TaskName}\"") == 0;

    public static bool Enable(string exePath)
    {
        string user = WindowsIdentity.GetCurrent().Name;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Gestionnaire RGB (carte mère, RAM, watercooling)</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{Escape(user)}</UserId><Delay>PT5S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>{Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>"{Escape(exePath)}"</Command></Exec></Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), "RgbManager-task.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try
        {
            return Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{file}\" /F") == 0;
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static void Disable() => Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");

    /// <summary>Lance l'appli via sa tâche planifiée (droits admin sans fenêtre UAC).</summary>
    public static bool RunTask() => Run("schtasks.exe", $"/Run /TN \"{TaskName}\"") == 0;

    /// <summary>Copie l'appli dans Program Files (dossier protégé, modifiable seulement en admin) et active le démarrage auto.</summary>
    public static string Install()
    {
        string source = AppContext.BaseDirectory;
        Directory.CreateDirectory(InstallDir);
        foreach (var file in IsInstalledCopy() ? Array.Empty<string>() : Directory.GetFiles(source))
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(InstallDir, name), overwrite: true);
        }
        string exe = Path.Combine(InstallDir, "RgbManager.exe");
        Enable(exe);
        Log.W($"Installé dans {InstallDir}");
        return exe;
    }

    static string Escape(string s) => System.Security.SecurityElement.Escape(s);

    static int Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi);
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }
}
