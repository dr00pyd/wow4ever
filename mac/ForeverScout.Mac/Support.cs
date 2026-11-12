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
#if LINUX
    // Linux-appen (client/linux) bygger på samma källfiler: där vinner "linux.<nyckel>".
    internal const string Prefix = "linux.";
#else
    internal const string Prefix = "mac.";
#endif

    /// <summary>
    /// Ordning: Core "mac.&lt;nyckel&gt;" (resp. "linux.") → appens egna <see cref="Local"/> → Core "&lt;nyckel&gt;".
    /// </summary>
    public static string T(string key)
    {
        if (Strings.Has(Prefix + key)) return Strings.S(Prefix + key);
        if (Local.TryGetValue(Prefix + key, out var v)) return Strings.IsSv ? v.Sv : v.En;
        return Strings.S(key);
    }

    public static string TF(string key, params object?[] args) => string.Format(Strings.Culture, T(key), args);

    /// <summary>
    /// 1.0.12: texter där Core-texten (Strings.cs) nämner Windows ("Starta med Windows", "ikonen vid klockan"). De
    /// ligger här i appen, så att Core och Windows-klienten inte ändras för Mac/Linux. Samma regel som Strings: sv + en,
    /// samma platshållare som basnyckeln (testas i Release112MacTests).
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (string Sv, string En)> Local = new Dictionary<string, (string Sv, string En)>
    {
        // ---- macOS (LaunchAgent, menyraden)
        ["mac.onb.autostart"] = ("Starta vid inloggning (rekommenderas)", "Open at login (recommended)"),
        ["mac.autostart.ask"] = ("Forever Scout startar inte när du loggar in på datorn just nu. Då skickas inget när du spelar förrän du startar appen själv.\n\nVill du att den startar vid inloggning? Du kan ändra det i Inställningar.", "Forever Scout does not open when you log in right now. Nothing is sent while you play until you start the app yourself.\n\nDo you want it to open at login? You can change this in Settings."),
        ["mac.autostart.ask.title"] = ("Starta vid inloggning?", "Open at login?"),
        ["mac.gamedir.toast"] = ("Välj din WoW-katalog så att Forever Scout kan börja. Klicka på lägerelden i menyraden.", "Choose your WoW folder so Forever Scout can start. Click the campfire in the menu bar."),
        // ---- Linux (XDG-autostart, systemfältet)
        ["linux.onb.autostart"] = ("Starta vid inloggning (rekommenderas)", "Start at login (recommended)"),
        ["linux.autostart.ask"] = ("Forever Scout startar inte när du loggar in på datorn just nu. Då skickas inget när du spelar förrän du startar appen själv.\n\nVill du att den startar vid inloggning? Du kan ändra det i Inställningar.", "Forever Scout does not start when you log in right now. Nothing is sent while you play until you start the app yourself.\n\nDo you want it to start at login? You can change this in Settings."),
        ["linux.autostart.ask.title"] = ("Starta vid inloggning?", "Start at login?"),
        ["linux.gamedir.toast"] = ("Välj din WoW-katalog så att Forever Scout kan börja. Klicka på lägerelden i systemfältet eller starta Forever Scout en gång till.", "Choose your WoW folder so Forever Scout can start. Click the campfire in the system tray or start Forever Scout once more."),
    };
}

/// <summary>
/// 1.0.12 (granskningen 2026-11-11, H1/H4/H5): reglerna bakom start, status och statusrad som rena funktioner, samma
/// som i Windows-klientens ScoutApp/MainForm. Används av ScoutController och MainWindow, testas utan gränssnitt.
/// </summary>
internal static class Rules112
{
    /// <summary>
    /// Startad av användaren (Finder, Launchpad, programmenyn, terminalen) – inte av autostart (--tray, LaunchAgent och
    /// XDG-autostart startar så) och inte efter en omstart (uppdatering, språkbyte: --restarted).
    /// </summary>
    public static bool UserStarted(IReadOnlyCollection<string> args) =>
        !args.Contains("--restarted") && !args.Contains("--tray") && !args.Contains("--minimized");

    public enum AutostartAsk { Nothing, MarkAsked, Ask }

    /// <summary>
    /// H1: befintliga användare får frågan om autostart en gång – bara när de själva startat appen, aldrig över spelet
    /// efter en tyst uppdatering eller vid autostart. Är autostart redan på räknas frågan som ställd.
    /// </summary>
    public static AutostartAsk DecideAutostartAsk(bool userStarted, bool alreadyAsked, bool autostartEnabled) =>
        !userStarted || alreadyAsked ? AutostartAsk.Nothing
        : autostartEnabled ? AutostartAsk.MarkAsked
        : AutostartAsk.Ask;

    public enum GameDirAction { None, Confirm, Pick, Notify }

    /// <summary>
    /// H5: utan godkänd spelkatalog öppnas en dialog bara när användaren startat appen (eller fönstret visas). Vid
    /// autostart och i bakgrunden blir det en rad i journalen och lägereld med röd prick i stället – även för en
    /// automatiskt hittad katalog, som då bekräftas nästa gång användaren öppnar appen eller trycker på knappen.
    /// </summary>
    public static GameDirAction DecideGameDir(WowLocator.StartStep step, bool interactive) => step switch
    {
        WowLocator.StartStep.Confirm => interactive ? GameDirAction.Confirm : GameDirAction.Notify,
        WowLocator.StartStep.Pick => interactive ? GameDirAction.Pick : GameDirAction.Notify,
        _ => GameDirAction.None,
    };

    /// <summary>H4: statustexten (nyckel) när inget laddas upp, inget fel finns och inget omförsök väntar.</summary>
    public static string IdleStatusKey(bool hasDir, bool paused, bool autoUpload, bool uploadedBefore) =>
        !hasDir ? "game.none"
        : paused ? "paused"
        : !autoUpload ? "auto.off"
        : uploadedBefore ? "last"
        : "never";

    /// <summary>
    /// H4: "Allt fungerar" (grönt) bara när det stämmer – spelkatalog, addonen på plats, addonen har sparat, automatisk
    /// uppladdning på och inga fel eller omförsök.
    /// </summary>
    public static bool Healthy(bool normalState, bool hasDir, bool addonInstalled, bool hasSavedVariables, bool hasError, bool retryPending, bool autoUploadActive) =>
        normalState && hasDir && addonInstalled && hasSavedVariables && !hasError && !retryPending && autoUploadActive;

    /// <summary>H4/H5: "Nästa synk"-fältet i statusraden (nyckel). "retry" tar tiden som platshållare.</summary>
    public static string FooterNextKey(bool retryPending, bool hasDir, bool autoUploadActive) =>
        retryPending ? "retry"
        : !hasDir ? "footer.next.nodir"
        : !autoUploadActive ? "footer.next.manual"
        : "footer.next";

    /// <summary>H5: huvudknappen (nyckel). Utan spelkatalog är den vägen framåt och säger det.</summary>
    public static string MainButtonKey(bool busy, bool hasDir) => busy ? "uploading" : !hasDir ? "pick.dir" : "upload";

    /// <summary>
    /// M5: felet som visas – uppladdningens, annars addonens. Addonens fel (kunde inte installeras, väntar på en
    /// signerad version) raderas inte av nästa synk, bara när addonen är på plats.
    /// </summary>
    public static string? ShownError(string? uploadError, string? addonError) => uploadError ?? addonError;

    /// <summary>M5: addonens fel försvinner först när en addon är installerad och den versionen inte är spärrad.</summary>
    public static bool AddonErrorResolved(string? installedVersion, IEnumerable<string> blocked) =>
        installedVersion != null && !blocked.Contains(installedVersion);
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
