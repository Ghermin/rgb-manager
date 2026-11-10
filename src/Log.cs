namespace RgbManager;

/// <summary>
/// Journal texte dans C:\ProgramData\RgbManager\log.txt (indépendant du profil utilisateur).
/// Si l'écriture échoue, la ligne et l'erreur partent dans %TEMP%\RgbManager-log.txt.
/// </summary>
static class Log
{
    static readonly object Gate = new();

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RgbManager");

    /// <summary>Ancien emplacement (%APPDATA%\RgbManager), lu une fois pour récupérer les réglages.</summary>
    public static string LegacyDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RgbManager");

    public static string FilePath => Path.Combine(Dir, "log.txt");

    public static void W(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var sw = new StreamWriter(fs);
                sw.Write(line);
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "RgbManager-log.txt"),
                        $"{line}    (journal principal inaccessible : {ex.GetType().Name} : {ex.Message}){Environment.NewLine}");
                }
                catch
                {
                    // Plus aucun endroit où écrire : on abandonne cette ligne.
                }
            }
        }
    }

    public static string Hex(byte[] data, int offset, int count) =>
        BitConverter.ToString(data, offset, Math.Min(count, data.Length - offset)).Replace('-', ' ');
}
