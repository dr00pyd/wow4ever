using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using ForeverScout.Core;

namespace ForeverScout.Mac;

/// <summary>
/// Texter från samma <see cref="Strings"/> som Windows-klienten (sv + en). Där macOS behöver en annan formulering
/// (menyraden i stället för fältet, /Applications i stället för C:\…) finns nyckeln "mac.&lt;nyckel&gt;", som då vinner.
/// </summary>
internal static class Texts
{
    public static string T(string key) => Strings.Has("mac." + key) ? Strings.S("mac." + key) : Strings.S(key);

    public static string TF(string key, params object?[] args) => string.Format(Strings.Culture, T(key), args);
}

/// <summary>Fältikonens lägen (samma som på Windows).</summary>
internal enum TrayState { Normal, Uploading, Error }

/// <summary>Länkar till sajten (sv/en). Sluggarna följer lib/i18n/routes.ts – samma som Windows-klienten.</summary>
internal static class Links
{
    public static string Guide(ApiClient api) => api.Site(Strings.IsSv ? "/" : "/en").ToString();
    public static string Contribute(ApiClient api) => api.Site(Strings.IsSv ? "/bidra" : "/en/contribute").ToString();
    public static string Faq(ApiClient api) => api.Site(Strings.IsSv ? "/bidra/vanliga-fragor" : "/en/contribute/faq").ToString();
    public static string Terms(ApiClient api) => api.Site(Strings.IsSv ? "/bidra/villkor" : "/en/contribute/terms").ToString();
    public static string Privacy(ApiClient api) => api.Site(Strings.IsSv ? "/bidra/integritet" : "/en/contribute/privacy").ToString();
    public static string Report(ApiClient api) => Contribute(api) + "#rapportera-fel";
    public static string Download(ApiClient api) => api.Site(Strings.IsSv ? "/bidra/ladda-ner" : "/en/contribute/download").ToString();

    /// <summary>Öppnar bara https-länkar mot sajtens värd (macOS: /usr/bin/open via UseShellExecute).</summary>
    public static void Open(ApiClient api, string url)
    {
        if (!Uri.TryCreate(api.BaseUri, url, out var u)) return;
        if (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)) return;
        if (!string.Equals(u.Host, api.BaseUri.Host, StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); } catch { }
    }
}

/// <summary>Addonen som byggdes in i appen (client/addon/ForeverScout/** vid byggtillfället).</summary>
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

/// <summary>Inbäddade bilder (Assets/*.png, från make-icons.mjs). Ingen läsning av appens filer från disk.</summary>
internal static class Res
{
    public static Stream Open(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream("res/" + name) ?? throw new FileNotFoundException(name);

    public static Bitmap Bitmap(string name)
    {
        using var s = Open(name);
        return new Bitmap(s);
    }

    public static WindowIcon Icon(string name)
    {
        using var s = Open(name);
        return new WindowIcon(s);
    }

    public static bool Has(string name) => Assembly.GetExecutingAssembly().GetManifestResourceInfo("res/" + name) != null;
}
