using System.Globalization;
using System.Text;

namespace RgbManager;

enum Effect { Static, Breathing, Rainbow, Off }

/// <summary>Réglages sauvegardés dans %APPDATA%\RgbManager\settings.ini (format clé=valeur).</summary>
sealed class Settings
{
    public byte R { get; set; } = 255;
    public byte G { get; set; } = 255;
    public byte B { get; set; } = 255;
    public Effect Effect { get; set; } = Effect.Static;
    public int Brightness { get; set; } = 100; // %
    public int Speed { get; set; } = 1;        // 0 lent, 1 moyen, 2 rapide
    public bool Mainboard { get; set; } = true;
    public bool Ram { get; set; } = true;
    public bool Commander { get; set; } = true;

    static string FilePath => Path.Combine(Log.Dir, "settings.ini");

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            string path = FilePath;
            if (!File.Exists(path))
            {
                // Récupère les réglages de l'ancien emplacement (%APPDATA%)
                string legacy = Path.Combine(Log.LegacyDir, "settings.ini");
                if (!File.Exists(legacy)) return s;
                path = legacy;
                Log.W($"Réglages repris depuis {legacy}");
            }
            foreach (var line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "R": s.R = byte.Parse(value, CultureInfo.InvariantCulture); break;
                    case "G": s.G = byte.Parse(value, CultureInfo.InvariantCulture); break;
                    case "B": s.B = byte.Parse(value, CultureInfo.InvariantCulture); break;
                    case "Effect": if (Enum.TryParse(value, out Effect e)) s.Effect = e; break;
                    case "Brightness": s.Brightness = Compat.Clamp(int.Parse(value, CultureInfo.InvariantCulture), 0, 100); break;
                    case "Speed": s.Speed = Compat.Clamp(int.Parse(value, CultureInfo.InvariantCulture), 0, 2); break;
                    case "Mainboard": s.Mainboard = bool.Parse(value); break;
                    case "Ram": s.Ram = bool.Parse(value); break;
                    case "Commander": s.Commander = bool.Parse(value); break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.W($"Réglages illisibles, valeurs par défaut ({ex.Message})");
            return new Settings();
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            var sb = new StringBuilder();
            sb.AppendLine($"R={R}").AppendLine($"G={G}").AppendLine($"B={B}")
              .AppendLine($"Effect={Effect}").AppendLine($"Brightness={Brightness}").AppendLine($"Speed={Speed}")
              .AppendLine($"Mainboard={Mainboard}").AppendLine($"Ram={Ram}").AppendLine($"Commander={Commander}");
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch (Exception ex)
        {
            Log.W($"Impossible d'enregistrer les réglages ({ex.Message})");
        }
    }

    public Settings Clone() => (Settings)MemberwiseClone();

    /// <summary>Couleur multipliée par la luminosité.</summary>
    public (byte R, byte G, byte B) ScaledColor()
    {
        double k = Compat.Clamp(Brightness, 0, 100) / 100.0;
        return ((byte)(R * k), (byte)(G * k), (byte)(B * k));
    }
}
