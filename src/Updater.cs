using System.Diagnostics;

namespace RgbManager;

/// <summary>
/// Mises à jour automatiques depuis les releases GitHub (au lancement puis toutes les 6 h) : le nouvel exe est
/// téléchargé dans le dossier d'installation (modifiable seulement en admin) puis lancé avec --updated ;
/// il ferme cette version, s'installe par-dessus et se relance.
/// </summary>
static class Updater
{
    const string Repo = "Ghermin/rgb-manager";
    const string AssetName = "RgbManager.exe";

    public static Version Current => GitHub.Normalize(typeof(Updater).Assembly.GetName().Version);

    static string DownloadPath => Path.Combine(Startup.InstallDir, "RgbManager-update.exe");

    /// <summary>Seule la copie installée, lancée en admin, se met à jour : elle seule peut écrire dans Program Files.</summary>
    public static bool Enabled => Startup.IsAdmin() && Startup.IsInstalledCopy();

    /// <summary>Télécharge la nouvelle version s'il y en a une. Renvoie le chemin de l'exe prêt à lancer, sinon null.</summary>
    public static string Fetch()
    {
        try
        {
            var asset = GitHub.Latest(Repo, AssetName);
            if (asset == null || asset.Version <= Current) return null;
            Log.W($"Mise à jour : v{asset.Version} disponible (actuelle v{Current}), téléchargement");
            GitHub.Download(asset, DownloadPath);
            return DownloadPath;
        }
        catch (Exception ex)
        {
            Log.W($"Mise à jour : {ex.Message}");
            return null;
        }
    }

    public static void Apply(string exe) => Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false });

    /// <summary>Supprime l'exe de mise à jour une fois installé (il ne peut pas s'effacer lui-même).</summary>
    public static void CleanUp()
    {
        foreach (var path in new[] { DownloadPath, DownloadPath + ".part" })
        {
            try { File.Delete(path); } catch { }
        }
    }
}
