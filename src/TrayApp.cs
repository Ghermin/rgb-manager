using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RgbManager;

/// <summary>Icône dans la zone de notification avec un menu clic droit.</summary>
sealed class TrayApp : ApplicationContext
{
    const string Playground = "https://ghermin.github.io/";

    static readonly (string Name, byte R, byte G, byte B)[] Presets =
    {
        ("Blanc", 255, 255, 255), ("Rouge", 255, 0, 0), ("Orange", 255, 90, 0), ("Jaune", 255, 190, 0),
        ("Vert", 0, 255, 0), ("Cyan", 0, 255, 255), ("Bleu", 0, 0, 255), ("Violet", 150, 0, 255), ("Rose", 255, 0, 120),
    };

    readonly Settings _settings;
    readonly Engine _engine;
    readonly NotifyIcon _tray;
    readonly Control _ui;
    readonly ContextMenuStrip _menu = new();
    readonly MenuTheme _theme = new();
    SwatchRow _swatches;
    readonly ToolStripMenuItem _statusAura = new() { Enabled = false };
    readonly ToolStripMenuItem _statusRam = new() { Enabled = false };
    readonly ToolStripMenuItem _statusCc = new() { Enabled = false };
    IntPtr _iconHandle;
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 2 * 60 * 1000 };
    bool _checkingUpdate;

    public TrayApp(bool admin)
    {
        _ui = new Control();
        _ui.CreateControl();

        _settings = Settings.Load();
        _engine = new Engine(_settings, admin);
        _engine.StatusChanged += () => _ui.BeginInvoke(RefreshMenu);
        _engine.Warning += msg => _ui.BeginInvoke(() => _tray.ShowBalloonTip(8000, "Gestionnaire RGB", msg, ToolTipIcon.Warning));

        _tray = new NotifyIcon { Visible = true, ContextMenuStrip = _menu };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowMenu();
        };

        BuildMenu();
        RefreshMenu();

        // Relancer l'appli (raccourci du bureau) ouvre ce menu
        var showEvent = SingleInstance.CreateShowEvent();
        new Thread(() =>
        {
            while (showEvent.WaitOne()) _ui.BeginInvoke(ShowMenu);
        }) { IsBackground = true, Name = "Raccourci" }.Start();

        // L'installateur d'une nouvelle version demande la fermeture : on rend la main proprement au matériel
        var quitEvent = SingleInstance.CreateQuitEvent();
        new Thread(() =>
        {
            quitEvent.WaitOne();
            Log.W("Fermeture demandée par l'installateur");
            _ui.BeginInvoke(ExitThread);
        }) { IsBackground = true, Name = "Fermeture" }.Start();

        // Mises à jour automatiques : 2 min après le lancement, puis toutes les 6 h
        if (Updater.Enabled)
        {
            _updateTimer.Tick += (_, _) => CheckForUpdate();
            _updateTimer.Start();
        }

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnding += OnSessionEnding;

        _engine.Start();
        if (!admin)
            _tray.ShowBalloonTip(6000, "Gestionnaire RGB", "Lancé sans droits admin : la RAM ne sera pas pilotée.", ToolTipIcon.Info);
    }

    void CheckForUpdate()
    {
        _updateTimer.Interval = 6 * 3600 * 1000;
        if (_checkingUpdate) return;
        _checkingUpdate = true;
        new Thread(() =>
        {
            Updater.CleanUp(); // exe de la mise à jour précédente
            string exe = Updater.Fetch();
            _ui.BeginInvoke(() =>
            {
                _checkingUpdate = false;
                if (exe == null) return;
                try
                {
                    Updater.Apply(exe); // il attend notre fermeture, s'installe et relance l'appli
                    ExitThread();
                }
                catch (Exception ex)
                {
                    Log.W($"Mise à jour : lancement impossible : {ex.Message}");
                }
            });
        }) { IsBackground = true, Name = "Mise à jour" }.Start();
    }

    void ShowMenu() =>
        typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(_tray, null);

    void BuildMenu()
    {
        var title = new ToolStripMenuItem("Gestionnaire RGB") { Enabled = false, Font = MenuTheme.TitleFont };
        _menu.Items.Add(title);
        _menu.Items.Add(new ToolStripSeparator());

        _swatches = new SwatchRow(Presets, _theme)
        {
            Current = () => _settings.Effect == Effect.Rainbow ? -1 :
                Array.FindIndex(Presets, p => p.R == _settings.R && p.G == _settings.G && p.B == _settings.B),
        };
        _swatches.Picked += i =>
        {
            var p = Presets[i];
            Change(s => { s.R = p.R; s.G = p.G; s.B = p.B; if (s.Effect is Effect.Off or Effect.Rainbow) s.Effect = Effect.Static; });
        };
        _menu.Items.Add(_swatches);
        var custom = new ToolStripMenuItem("Couleur personnalisée…");
        custom.Click += (_, _) => PickColor();
        _menu.Items.Add(custom);
        _menu.Items.Add(new ToolStripSeparator());

        _menu.Items.Add(Submenu("Effet",
            ("Statique", Effect.Static), ("Respiration", Effect.Breathing), ("Arc-en-ciel", Effect.Rainbow), ("Éteint", Effect.Off)));
        _menu.Items.Add(Choice("Luminosité", new[] { ("25 %", 25), ("50 %", 50), ("75 %", 75), ("100 %", 100) },
            () => _settings.Brightness, v => Change(s => s.Brightness = v)));
        _menu.Items.Add(Choice("Vitesse des effets", new[] { ("Lente", 0), ("Moyenne", 1), ("Rapide", 2) },
            () => _settings.Speed, v => Change(s => s.Speed = v)));

        var devices = new ToolStripMenuItem("Appareils");
        devices.DropDownItems.Add(Toggle(_statusAura, () => _settings.Mainboard, v => Change(s => s.Mainboard = v)));
        devices.DropDownItems.Add(Toggle(_statusRam, () => _settings.Ram, v => Change(s => s.Ram = v)));
        devices.DropDownItems.Add(Toggle(_statusCc, () => _settings.Commander, v => Change(s => s.Commander = v)));
        _menu.Items.Add(devices);
        _menu.Items.Add(new ToolStripSeparator());

        var startup = new ToolStripMenuItem("Lancer au démarrage de Windows") { Tag = "startup" };
        startup.Click += (_, _) => ToggleStartup();
        _menu.Items.Add(startup);
        var log = new ToolStripMenuItem("Ouvrir le journal");
        log.Click += (_, _) => { if (File.Exists(Log.FilePath)) Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }); };
        _menu.Items.Add(log);
        var quit = new ToolStripMenuItem("Quitter");
        quit.Click += (_, _) => ExitThread();
        _menu.Items.Add(quit);

        // Lien discret vers le playground, tout en bas. Ouvert via explorer : le navigateur
        // ne démarre pas avec les droits administrateur de l'appli.
        _menu.Items.Add(new ToolStripSeparator());
        var playground = new ToolStripMenuItem("un projet du bored dev playground") { Tag = "sig", Font = MenuTheme.MonoFont };
        playground.Click += (_, _) => Process.Start("explorer.exe", Playground);
        _menu.Items.Add(playground);

        _menu.Opening += (_, _) => RefreshMenu();
        _theme.Attach(_menu); // design de la page du projet, clair ou sombre selon Windows
    }

    ToolStripMenuItem Submenu(string text, params (string Label, Effect Value)[] options)
    {
        var parent = new ToolStripMenuItem(text);
        foreach (var o in options)
        {
            var item = new ToolStripMenuItem(o.Label) { Tag = o.Value };
            item.Click += (_, _) => Change(s => s.Effect = o.Value);
            parent.DropDownItems.Add(item);
        }
        parent.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem i in parent.DropDownItems) i.Checked = (Effect)i.Tag == _settings.Effect;
        };
        return parent;
    }

    static ToolStripMenuItem Choice(string text, (string Label, int Value)[] options, Func<int> current, Action<int> set)
    {
        var parent = new ToolStripMenuItem(text);
        foreach (var o in options)
        {
            var item = new ToolStripMenuItem(o.Label) { Tag = o.Value };
            item.Click += (_, _) => set(o.Value);
            parent.DropDownItems.Add(item);
        }
        parent.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem i in parent.DropDownItems) i.Checked = (int)i.Tag == current();
        };
        return parent;
    }

    static ToolStripMenuItem Toggle(ToolStripMenuItem item, Func<bool> current, Action<bool> set)
    {
        item.Enabled = true;
        item.Click += (_, _) => set(!current());
        item.Tag = current;
        return item;
    }

    void Change(Action<Settings> change)
    {
        _engine.Update(change);
        RefreshMenu();
    }

    void PickColor()
    {
        using var dlg = new ColorDialog { FullOpen = true, Color = Color.FromArgb(_settings.R, _settings.G, _settings.B) };
        if (dlg.ShowDialog() == DialogResult.OK)
            Change(s => { s.R = dlg.Color.R; s.G = dlg.Color.G; s.B = dlg.Color.B; if (s.Effect is Effect.Off or Effect.Rainbow) s.Effect = Effect.Static; });
    }

    void ToggleStartup()
    {
        try
        {
            if (Startup.IsEnabled())
            {
                Startup.Disable();
            }
            else if (!Startup.IsAdmin())
            {
                MessageBox.Show("Relance l'appli en administrateur pour activer le démarrage automatique.", "Gestionnaire RGB");
            }
            else if (Startup.IsInstalledCopy())
            {
                Startup.Enable(Application.ExecutablePath);
            }
            else
            {
                // Copie portable : une tâche admin ne doit pas lancer un exe modifiable par n'importe quel programme.
                MessageBox.Show("Cette copie tourne sans être installée (--portable). Lancez RgbManager.exe sans option pour l'installer : le démarrage avec Windows sera activé.",
                    "Gestionnaire RGB");
            }
        }
        catch (Exception ex)
        {
            Log.W($"Démarrage auto : {ex.Message}");
            MessageBox.Show(ex.Message, "Gestionnaire RGB");
        }
        RefreshMenu();
    }

    void RefreshMenu()
    {
        _statusAura.Text = _engine.AuraStatus;
        _statusRam.Text = _engine.RamStatus;
        _statusCc.Text = _engine.CommanderStatus;
        _statusAura.Checked = _settings.Mainboard;
        _statusRam.Checked = _settings.Ram;
        _statusCc.Checked = _settings.Commander;

        foreach (ToolStripItem i in _menu.Items)
            if (i is ToolStripMenuItem st && "startup".Equals(st.Tag))
                st.Checked = Startup.IsEnabled();
        _swatches.Invalidate();

        string effect = _settings.Effect switch
        {
            Effect.Static => "statique", Effect.Breathing => "respiration", Effect.Rainbow => "arc-en-ciel", _ => "éteint",
        };
        _tray.Text = $"RGB : {effect}, {_settings.Brightness} %";
        UpdateIcon();
    }

    void UpdateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(3, 3, 26, 26);
            if (_settings.Effect == Effect.Rainbow)
            {
                using var brush = new LinearGradientBrush(rect, Color.Red, Color.Blue, 45f)
                {
                    InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { Color.Red, Color.Yellow, Color.Lime, Color.Cyan, Color.Blue, Color.Magenta },
                        Positions = new[] { 0f, 0.2f, 0.4f, 0.6f, 0.8f, 1f },
                    },
                };
                g.FillEllipse(brush, rect);
            }
            else
            {
                var c = _settings.Effect == Effect.Off ? Color.FromArgb(60, 60, 60) : Color.FromArgb(_settings.R, _settings.G, _settings.B);
                using var brush = new SolidBrush(c);
                g.FillEllipse(brush, rect);
            }
            using var pen = new Pen(Color.FromArgb(160, 0, 0, 0), 2);
            g.DrawEllipse(pen, rect);
        }
        IntPtr handle = bmp.GetHicon();
        var old = _tray.Icon;
        _tray.Icon = Icon.FromHandle(handle);
        old?.Dispose();
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        _iconHandle = handle;
    }

    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _engine.RequestApply(true); // la carte mère sort du mode direct en veille
    }

    void OnSessionEnding(object sender, SessionEndingEventArgs e) => _engine.Stop();

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _updateTimer.Dispose();
        _engine.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        base.ExitThreadCore();
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
}
