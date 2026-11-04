using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ForeverScout;

/// <summary>Mörka menyer i Lägereld-stil (fältmenyn och kugghjulsmenyn).</summary>
internal sealed class DarkMenuRenderer() : ToolStripProfessionalRenderer(new DarkColors())
{
    public static ContextMenuStrip NewMenu() => new()
    {
        Renderer = new DarkMenuRenderer(), BackColor = Theme.Surface, ForeColor = Theme.Text,
        Font = Theme.Ui(9.75f), ShowImageMargin = true, ShowCheckMargin = false, Padding = new Padding(2, 4, 2, 4),
    };

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var color = !e.Item.Enabled ? Theme.Faint : e.Item.Selected ? Theme.GoldHi : Theme.Text;
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, color, e.TextFormat);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        r.Inflate(1, 1);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Theme.GoldSoft)) e.Graphics.FillRectangle(bg, r);
        using var pen = new Pen(Theme.Gold, Math.Max(1.6f, r.Height / 8f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawLines(pen,
        [
            new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
            new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
            new PointF(r.Left + r.Width * 0.78f, r.Top + r.Height * 0.30f),
        ]);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(Theme.Surface3);
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Muted;
        base.OnRenderArrow(e);
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color MenuBorder => Theme.Surface3;
        public override Color MenuItemBorder => Theme.Surface2;
        public override Color MenuItemSelected => Theme.Surface2;
        public override Color MenuItemSelectedGradientBegin => Theme.Surface2;
        public override Color MenuItemSelectedGradientEnd => Theme.Surface2;
        public override Color MenuItemPressedGradientBegin => Theme.Surface2;
        public override Color MenuItemPressedGradientEnd => Theme.Surface2;
        public override Color CheckBackground => Theme.GoldSoft;
        public override Color CheckSelectedBackground => Theme.GoldSoft;
        public override Color CheckPressedBackground => Theme.GoldSoft;
        public override Color SeparatorDark => Theme.Surface3;
        public override Color SeparatorLight => Theme.Surface3;
    }
}

public enum TrayState { Normal, Uploading, Error }

/// <summary>
/// Ikoner för fältet (genererade av tools/make-icons.mjs): elden, elden med amber ring och vandrande gnista
/// (uppladdning, fyra bildrutor) och elden med röd prick (fel).
/// </summary>
internal static class TrayIcons
{
    public static Icon Normal { get; } = FromPng("tray-normal.png");
    public static Icon Error { get; } = FromPng("tray-error.png");
    public static Icon[] Uploading { get; } = Enumerable.Range(0, 4).Select(i => FromPng($"tray-upload-{i}.png")).ToArray();

    private static Icon FromPng(string name)
    {
        using var bmp = Theme.Resource(name);
        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);
}
