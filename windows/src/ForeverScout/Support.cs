using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ForeverScout.Core;

namespace ForeverScout;

/// <summary>Färger från app/globals.css ("Lägereld"): varm nästan-svart bas, guld som enda accent.</summary>
internal static class Theme
{
    public static readonly Color Bg = ColorTranslator.FromHtml("#121013");
    public static readonly Color Bg2 = ColorTranslator.FromHtml("#17141a");
    public static readonly Color Surface = ColorTranslator.FromHtml("#1e1a1f");
    public static readonly Color Surface2 = ColorTranslator.FromHtml("#262127");
    public static readonly Color Surface3 = ColorTranslator.FromHtml("#2f2930");
    public static readonly Color Line = Color.FromArgb(255, 0x2a, 0x25, 0x27);
    public static readonly Color Text = ColorTranslator.FromHtml("#f2ece3");
    public static readonly Color Muted = ColorTranslator.FromHtml("#b9afa4");
    public static readonly Color Faint = ColorTranslator.FromHtml("#a09486");
    public static readonly Color BorderInput = ColorTranslator.FromHtml("#857a6e");
    public static readonly Color Gold = ColorTranslator.FromHtml("#e9b44c");
    public static readonly Color GoldHi = ColorTranslator.FromHtml("#ffd27a");
    public static readonly Color GoldInk = ColorTranslator.FromHtml("#1d1406");
    public static readonly Color GoldSoft = Color.FromArgb(255, 0x3a, 0x2f, 0x1f);
    public static readonly Color Ok = ColorTranslator.FromHtml("#7ad69a");
    public static readonly Color Warn = ColorTranslator.FromHtml("#f0b45a");
    public static readonly Color Err = ColorTranslator.FromHtml("#f87171");

    public static Font Display(float size) => new("Georgia", size, FontStyle.Bold);
    public static Font Ui(float size, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);

    public static Button GoldButton(string text)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Gold, ForeColor = GoldInk,
            Font = Ui(10f, FontStyle.Bold), Padding = new Padding(14, 5, 14, 5), Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = GoldHi;
        b.FlatAppearance.MouseDownBackColor = GoldHi;
        return b;
    }

    public static Button QuietButton(string text)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Surface3, ForeColor = Text,
            Font = Ui(9.5f), Padding = new Padding(10, 4, 10, 4), Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = BorderInput;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = Surface2;
        b.EnabledChanged += (_, _) => b.ForeColor = b.Enabled ? Text : Faint;
        return b;
    }

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
