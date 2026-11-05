using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ForeverScout.Core;

namespace ForeverScout;

/// <summary>
/// Design tokens för UI V1 (inkorg/ui/BRIEF-UI-V1.md): Background #17151A · Panel #211C20 · Elevated #292228 ·
/// Border #42363A · Primary gold #F0B844 · Highlight gold #FFD987 · Text #F3EEE8 · Secondary #B9AAA5 ·
/// Success #68C8A0 · Error #E47772. Avstånd 4/8/12/16/24/32 px, hörnradie 6–10 px, minsta brödtext 13 px (≈ 9,75 pt).
/// </summary>
internal static class Theme
{
    public static readonly Color Bg = ColorTranslator.FromHtml("#17151A");        // Background
    public static readonly Color Bg2 = ColorTranslator.FromHtml("#1E1920");       // header/sidfot (prototypen)
    public static readonly Color Footer = ColorTranslator.FromHtml("#151318");
    public static readonly Color Surface = ColorTranslator.FromHtml("#211C20");   // Panel
    public static readonly Color Surface2 = ColorTranslator.FromHtml("#292228");  // Elevated panel
    public static readonly Color Surface3 = ColorTranslator.FromHtml("#302A31");  // sekundär knapp
    public static readonly Color TableHead = ColorTranslator.FromHtml("#30272B");
    public static readonly Color Line = ColorTranslator.FromHtml("#42363A");      // Border
    public static readonly Color LineSoft = ColorTranslator.FromHtml("#3B3035");
    public static readonly Color Text = ColorTranslator.FromHtml("#F3EEE8");      // Text primary
    public static readonly Color Muted = ColorTranslator.FromHtml("#B9AAA5");     // Text secondary
    public static readonly Color Faint = ColorTranslator.FromHtml("#A3948F");
    public static readonly Color BorderInput = ColorTranslator.FromHtml("#71615F");
    public static readonly Color InputBg = ColorTranslator.FromHtml("#141217");
    public static readonly Color Gold = ColorTranslator.FromHtml("#F0B844");      // Primary gold
    public static readonly Color GoldHi = ColorTranslator.FromHtml("#FFD987");    // Highlight gold
    public static readonly Color GoldTop = ColorTranslator.FromHtml("#FFD46C");
    public static readonly Color GoldBottom = ColorTranslator.FromHtml("#E7A735");
    public static readonly Color GoldInk = ColorTranslator.FromHtml("#291804");
    public static readonly Color GoldSoft = ColorTranslator.FromHtml("#312721");  // värdshusruta
    public static readonly Color GoldEdge = ColorTranslator.FromHtml("#5A432C");
    public static readonly Color Ok = ColorTranslator.FromHtml("#68C8A0");        // Success
    public static readonly Color OkSoft = ColorTranslator.FromHtml("#28372F");
    public static readonly Color Warn = ColorTranslator.FromHtml("#F0B45A");
    public static readonly Color Err = ColorTranslator.FromHtml("#E47772");       // Error
    public static readonly Color ErrSoft = ColorTranslator.FromHtml("#3A2427");

    public static Font Display(float size) => new("Georgia", size, FontStyle.Bold);
    public static Font Ui(float size, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);

    public static Button GoldButton(string text) => new ScoutButton(text, primary: true);

    public static Button QuietButton(string text) => new ScoutButton(text, primary: false);

    public static TextBox Input() => new()
    {
        BackColor = Bg, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle, Font = Ui(10.5f),
    };

    public static Label Label(string text, float size = 9.5f, Color? color = null, FontStyle style = FontStyle.Regular) => new()
    {
        Text = text, AutoSize = true, ForeColor = color ?? Text, Font = Ui(size, style), BackColor = Color.Transparent,
    };

    /// <summary>Mörk titelrad (Windows 10 20H1+/11).</summary>
    public static void DarkTitleBar(Form f)
    {
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, sizeof(int));
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static Icon AppIcon { get; } = LoadIcon();

    /// <summary>Inbäddad bild (Resources/*.png).</summary>
    public static Bitmap Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("res/" + name)
                      ?? throw new FileNotFoundException(name);
        using var tmp = new Bitmap(s);
        return new Bitmap(tmp);
    }

    /// <summary>Hela logotypen (bara inuti appen – aldrig som exe-ikon), skalad till given bredd.</summary>
    public static PictureBox Logo(int width) => new()
    {
        Image = Resource("logo-header.png"), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent,
        Size = new Size(width, (int)Math.Round(width * 258 / 360.0)),
    };

    private static Icon LoadIcon()
    {
        // Inbäddad resurs – ingen läsning av exe-filen från disk.
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("res/app.ico");
        return s is null ? SystemIcons.Application : new Icon(s);
    }
}

/// <summary>Nyckeln sparas krypterad med DPAPI (CurrentUser) i %APPDATA%\ForeverScout\key.bin.</summary>
internal static class KeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ForeverScout.v1");
    private static string PathFor => System.IO.Path.Combine(SafeFs.AppDataDir, "key.bin");

    public static string? Load()
    {
        try
        {
            if (!SafeFs.FileExists(PathFor)) return null;
            var plain = ProtectedData.Unprotect(SafeFs.ReadAllBytes(PathFor), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }

    public static void Save(string key) =>
        SafeFs.WriteAllBytes(PathFor, ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));

    public static void Delete() { try { SafeFs.DeleteFile(PathFor); } catch (IOException) { } catch (SafeFsException) { } }
}

/// <summary>"Starta med Windows": värdet ForeverScout under HKCU\Software\Microsoft\Windows\CurrentVersion\Run (via SafeFs).</summary>
internal static class Autostart
{
    public static bool IsEnabled => SafeFs.AutostartEnabled;
    public static void Set(bool on) => SafeFs.SetAutostart(on);
}

/// <summary>Länkar till sajten (sv/en). Sluggarna följer lib/i18n/routes.ts.</summary>
internal static class Links
{
    public static string Guide(ApiClient api) => api.Site(Strings.IsSv ? "/" : "/en").ToString();
    public static string Contribute(ApiClient api) => api.Site(Strings.IsSv ? "/bidra" : "/en/contribute").ToString();
    public static string Faq(ApiClient api) => api.Site(Strings.IsSv ? "/bidra/vanliga-fragor" : "/en/contribute/faq").ToString();
    public static string Terms(ApiClient api) => api.Site(Strings.IsSv ? "/bidra/villkor" : "/en/contribute/terms").ToString();
    public static string Privacy(ApiClient api) => api.Site(Strings.IsSv ? "/bidra/integritet" : "/en/contribute/privacy").ToString();
    public static string Report(ApiClient api) => Contribute(api) + "#rapportera-fel";
    public static string Download(ApiClient api) => api.Site(Strings.IsSv ? "/bidra/ladda-ner" : "/en/contribute/download").ToString();

    /// <summary>Öppnar bara http(s)-länkar mot sajtens värd.</summary>
    public static void Open(ApiClient api, string url)
    {
        if (!Uri.TryCreate(api.BaseUri, url, out var u)) return;
        if (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)) return;
        if (!string.Equals(u.Host, api.BaseUri.Host, StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); } catch { }
    }
}

/// <summary>Addonen som byggdes in i exe:n (client/addon/ForeverScout/** vid byggtillfället).</summary>
internal static class AddonBundle
{
    public static IReadOnlyList<AddonInstaller.AddonFile> Files { get; } = Load();

    private static List<AddonInstaller.AddonFile> Load()
    {
        var asm = Assembly.GetExecutingAssembly();
        var list = new List<AddonInstaller.AddonFile>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith("addon/", StringComparison.Ordinal)) continue;
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            list.Add(new AddonInstaller.AddonFile(name["addon/".Length..].Replace('\\', '/'), ms.ToArray()));
        }
        return list;
    }

    public static string? Version => AddonInstaller.BundledVersion(Files);
}
