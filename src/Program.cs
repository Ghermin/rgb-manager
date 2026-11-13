using System.ComponentModel;
using System.Diagnostics;

namespace RgbManager;

static class Program
{
    /// <summary>
    /// Sans argument : icône dans la barre des tâches.
    /// --test N   : applique les réglages pendant N secondes puis quitte (rend le watercooling au mode matériel).
    /// Exe téléchargé (hors de Program Files), sans option : il s'installe puis se lance de là (voir Installer).
    /// --updated  : idem sans aucune fenêtre (mise à jour automatique) ; --install : ancien nom, même effet.
    /// --portable : ne pas s'installer, tourner depuis ici.
    /// --uninstall : désinstalle (lancé par « Applications installées »).
    /// --no-elevate : ne pas redemander les droits admin.
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        bool admin = Startup.IsAdmin();
        bool canElevate = !args.Contains("--no-elevate");

        if (args.Contains("--uninstall"))
        {
            if (!admin) return canElevate && Elevate(args) ? 0 : 1;
            return Installer.Uninstall();
        }

        if (args.All(a => a is "--no-elevate" or "--updated" or "--install") && !Startup.IsInstalledCopy())
        {
            bool silent = args.Contains("--updated");
            if (admin) return Installer.Install(silent);
            if (canElevate && Elevate(args)) return 0;
            if (!silent)
                MessageBox.Show("L'installation a besoin des droits administrateur, une seule fois : relancez RgbManager.exe et acceptez la fenêtre Windows.",
                    "Gestionnaire RGB", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }

        bool trayMode = args.All(a => a == "--no-elevate");

        // Déjà lancée : on lui demande d'ouvrir son menu, sans rien relancer
        if (trayMode && SingleInstance.SignalExisting()) return 0;

        // Lancement normal (raccourci) : passer par la tâche planifiée, qui a déjà les droits admin → pas de fenêtre UAC
        if (trayMode && !admin && canElevate && Startup.IsEnabled() && Startup.RunTask()) return 0;

        // Fenêtre admin refusée : on continue sans la RAM.
        if (!admin && canElevate && Elevate(args)) return 0;

        Log.W($"--- Démarrage v{typeof(Program).Assembly.GetName().Version} (admin : {admin}) {string.Join(" ", args)}");
        Log.W($"    utilisateur {System.Security.Principal.WindowsIdentity.GetCurrent().Name}, 64 bits {Environment.Is64BitProcess}, exe {Application.ExecutablePath}, dossier courant {Environment.CurrentDirectory}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.W($"ERREUR FATALE : {e.ExceptionObject}");
        Application.ThreadException += (_, e) => Log.W($"ERREUR interface : {e.Exception}");

        int count = Array.IndexOf(args, "--argb-count");
        if (count >= 0)
        {
            // LED 1–14 en bleu, puis bandes de 10 LED de couleurs différentes jusqu'à 60
            using var aura = AuraMainboard.Open();
            for (byte ch = 0; ch < 4; ch++) aura.SendRaw(0xEC, 0x35, ch, 0x00, 0x00, AuraMainboard.ModeDirect);
            var bands = new (byte, byte, byte)[] { (255, 0, 0), (0, 255, 0), (255, 200, 0), (255, 0, 255), (255, 255, 255) };
            var rgb = new byte[60 * 3];
            for (int k = 0; k < 60; k++)
            {
                var c = k < 14 ? ((byte)0, (byte)0, (byte)255) : bands[Math.Min((k - 14) / 10, bands.Length - 1)];
                rgb[k * 3] = c.Item1; rgb[k * 3 + 1] = c.Item2; rgb[k * 3 + 2] = c.Item3;
            }
            var end = DateTime.Now.AddSeconds(240);
            while (DateTime.Now < end) { aura.SendDirectRaw(0x00, rgb); Thread.Sleep(1000); }
            return 0;
        }

        int probe2 = Array.IndexOf(args, "--aura-probe2");
        if (probe2 >= 0)
        {
            int seconds = probe2 + 1 < args.Length && int.TryParse(args[probe2 + 1], out int ps2) ? ps2 : 180;
            AuraProbe2(seconds);
            return 0;
        }

        int probe = Array.IndexOf(args, "--aura-probe");
        if (probe >= 0)
        {
            int seconds = probe + 1 < args.Length && int.TryParse(args[probe + 1], out int ps) ? ps : 600;
            AuraProbe(seconds);
            return 0;
        }

        int i = Array.IndexOf(args, "--test");
        if (i >= 0)
        {
            int seconds = i + 1 < args.Length && int.TryParse(args[i + 1], out int n) ? n : 20;
            var engine = new Engine(Settings.Load(), admin);
            engine.Start();
            Thread.Sleep(seconds * 1000);
            engine.Stop();
            Log.W("--- Fin du test");
            return 0;
        }

        return RunTray(admin);
    }

    /// <summary>Se relance en admin (fenêtre UAC) avec les mêmes options. Renvoie false si la fenêtre est refusée.</summary>
    static bool Elevate(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Application.ExecutablePath, string.Join(" ", args.Append("--no-elevate").Select(a => $"\"{a}\"")))
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Test d'identification : chaque sortie de la carte mère reçoit une couleur différente.
    /// Prise RGB 1 = rouge, prise RGB 2 = vert, ARGB n°1 = bleu, ARGB n°2 = jaune, ARGB n°3 = magenta.
    /// </summary>
    static void AuraProbe(int seconds)
    {
        using var aura = AuraMainboard.Open();
        Log.W($"Test AURA : firmware {aura.Firmware}, {seconds} s");
        for (byte ch = 0; ch < 4; ch++) aura.SendRaw(0xEC, 0x35, ch, 0x00, 0x00, AuraMainboard.ModeDirect);

        byte[] Fill(int n, byte r, byte g, byte b)
        {
            var buf = new byte[n * 3];
            for (int k = 0; k < n; k++) { buf[k * 3] = r; buf[k * 3 + 1] = g; buf[k * 3 + 2] = b; }
            return buf;
        }

        var board = new byte[] { 255, 0, 0, 0, 255, 0 };
        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end)
        {
            aura.SendDirectRaw(0x04, board);
            aura.SendDirectRaw(0x00, Fill(14, 0, 0, 255));
            aura.SendDirectRaw(0x01, Fill(14, 255, 200, 0));
            aura.SendDirectRaw(0x02, Fill(14, 255, 0, 255));
            Thread.Sleep(1000);
        }
        Log.W("Test AURA : fin");
    }

    /// <summary>
    /// Test des 3 façons de piloter la carte mère, en boucle : 15 s chacune.
    /// Bleu = mode direct, rouge = effet matériel statique, arc-en-ciel = effet matériel animé.
    /// </summary>
    static void AuraProbe2(int seconds)
    {
        using var aura = AuraMainboard.Open();
        Log.W($"Test AURA 2 : firmware {aura.Firmware}, {seconds} s");

        byte[] Fill(int n, byte r, byte g, byte b)
        {
            var buf = new byte[n * 3];
            for (int k = 0; k < n; k++) { buf[k * 3] = r; buf[k * 3 + 1] = g; buf[k * 3 + 2] = b; }
            return buf;
        }

        void Effect(byte mode, byte r, byte g, byte b)
        {
            for (byte ch = 0; ch < 4; ch++) aura.SendRaw(0xEC, 0x35, ch, 0x00, 0x00, mode);
            var color = new byte[65];
            color[0] = 0xEC; color[1] = 0x36; color[2] = 0xFF; color[3] = 0xFF; color[4] = 0x00;
            Array.Copy(Fill(16, r, g, b), 0, color, 5, 48);
            aura.SendRaw(color);
        }

        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end)
        {
            Log.W("Test AURA 2 : BLEU = mode direct");
            for (byte ch = 0; ch < 4; ch++) aura.SendRaw(0xEC, 0x35, ch, 0x00, 0x00, AuraMainboard.ModeDirect);
            for (int s = 0; s < 15 && DateTime.Now < end; s++)
            {
                aura.SendDirectRaw(0x04, Fill(2, 0, 0, 255));
                for (byte d = 0; d < 4; d++) aura.SendDirectRaw(d, Fill(14, 0, 0, 255));
                Thread.Sleep(1000);
            }

            if (DateTime.Now >= end) break;
            Log.W("Test AURA 2 : ROUGE = effet statique");
            Effect(AuraMainboard.ModeStatic, 255, 0, 0);
            Thread.Sleep(15000);

            if (DateTime.Now >= end) break;
            Log.W("Test AURA 2 : ARC-EN-CIEL = effet animé");
            Effect(AuraMainboard.ModeSpectrumCycle, 255, 255, 255);
            for (byte ch = 0; ch < 4; ch++) aura.SendRaw(0xEC, 0x35, ch, 0x00, 0x00, AuraMainboard.ModeRainbow);
            Thread.Sleep(15000);
        }
        Log.W("Test AURA 2 : fin");
    }

    static int RunTray(bool admin)
    {
        using var single = new Mutex(true, @"Local\RgbManager.Instance", out bool first);
        if (!first) return 0;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp(admin));
        return 0;
    }
}
