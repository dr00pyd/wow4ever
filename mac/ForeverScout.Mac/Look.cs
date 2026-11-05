using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ForeverScout.Mac;

/// <summary>
/// Lägereld-tokens från UI V1 (inkorg/ui/BRIEF-UI-V1.md, samma värden som Theme i Windows-klientens Support.cs):
/// Background #17151A · Panel #211C20 · Elevated #292228 · Border #42363A · Primary gold #F0B844 · Highlight gold #FFD987 ·
/// Text #F3EEE8 · Secondary #B9AAA5 · Success #68C8A0 · Error #E47772. Avstånd 4/8/12/16/24/32, hörnradie 6–10, minsta
/// brödtext 13 px.
/// </summary>
internal static class Look
{
    public static readonly Color Bg = Color.Parse("#17151A");
    public static readonly Color Bg2 = Color.Parse("#1E1920");
    public static readonly Color Footer = Color.Parse("#151318");
    public static readonly Color Surface = Color.Parse("#211C20");
    public static readonly Color Surface2 = Color.Parse("#292228");
    public static readonly Color Surface3 = Color.Parse("#302A31");
    public static readonly Color TableHead = Color.Parse("#30272B");
    public static readonly Color Line = Color.Parse("#42363A");
    public static readonly Color LineSoft = Color.Parse("#3B3035");
    public static readonly Color Text = Color.Parse("#F3EEE8");
    public static readonly Color Muted = Color.Parse("#B9AAA5");
    public static readonly Color Faint = Color.Parse("#A3948F");
    public static readonly Color BorderInput = Color.Parse("#71615F");
    public static readonly Color InputBg = Color.Parse("#141217");
    public static readonly Color Gold = Color.Parse("#F0B844");
    public static readonly Color GoldHi = Color.Parse("#FFD987");
    public static readonly Color GoldInk = Color.Parse("#291804");
    public static readonly Color GoldSoft = Color.Parse("#312721");
    public static readonly Color GoldEdge = Color.Parse("#5A432C");
    public static readonly Color Ok = Color.Parse("#68C8A0");
    public static readonly Color OkSoft = Color.Parse("#28372F");
    public static readonly Color Warn = Color.Parse("#F0B45A");
    public static readonly Color Err = Color.Parse("#E47772");
    public static readonly Color ErrSoft = Color.Parse("#3A2427");

    public static IBrush B(Color c) => new SolidColorBrush(c);

    /// <summary>Rubriker i Georgia (som på Windows och på sajten), brödtext i systemfonten (San Francisco).</summary>
    public static readonly FontFamily Display = new("Georgia, Times New Roman, serif");
    public static readonly FontFamily Mono = new("Menlo, Monaco, monospace");

    public static TextBlock Label(string text, double size = 13, Color? color = null, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = text, FontSize = size, Foreground = B(color ?? Text), FontWeight = weight, TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Heading(string text, double size = 22) => new()
    {
        Text = text, FontSize = size, FontFamily = Display, FontWeight = FontWeight.Bold, Foreground = B(Text), TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>Rundad panel med tunn kant (Panel/Elevated).</summary>
    public static Border Panel(Control child, Color? bg = null, double pad = 16) => new()
    {
        Background = B(bg ?? Surface), BorderBrush = B(Line), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
        Padding = new Thickness(pad), Child = child,
    };

    /// <summary>Guldknapp (primär handling) med tydligt fokus- och hovringsläge.</summary>
    public static Button GoldButton(string text)
    {
        var b = new Button
        {
            Content = text, Background = B(Gold), Foreground = B(GoldInk), FontWeight = FontWeight.Bold, FontSize = 14,
            CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 8), BorderBrush = B(GoldHi), BorderThickness = new Thickness(1),
        };
        b.Resources["ButtonBackgroundPointerOver"] = B(GoldHi);
        b.Resources["ButtonBackgroundPressed"] = B(Color.Parse("#E7A735"));
        b.Resources["ButtonForegroundPointerOver"] = B(GoldInk);
        b.Resources["ButtonForegroundPressed"] = B(GoldInk);
        b.Resources["ButtonBorderBrushPointerOver"] = B(GoldHi);
        b.Resources["ButtonBackgroundDisabled"] = B(Surface3);
        b.Resources["ButtonForegroundDisabled"] = B(Faint);
        return b;
    }

    /// <summary>Sekundär knapp.</summary>
    public static Button QuietButton(string text)
    {
        var b = new Button
        {
            Content = text, Background = B(Surface3), Foreground = B(Text), FontSize = 13, CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6), BorderBrush = B(Line), BorderThickness = new Thickness(1),
        };
        b.Resources["ButtonBackgroundPointerOver"] = B(Surface2);
        b.Resources["ButtonForegroundPointerOver"] = B(GoldHi);
        b.Resources["ButtonBorderBrushPointerOver"] = B(GoldEdge);
        b.Resources["ButtonBackgroundPressed"] = B(Bg2);
        return b;
    }

    /// <summary>Inmatningsfält med guldram vid fokus.</summary>
    public static TextBox Input(string? watermark = null, int maxLength = 0)
    {
        var t = new TextBox
        {
            Background = B(InputBg), Foreground = B(Text), BorderBrush = B(BorderInput), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), FontSize = 14, Padding = new Thickness(10, 7), PlaceholderText = watermark, MaxLength = maxLength,
        };
        t.Resources["TextControlBorderBrushFocused"] = B(Gold);
        t.Resources["TextControlBackgroundFocused"] = B(InputBg);
        t.Resources["TextControlBackgroundPointerOver"] = B(InputBg);
        t.Resources["TextControlForegroundFocused"] = B(Text);
        t.Resources["TextControlForegroundPointerOver"] = B(Text);
        return t;
    }

    /// <summary>Statuspiller (t.ex. "Addon 1.0.3 anslutet").</summary>
    public static Border Pill(TextBlock text) => new()
    {
        Background = B(OkSoft), CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3), Child = text,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>height null: höjden följer innehållet. En satt Height vinner över SizeToContent i Avalonia,
    /// så dialogen blev bara en namnlist (1.0.7 hade Height=10).</summary>
    public static Window StyleWindow(Window w, string title, double width, double? height)
    {
        w.Title = title;
        w.Width = width;
        if (height is { } h) w.Height = h;
        else { w.Height = double.NaN; w.SizeToContent = SizeToContent.Height; }
        w.Background = B(Bg);
        w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (Res.Has("app-256.png")) w.Icon = Res.Icon("app-256.png");
        return w;
    }
}
