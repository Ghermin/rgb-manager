using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace RgbManager;

/// <summary>
/// Dossier d'installation (Program Files, voir Installer) + lancement à l'ouverture de session via une tâche planifiée
/// « avec les privilèges les plus élevés » (pas de fenêtre admin à chaque démarrage).
/// </summary>
static class Startup
{
    const string TaskName = "RgbManager";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RgbManager");

    public static string InstalledExe => Path.Combine(InstallDir, "RgbManager.exe");

    public static bool IsAdmin() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Cet exe est-il celui de Program Files ? (pas l'exe téléchargé, ni celui de mise à jour posé à côté)</summary>
    public static bool IsInstalledCopy() =>
        string.Equals(Path.GetFullPath(Application.ExecutablePath), InstalledExe, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Arrête l'appli lancée par la tâche planifiée.</summary>
    public static void EndTask() => Run("schtasks.exe", $"/End /TN \"{TaskName}\"");

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
