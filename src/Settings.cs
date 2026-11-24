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
    public bool Keyboard { get; set; } = true;
    public int KeyboardBrightness { get; set; } = 100; // %, en plus de Brightness : un clavier éclaire de près

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
                if (!File.Exists(legacy)) return FromLedControl() ?? s;
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
                    case "Keyboard": s.Keyboard = bool.Parse(value); break;
                    case "KeyboardBrightness": s.KeyboardBrightness = Compat.Clamp(int.Parse(value, CultureInfo.InvariantCulture), 0, 100); break;
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
              .AppendLine($"Mainboard={Mainboard}").AppendLine($"Ram={Ram}").AppendLine($"Commander={Commander}")
              .AppendLine($"Keyboard={Keyboard}").AppendLine($"KeyboardBrightness={KeyboardBrightness}");
            File.WriteAllText(FilePath, sb.ToString());
        }
        catch (Exception ex)
        {
            Log.W($"Impossible d'enregistrer les réglages ({ex.Message})");
        }
    }

    public Settings Clone() => (Settings)MemberwiseClone();

    /// <summary>
    /// Premier lancement sur un PC qui utilisait LED Control (fusionné ici) : on reprend sa couleur et ses luminosités
    /// (%APPDATA%\LedControl\settings.json : {"Color": "RRGGBB", "Brightness": n, "KeyboardBrightness": n}).
    /// </summary>
    static Settings FromLedControl()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LedControl", "settings.json");
            if (!File.Exists(path)) return null;
            var json = (Dictionary<string, object>)new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(File.ReadAllText(path));
            var s = new Settings();
            if (json.TryGetValue("Color", out var c) && c is string hex && hex.Length == 6)
            {
                int rgb = int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                (s.R, s.G, s.B) = ((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            }
            if (json.TryGetValue("Brightness", out var b)) s.Brightness = Compat.Clamp(Convert.ToInt32(b, CultureInfo.InvariantCulture), 0, 100);
            if (json.TryGetValue("KeyboardBrightness", out var k)) s.KeyboardBrightness = Compat.Clamp(Convert.ToInt32(k, CultureInfo.InvariantCulture), 0, 100);
            Log.W($"Réglages repris de LED Control : couleur {s.R},{s.G},{s.B}, luminosité {s.Brightness} %, clavier {s.KeyboardBrightness} %");
            return s;
        }
        catch (Exception ex)
        {
            Log.W($"Réglages de LED Control illisibles ({ex.Message})");
            return null;
        }
    }

    /// <summary>Couleur multipliée par la luminosité (et par celle du clavier, pour le clavier).</summary>
    public (byte R, byte G, byte B) ScaledColor(bool keyboard = false)
    {
        double k = Compat.Clamp(Brightness, 0, 100) / 100.0;
        if (keyboard) k *= Compat.Clamp(KeyboardBrightness, 0, 100) / 100.0;
        return ((byte)(R * k), (byte)(G * k), (byte)(B * k));
    }
}
