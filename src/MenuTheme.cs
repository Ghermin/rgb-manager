using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RgbManager;

/// <summary>
/// Couleurs du menu, reprises de la page du projet (docs/index.html) : clair ou sombre selon Windows
/// (Paramètres › Personnalisation › Couleurs › « Choisir votre mode d'application par défaut »).
/// </summary>
sealed class Palette
{
    public Color Back, Hover, Text, Muted, Line;

    public static readonly Palette Light = new()
    {
        Back = Color.FromArgb(0xFF, 0xFF, 0xFF), Hover = Color.FromArgb(0xEC, 0xEE, 0xF2),
        Text = Color.FromArgb(0x12, 0x13, 0x18), Muted = Color.FromArgb(0x56, 0x5A, 0x66), Line = Color.FromArgb(0xDF, 0xE2, 0xE8),
    };

    public static readonly Palette Dark = new()
    {
        Back = Color.FromArgb(0x15, 0x16, 0x1B), Hover = Color.FromArgb(0x25, 0x27, 0x30),
        Text = Color.FromArgb(0xEE, 0xF0, 0xF5), Muted = Color.FromArgb(0x9E, 0xA3, 0xB0), Line = Color.FromArgb(0x2C, 0x2F, 0x38),
    };

    /// <summary>Mode des applications choisi dans Windows (0 = sombre). Clair par défaut si la clé manque.</summary>
    public static bool WindowsIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }
}

/// <summary>Menu moderne : coins arrondis (Windows 11), survol arrondi, coches et flèches fines, thème clair / sombre.</summary>
sealed class MenuTheme : ToolStripRenderer
{
    public Palette P { get; private set; } = Palette.WindowsIsDark() ? Palette.Dark : Palette.Light;
    public static readonly Font TextFont = new("Segoe UI", 9.75f);
    public static readonly Font TitleFont = new("Segoe UI", 9.75f, FontStyle.Bold);
    public static readonly Font MonoFont = new("Consolas", 8f);

    /// <summary>Relit le mode de Windows ; renvoie true s'il a changé.</summary>
    public bool Refresh()
    {
        var p = Palette.WindowsIsDark() ? Palette.Dark : Palette.Light;
        if (p == P) return false;
        P = p;
        return true;
    }

    /// <summary>Applique le style à un menu et à tous ses sous-menus, présents et futurs.</summary>
    public void Attach(ToolStripDropDownMenu menu)
    {
        menu.Renderer = this;
        menu.Font = TextFont;
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = true;
        menu.DropShadowEnabled = true;
        menu.Padding = new Padding(4, 6, 4, 6);
        menu.HandleCreated += (_, _) => ApplyWindow(menu);
        menu.ShowItemToolTips = true;
        // Le mode de Windows est relu à chaque ouverture : le menu suit un changement sans redémarrer l'appli.
        menu.Opening += (_, _) => { Refresh(); ApplyWindow(menu); menu.Invalidate(); };
        foreach (ToolStripItem item in menu.Items) AttachItem(item);
        menu.ItemAdded += (_, e) => AttachItem(e.Item);
    }

    void AttachItem(ToolStripItem item)
    {
        if (item is ToolStripMenuItem m)
        {
            m.Padding = new Padding(m.Padding.Left, 3, m.Padding.Right, 3);
            if (m.HasDropDownItems && m.DropDown is ToolStripDropDownMenu sub) Attach(sub);
        }
    }

    /// <summary>Coins arrondis et bordure de la fenêtre du menu par DWM (Windows 11 ; sans effet ailleurs).</summary>
    void ApplyWindow(Control c)
    {
        if (!c.IsHandleCreated) return;
        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(c.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int border = ColorTranslator.ToWin32(P.Line);
        DwmSetWindowAttribute(c.Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    static float Scale(ToolStripItem item) => (item.Owner?.DeviceDpi ?? 96) / 96f;
    static float Scale(ToolStrip strip) => strip.DeviceDpi / 96f;

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var b = new SolidBrush(P.Back);
        e.Graphics.FillRectangle(b, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // Windows 11 dessine la bordure arrondie (DWMWA_BORDER_COLOR). Sinon, un simple trait.
        if (Environment.OSVersion.Version.Build >= 22000) return;
        using var pen = new Pen(P.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var item = e.Item;
        bool hot = item.Enabled && (item.Selected || (item is ToolStripMenuItem m && m.DropDown.Visible));
        if (!hot) return;
        float s = Scale(item);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(new RectangleF(0, 0.5f, item.Width - 0.5f, item.Height - 1f), 6 * s);
        using var b = new SolidBrush(P.Hover);
        e.Graphics.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var item = e.Item;
        Color c = !item.Enabled || "sig".Equals(item.Tag) ? P.Muted : P.Text;
        if ("sig".Equals(item.Tag) && item.Selected) c = P.Text;
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, c, e.TextFormat);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        float s = Scale(e.Item);
        var r = e.ImageRectangle;
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(P.Text, 1.6f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(pen, new[]
        {
            new PointF(cx - 4.5f * s, cy + 0.2f * s), new PointF(cx - 1.5f * s, cy + 3.2f * s), new PointF(cx + 4.5f * s, cy - 3.3f * s),
        });
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        float s = Scale(e.Item);
        var r = e.ArrowRectangle;
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(P.Muted, 1.4f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(pen, new[] { new PointF(cx - 1.5f * s, cy - 3.5f * s), new PointF(cx + 2f * s, cy), new PointF(cx - 1.5f * s, cy + 3.5f * s) });
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        float s = Scale(e.Item);
        int y = e.Item.Height / 2;
        using var pen = new Pen(P.Line);
        e.Graphics.DrawLine(pen, 8 * s, y, e.Item.Width - 8 * s, y);
    }

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// <summary>Rangée de pastilles de couleur cliquables, comme sur la page du projet.</summary>
sealed class SwatchRow : ToolStripItem
{
    readonly (string Name, byte R, byte G, byte B)[] _colors;
    readonly MenuTheme _theme;
    int _hover = -1;

    public Func<int> Current = () => -1;
    public event Action<int> Picked;

    public SwatchRow((string Name, byte R, byte G, byte B)[] colors, MenuTheme theme)
    {
        _colors = colors;
        _theme = theme;
        AccessibleName = "Couleurs";
    }

    float S => (Owner?.DeviceDpi ?? 96) / 96f;
    float Size1 => 18 * S;
    float Gap => 6 * S;
    float Left => 4 * S; // aligné sur le texte des autres entrées

    public override Size GetPreferredSize(Size constrainingSize) =>
        new((int)Math.Ceiling(Left * 2 + _colors.Length * Size1 + (_colors.Length - 1) * Gap), (int)Math.Ceiling(34 * S));

    RectangleF Cell(int i) => new(Left + i * (Size1 + Gap), (Height - Size1) / 2f, Size1, Size1);

    int HitTest(Point p)
    {
        for (int i = 0; i < _colors.Length; i++)
        {
            var r = Cell(i);
            r.Inflate(Gap / 2, Gap / 2);
            if (r.Contains(p)) return i;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = HitTest(e.Location);
        if (h != _hover)
        {
            _hover = h;
            ToolTipText = h >= 0 ? _colors[h].Name : null;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnClick(EventArgs e)
    {
        if (_hover >= 0) Picked?.Invoke(_hover);
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = _theme.P;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        int current = Current();
        for (int i = 0; i < _colors.Length; i++)
        {
            var c = _colors[i];
            var r = Cell(i);
            if (i == current || i == _hover)
            {
                // Anneau autour de la couleur choisie (plein) ou survolée (léger)
                var ring = r;
                ring.Inflate(3 * S, 3 * S);
                using var ringPath = MenuTheme.Rounded(ring, 7.5f * S);
                using var ringPen = new Pen(i == current ? p.Text : p.Muted, 1.5f * S);
                g.DrawPath(ringPen, ringPath);
            }
            using var path = MenuTheme.Rounded(r, 5 * S);
            using var fill = new SolidBrush(Color.FromArgb(c.R, c.G, c.B));
            g.FillPath(fill, path);
            using var edge = new Pen(Color.FromArgb(90, 128, 128, 128), 1f);
            g.DrawPath(edge, path);
        }
    }
}

/// <summary>
/// Curseur 0–100 % dans le menu (luminosité). Hébergé comme contrôle : glisser ou cliquer ne ferme pas le menu.
/// <see cref="Changed"/> part en continu pendant le glissement (final = false), puis une fois au relâchement (final = true).
/// </summary>
sealed class SliderRow : ToolStripControlHost
{
    public SliderRow(string label, MenuTheme theme) : base(new SliderControl(label, theme))
    {
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        AccessibleName = label;
    }

    SliderControl Slider => (SliderControl)Control;

    public int Value
    {
        get => Slider.Value;
        set => Slider.Value = value;
    }

    /// <summary>Appareil coupé : curseur en gris.</summary>
    public bool Dimmed
    {
        get => Slider.Dimmed;
        set => Slider.Dimmed = value;
    }

    public event Action<int, bool> Changed
    {
        add => Slider.Changed += value;
        remove => Slider.Changed -= value;
    }
}

sealed class SliderControl : Control
{
    readonly string _label;
    readonly MenuTheme _theme;
    int _value = 100;
    bool _drag, _hover, _dimmed;

    public event Action<int, bool> Changed;

    public SliderControl(string label, MenuTheme theme)
    {
        _label = label;
        _theme = theme;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false); // le clavier reste au menu
        TabStop = false;
        Font = MenuTheme.TextFont;
        Size = GetPreferredSize(Size.Empty);
    }

    public int Value
    {
        get => _value;
        set
        {
            value = Compat.Clamp(value, 0, 100);
            if (value == _value) return;
            _value = value;
            Invalidate();
        }
    }

    public bool Dimmed
    {
        get => _dimmed;
        set
        {
            if (value == _dimmed) return;
            _dimmed = value;
            Invalidate();
        }
    }

    float S => DeviceDpi / 96f;
    float Inset => 4 * S; // aligné sur le texte des autres entrées
    int LabelWidth => string.IsNullOrEmpty(_label) ? 0 : TextRenderer.MeasureText(_label, Font).Width + (int)(10 * S);
    int ValueWidth => TextRenderer.MeasureText("100 %", Font).Width + (int)(4 * S);
    float Knob => 6 * S;

    public override Size GetPreferredSize(Size proposedSize) =>
        new((int)(Inset + LabelWidth + 130 * S + ValueWidth + 8 * S), (int)(30 * S));

    (float X0, float X1) Track()
    {
        float x0 = Inset + LabelWidth + Knob, x1 = Width - ValueWidth - 8 * S - Knob;
        return (x0, Math.Max(x0 + 1, x1));
    }

    void SetFromX(int x, bool final)
    {
        var (x0, x1) = Track();
        int v = (int)Math.Round((x - x0) / (x1 - x0) * 100);
        v = Compat.Clamp(v, 0, 100);
        bool changed = v != _value;
        Value = v;
        if (changed || final) Changed?.Invoke(_value, final);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _drag = true;
            Capture = true;
            SetFromX(e.X, false);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag) SetFromX(e.X, false);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_drag && e.Button == MouseButtons.Left)
        {
            _drag = false;
            Capture = false;
            SetFromX(e.X, true);
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int v = Compat.Clamp(_value + Math.Sign(e.Delta) * 5, 0, 100);
        if (v != _value)
        {
            Value = v;
            Changed?.Invoke(v, true);
        }
        base.OnMouseWheel(e);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = _theme.P;
        var ink = _dimmed ? p.Muted : p.Text;
        g.Clear(p.Back);

        var text = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        if (LabelWidth > 0)
            TextRenderer.DrawText(g, _label, Font, new Rectangle((int)Inset, 0, LabelWidth, Height), ink, text | TextFormatFlags.Left);
        TextRenderer.DrawText(g, $"{_value} %", Font, new Rectangle(Width - ValueWidth - (int)(8 * S), 0, ValueWidth, Height), p.Muted, text | TextFormatFlags.Right);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var (x0, x1) = Track();
        float cy = Height / 2f, h = 4 * S, x = x0 + (x1 - x0) * _value / 100f;
        using (var back = MenuTheme.Rounded(new RectangleF(x0, cy - h / 2, x1 - x0, h), h / 2))
        using (var b = new SolidBrush(p.Line))
            g.FillPath(b, back);
        if (x > x0 && !_dimmed)
        {
            using var fill = MenuTheme.Rounded(new RectangleF(x0, cy - h / 2, x - x0, h), h / 2);
            using var b = new SolidBrush(ink);
            g.FillPath(b, fill);
        }
        float r = Knob * (_drag || _hover ? 1.15f : 1f);
        using (var b = new SolidBrush(p.Back))
            g.FillEllipse(b, x - r, cy - r, 2 * r, 2 * r);
        using (var pen = new Pen(ink, 2 * S))
            g.DrawEllipse(pen, x - r, cy - r, 2 * r, 2 * r);
    }
}
