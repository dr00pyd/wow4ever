using System.Drawing.Drawing2D;

namespace ForeverScout;

/// <summary>Små egenritade kontroller för UI V1: rundade paneler, knappar med fokusmarkering, inmatningsfält, piller.</summary>
internal static class Draw
{
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Färgen bakom en kontroll (första ogenomskinliga förälder, eller en rundad panels fyllning).</summary>
    public static Color BackOf(Control c)
    {
        for (var p = c.Parent; p != null; p = p.Parent)
        {
            if (p is RoundedPanel rp) return rp.FillColor;
            if (p.BackColor.A == 255) return p.BackColor;
        }
        return Theme.Bg;
    }

    public static int Px(Control c, int logical) => c.LogicalToDeviceUnits(logical);
}

/// <summary>Panel med rundade hörn, kant och fyllning. Barn med BackColor = Transparent ritas ovanpå.</summary>
internal class RoundedPanel : Panel
{
    public Color FillColor { get; set; } = Theme.Surface;
    public Color BorderColor { get; set; } = Theme.Line;
    public int Radius { get; set; } = 10;

    /// <summary>Barn utan Dock dockas överst så att Padding gäller (WinForms tillämpar Padding bara på dockade barn).</summary>
    public bool DockChildren { get; set; } = true;

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        if (DockChildren && e.Control is { Dock: DockStyle.None } c) c.Dock = DockStyle.Top;
    }

    public RoundedPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using (var b = new SolidBrush(Draw.BackOf(this))) e.Graphics.FillRectangle(b, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Draw.Round(r, Draw.Px(this, Radius));
        using (var b = new SolidBrush(FillColor)) g.FillPath(b, path);
        if (BorderColor.A > 0) using (var pen = new Pen(BorderColor)) g.DrawPath(pen, path);
        base.OnPaint(e);
    }
}

/// <summary>Knapp: primär (guldgradient) eller sekundär. Rundad, tangentbordsfokus syns som guldring.</summary>
internal sealed class ScoutButton : Button
{
    private readonly bool _primary;
    private bool _hover, _down;

    public ScoutButton(string text, bool primary)
    {
        _primary = primary;
        Text = text;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = Theme.Ui(10f, primary ? FontStyle.Bold : FontStyle.Regular);
        Padding = primary ? new Padding(16, 7, 16, 7) : new Padding(12, 6, 12, 6);
        Cursor = Cursors.Hand;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        ForeColor = primary ? Theme.GoldInk : Theme.Text;
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; _down = false; Invalidate(); };
        MouseDown += (_, _) => { _down = true; Invalidate(); };
        MouseUp += (_, _) => { _down = false; Invalidate(); };
        GotFocus += (_, _) => Invalidate();
        LostFocus += (_, _) => Invalidate();
        EnabledChanged += (_, _) => Invalidate();
    }

    public override Size GetPreferredSize(Size proposed)
    {
        var t = TextRenderer.MeasureText(Text, Font);
        return new Size(t.Width + Padding.Horizontal + Draw.Px(this, 4), Math.Max(t.Height + Padding.Vertical, Draw.Px(this, 34)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Draw.BackOf(this))) g.FillRectangle(bg, ClientRectangle);
        var r = new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f);
        using var path = Draw.Round(r, Draw.Px(this, 6));
        Color text;
        if (_primary)
        {
            var top = !Enabled ? ControlPaint.Dark(Theme.GoldBottom, 0.3f) : _down ? Theme.GoldBottom : _hover ? Theme.GoldHi : Theme.GoldTop;
            var bottom = !Enabled ? ControlPaint.Dark(Theme.GoldBottom, 0.4f) : _down ? Theme.GoldBottom : Theme.GoldBottom;
            using var lg = new LinearGradientBrush(new RectangleF(0, 0, Width, Height), top, bottom, LinearGradientMode.Vertical);
            g.FillPath(lg, path);
            using var edge = new Pen(Theme.GoldBottom);
            g.DrawPath(edge, path);
            text = Enabled ? Theme.GoldInk : ControlPaint.Dark(Theme.GoldInk, -0.2f);
        }
        else
        {
            using var fill = new SolidBrush(_down ? Theme.Surface2 : _hover ? ControlPaint.Light(Theme.Surface3, 0.12f) : Theme.Surface3);
            g.FillPath(fill, path);
            using var edge = new Pen(_hover && Enabled ? Theme.Gold : ColorTranslator.FromHtml("#72656C"));
            g.DrawPath(edge, path);
            text = Enabled ? Theme.Text : Theme.Faint;
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues)
        {
            var fr = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using var fp = Draw.Round(fr, Draw.Px(this, 7));
            using var ring = new Pen(Theme.GoldHi, 1.6f);
            g.DrawPath(ring, fp);
        }
    }
}

/// <summary>Inmatningsfält i rundad ram. Ramen blir guld när fältet har fokus.</summary>
internal sealed class InputBox : RoundedPanel
{
    public TextBox Box { get; } = new()
    {
        BorderStyle = BorderStyle.None, BackColor = Theme.InputBg, ForeColor = Theme.Text, Font = Theme.Ui(10.5f),
    };

    public InputBox(int width)
    {
        FillColor = Theme.InputBg;
        BorderColor = Theme.BorderInput;
        Radius = 6;
        Width = width;
        Height = Box.PreferredHeight + 20;
        // Ramen ger 8 px; resten (6 px) är TextBoxens egen inre marginal. Utan den hamnar markören och första tecknets
        // vänstra överhäng (t.ex. "j") på x = 0 och klipps – BorderStyle.None har ingen marginal alls.
        Padding = new Padding(8, 10, 8, 8);
        Box.Dock = DockStyle.Fill;
        Box.HandleCreated += (_, _) => ApplyTextMargins();
        Box.DpiChangedAfterParent += (_, _) => ApplyTextMargins();
        Controls.Add(Box);
        Box.GotFocus += (_, _) => { BorderColor = Theme.Gold; Invalidate(); };
        Box.LostFocus += (_, _) => { BorderColor = Theme.BorderInput; Invalidate(); };
        Click += (_, _) => Box.Focus();
    }

    public string PlaceholderText { get => Box.PlaceholderText; set => Box.PlaceholderText = value; }

    /// <summary>Sätter texten och visar den från början (ingen horisontell rullning som döljer första tecknet).</summary>
    public string Value
    {
        get => Box.Text;
        set
        {
            Box.Text = value;
            // Visa texten från början, lägg sedan markören efter sista tecknet (som när man skrivit själv) –
            // en markör på position 0 ritas ovanpå första tecknet och får t.ex. ett "j" att se bortklippt ut.
            Box.SelectionStart = 0;
            Box.ScrollToCaret();
            Box.SelectionStart = Box.TextLength;
            Box.SelectionLength = 0;
        }
    }

    /// <summary>Inre textmarginal i logiska pixlar (vänster/höger).</summary>
    public const int TextMarginLogical = 6;

    private const int EM_SETMARGINS = 0xD3, EM_GETMARGINS = 0xD4, EC_LEFTMARGIN = 1, EC_RIGHTMARGIN = 2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    private void ApplyTextMargins()
    {
        int m = Box.LogicalToDeviceUnits(TextMarginLogical);
        SendMessage(Box.Handle, EM_SETMARGINS, EC_LEFTMARGIN | EC_RIGHTMARGIN, (m & 0xFFFF) | (m << 16));
    }

    /// <summary>Aktuell vänster/höger marginal i TextBoxen (enhetspixlar) – används av testerna.</summary>
    public (int Left, int Right) TextMargins
    {
        get
        {
            var r = (long)SendMessage(Box.Handle, EM_GETMARGINS, IntPtr.Zero, IntPtr.Zero);
            return ((int)(r & 0xFFFF), (int)((r >> 16) & 0xFFFF));
        }
    }
}

/// <summary>Litet piller med statusprick (t.ex. "● Addon 1.0.1 anslutet").</summary>
internal sealed class Pill : RoundedPanel
{
    private readonly Label _label = new() { AutoSize = true, BackColor = Color.Transparent, Font = Theme.Ui(9f), Margin = Padding.Empty };

    public Pill()
    {
        DockChildren = false;
        Radius = 12;
        Padding = new Padding(10, 4, 10, 4);
        Controls.Add(_label);
        Set("", Theme.Ok);
    }

    private void Fit()
    {
        var sz = _label.PreferredSize;
        _label.Location = new Point(Padding.Left, Padding.Top);
        Size = new Size(sz.Width + Padding.Horizontal, sz.Height + Padding.Vertical);
    }

    public void Set(string text, Color color)
    {
        _label.Text = "● " + text;
        _label.ForeColor = color;
        FillColor = color == Theme.Ok ? Theme.OkSoft : color == Theme.Err ? Theme.ErrSoft : Theme.Surface2;
        BorderColor = color == Theme.Ok ? ColorTranslator.FromHtml("#4C8668") : color == Theme.Err ? ColorTranslator.FromHtml("#8A4A4A") : Theme.Line;
        Fit();
        Invalidate();
    }
}

/// <summary>Versal liten rubrik i guld ("MITT BIDRAG").</summary>
internal sealed class SmallTitle : Label
{
    public SmallTitle(string text)
    {
        Text = text.ToUpper(ForeverScout.Core.Strings.Culture);
        AutoSize = true;
        ForeColor = ColorTranslator.FromHtml("#EFC06B");
        Font = Theme.Ui(8.5f, FontStyle.Bold);
        BackColor = Color.Transparent;
        Margin = new Padding(0, 0, 0, 6);
    }
}
