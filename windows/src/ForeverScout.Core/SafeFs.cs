using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ForeverScout.Core;

/// <summary>Försök att röra en sökväg eller registernyckel utanför det tillåtna.</summary>
public sealed class SafeFsException(string reason, string path) : ScoutException(ErrorCodes.AccessDenied, reason)
{
    public string Reason { get; } = reason;
    /// <summary>Den nekade sökvägen (visas aldrig i UI – kan innehålla kontonamn).</summary>
    public string DeniedPath { get; } = path;
}

/// <summary>
/// ENDA vägen till filsystemet och registret för hela appen (ett test kontrollerar att ingen annan fil använder
/// File./Directory./FileStream/Registry./Environment.GetFolderPath …). Allt annat än följande ger <see cref="SafeFsException"/>:
/// <list type="number">
/// <item>Under vald spelkatalog (<see cref="SetGameDir"/>):
///   <c>Interface\AddOns\ForeverScout\**</c> (läsa/skriva – installation, inkl. ForeverScout.new/.old under bytet),
///   <c>WTF\Account\*\SavedVariables\ForeverScout.lua</c> (läsa),
///   <c>WTF\Account\*\SavedVariables\Auctionator.lua</c> och <c>Auc-Stat-Simple.lua</c> (läsa – bara när "Dela
///   auktionspriser" är på, <see cref="AuctionFilesAllowed"/>),
///   <c>Cache\ADB\*\DBCache.bin</c> (läsa),
///   samt kataloglistning av just de mellanliggande katalogerna (Interface, Interface\AddOns, WTF\Account,
///   WTF\Account\*\SavedVariables, Cache\ADB) för att hitta filerna.</item>
/// <item>Sökning efter spelkatalogen (bara existens/kataloglistning, inget innehåll): registrerade sökrötter
///   (registrets InstallPath, standardsökvägar, användarens val), deras direkta underkataloger,
///   <c>&lt;under&gt;\Interface</c>, <c>&lt;under&gt;\WTF</c> och <c>&lt;rot&gt;\.build.info</c>.</item>
/// <item><c>%APPDATA%\ForeverScout\**</c>: appens inställningar, nyckel, logg (läsa/skriva).</item>
/// <item>Appens egen exe och dess <c>.new.exe</c>/<c>.old.exe</c> (självuppdatering).</item>
/// </list>
/// Sökvägar normaliseras (GetFullPath, skiftlägesokänsligt). <c>..</c>, UNC/nätverks- och enhetssökvägar (\\…, \\?\…),
/// alternativa dataströmmar (<c>:</c>) och symlänkar/junctions (reparse points) inom de tillåtna områdena nekas.
/// <para><b>macOS</b> (client/mac): samma regler under spelkatalogen (t.ex. <c>/Applications/World of Warcraft/_forever_</c>),
/// med <c>/</c> som avgränsare och skiftlägesokänslig jämförelse (APFS är oftast skiftlägesokänsligt). I stället för
/// %APPDATA%, registret och exe-bytet gäller: <c>~/Library/Application Support/ForeverScout/**</c> (katalogen 0700),
/// exakt filen <c>~/Library/LaunchAgents/se.wow4ever.foreverscout.plist</c> ("Starta vid inloggning"), det egna
/// <c>.app</c>-paketet (bara som helhet, för bytet) och uppdateringskatalogen <c>.ForeverScout-update</c> bredvid det.
/// <c>..</c>, relativa sökvägar, <c>..namedfork</c> (resursgrenar) och symlänkar nekas. Windows-vägen är oförändrad.</para>
/// <para><b>Kontroll efter öppning (L11, 1.0.7).</b> Reglerna ovan kontrolleras på sökvägen; mellan kontrollen och
/// användningen kan en annan lokal användare med skrivrätt i spelkatalogen byta en katalog mot en junction. På Windows
/// öppnas därför filer via handtag och handtagets verkliga sökväg (GetFinalPathNameByHandle) jämförs med det tillåtna
/// områdets – läsning sker ur samma handtag, skrivning går via temporärfil + namnbyte på handtaget, radering via
/// handtaget. Katalogoperationerna (skapa, radera, flytta – bara våra egna fasta namn) kontrolleras via handtag precis
/// före (resp. efter) operationen. På macOS görs symlänkskontrollen om efter öppningen, se <see cref="VerifyOpened"/>.</para>
/// </summary>
public static class SafeFs
{
    private static readonly object Lock = new();
    private static readonly HashSet<string> GameRoots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ProbeRoots = new(StringComparer.OrdinalIgnoreCase);
    private static string? _appData;

    public enum Access { Probe, Read, Write }

    /// <summary>Unix-sökvägar (macOS). Windows: false, och då körs exakt samma kod som före macOS-stödet.</summary>
    [UnsupportedOSPlatformGuard("windows")]
    internal static bool Unix => !OperatingSystem.IsWindows();

    /// <summary>Katalogavgränsaren i normaliserade sökvägar.</summary>
    private static char Sep => Unix ? '/' : '\\';

    /// <summary>macOS: paket-id (Info.plist CFBundleIdentifier, LaunchAgent-etikett, nyckelringens tjänst).</summary>
    public const string MacBundleId = "se.wow4ever.foreverscout";

    // ------------------------------------------------------------------ konfiguration

    /// <summary>%APPDATA%\ForeverScout (i Debug-byggen kan FOREVERSCOUT_DATADIR peka om den för test).</summary>
    public static string AppDataDir
    {
        get
        {
            lock (Lock)
            {
                if (_appData != null) return _appData;
                string dir = Linux
                    ? LinuxConfigHome + "/ForeverScout"
                    : Unix
                    ? Path.Combine(Home, "Library", "Application Support", "ForeverScout")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ForeverScout");
#if DEBUG
                var env = Environment.GetEnvironmentVariable("FOREVERSCOUT_DATADIR");
                if (!string.IsNullOrWhiteSpace(env)) dir = env;
#endif
                var n = Normalize(dir);
                if (Unix) CreatePrivateDirectory(n); else Directory.CreateDirectory(n);
                _appData = RealPath(n);
                return _appData;
            }
        }
    }

    /// <summary>Byter appdatakatalog (tester). Katalogen skapas.</summary>
    public static void UseAppDataDir(string dir)
    {
        lock (Lock)
        {
            var n = Normalize(dir);
            if (Unix) CreatePrivateDirectory(n); else Directory.CreateDirectory(n);
            _appData = RealPath(n);
        }
    }

    /// <summary>macOS: hemkatalogen ($HOME).</summary>
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Linux (client/linux). Unix-reglerna gäller också där; det här väljer bara sökvägarna.</summary>
    [SupportedOSPlatformGuard("linux")]
    internal static bool Linux => OperatingSystem.IsLinux();

    /// <summary>Linux: $XDG_CONFIG_HOME om den är en absolut sökväg, annars ~/.config (XDG Base Directory).</summary>
    private static string LinuxConfigHome
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrWhiteSpace(xdg) && xdg.StartsWith('/'))
            {
                try { return NormalizeUnix(xdg); } catch (SafeFsException) { }
            }
            return NormalizeUnix(Home.TrimEnd('/') + "/.config");
        }
    }

    /// <summary>macOS: appens datakatalog skapas (och hålls) med rättigheterna 0700 – bara användaren själv.</summary>
    [UnsupportedOSPlatform("windows")]
    private static void CreatePrivateDirectory(string dir)
    {
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(dir, Private);
        File.SetUnixFileMode(dir, Private);
    }

    /// <summary>
    /// Väljer spelkatalogen (t.ex. …\World of Warcraft\_forever_) och ersätter tidigare val. Returnerar den verkliga
    /// sökvägen (symlänkar/junctions i själva valet följs EN gång här; under den nekas de). Alla sökvägar som appen
    /// sedan bygger ska utgå från returvärdet.
    /// </summary>
    public static string? SetGameDir(string? dir)
    {
        string? real = string.IsNullOrWhiteSpace(dir) ? null : RealPath(Normalize(dir));
        lock (Lock)
        {
            GameRoots.Clear();
            if (real != null) GameRoots.Add(real);
        }
        if (real != null && Path.GetDirectoryName(real) is { } parent) AllowProbeRoot(parent);
        return real;
    }

    private static volatile bool _auctionFiles;

    /// <summary>
    /// Auktionsaddonernas prisfiler (<c>WTF\Account\*\SavedVariables\Auctionator.lua</c> och <c>Auc-Stat-Simple.lua</c>,
    /// se <see cref="AuctionData.Readers"/>) får läsas BARA när användaren kryssat i "Dela auktionspriser" (av som
    /// standard). Appen sätter det från inställningarna vid start och när de sparas; annars nekas filerna som alla andra.
    /// </summary>
    public static bool AuctionFilesAllowed
    {
        get => _auctionFiles;
        set => _auctionFiles = value;
    }

    /// <summary>Lägger till en spelkatalog utan att ta bort andra (tester).</summary>
    public static string AllowGameDir(string dir)
    {
        var real = RealPath(Normalize(dir));
        lock (Lock) GameRoots.Add(real);
        return real;
    }

    /// <summary>Registrerar en sökrot för att hitta spelkatalogen (bara existens/listning). Returnerar verklig sökväg.</summary>
    public static string? AllowProbeRoot(string dir)
    {
        try
        {
            var real = RealPath(Normalize(dir));
            lock (Lock) ProbeRoots.Add(real);
            return real;
        }
        catch (SafeFsException) { return null; }
    }

    /// <summary>Följer symlänkar/junctions i en (redan normaliserad) sökvägs befintliga komponenter.</summary>
    public static string RealPath(string full)
    {
        if (Unix) return RealPathUnix(full, 0);
        var parts = full.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var cur = parts[0] + "\\";
        for (int i = 1; i < parts.Length; i++)
        {
            cur = Path.Combine(cur, parts[i]);
            try
            {
                var di = new DirectoryInfo(cur);
                if (di.Exists && (di.Attributes & FileAttributes.ReparsePoint) != 0 && di.ResolveLinkTarget(returnFinalTarget: true) is { } t)
                {
                    var target = Path.GetFullPath(t.FullName).TrimEnd('\\');
                    if (target.StartsWith(@"\\", StringComparison.Ordinal)) throw new SafeFsException("unc-or-device", full);
                    cur = target;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return cur.Length > 3 ? cur.TrimEnd('\\') : cur;
    }

    /// <summary>macOS: som <see cref="RealPath"/> – symlänkar i valets befintliga komponenter följs (t.ex. /var → /private/var).</summary>
    private static string RealPathUnix(string full, int depth)
    {
        if (depth > 16) throw new SafeFsException("link-loop", full);
        var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var cur = "/";
        for (int i = 0; i < parts.Length; i++)
        {
            cur = cur == "/" ? "/" + parts[i] : cur + "/" + parts[i];
            try
            {
                var fi = new FileInfo(cur);
                if (fi.LinkTarget is not null && fi.ResolveLinkTarget(returnFinalTarget: true) is { } t)
                {
                    var rest = string.Join('/', parts.Skip(i + 1));
                    var target = RealPathUnix(NormalizeUnix(t.FullName), depth + 1);
                    return rest.Length == 0 ? target : RealPathUnix(NormalizeUnix(target + "/" + rest), depth + 1);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return cur;
    }

    /// <summary>
    /// macOS: absolut sökväg med <c>/</c>. Nekar tomt, relativt, <c>..</c>, <c>//</c> i början, NUL, jokertecken,
    /// bakstreck och resursgrenar (<c>..namedfork</c>). <c>.</c> och dubbla <c>/</c> tas bort. Ren strängbehandling
    /// (ingen GetFullPath), så att reglerna kan testas på alla plattformar.
    /// </summary>
    internal static string NormalizeUnix(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new SafeFsException("empty", path ?? "");
        var p = path.Trim();
        if (!p.StartsWith('/')) throw new SafeFsException("not-absolute", path);
        if (p.StartsWith("//", StringComparison.Ordinal)) throw new SafeFsException("unc-or-device", path);
        if (p.Contains('\0') || p.Contains('\\') || p.Contains('*') || p.Contains('?')) throw new SafeFsException("chars", path);
        var kept = new List<string>();
        foreach (var seg in p.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == "..") throw new SafeFsException("dotdot", path);
            if (seg == ".") continue;
            if (seg.StartsWith("..namedfork", StringComparison.OrdinalIgnoreCase)) throw new SafeFsException("stream", path);
            kept.Add(seg);
        }
        return "/" + string.Join('/', kept);
    }

    // ------------------------------------------------------------------ policy

    /// <summary>Normaliserar och nekar trasiga/farliga former. Kastar <see cref="SafeFsException"/>.</summary>
    public static string Normalize(string path)
    {
        if (Unix) return NormalizeUnix(path);
        if (string.IsNullOrWhiteSpace(path)) throw new SafeFsException("empty", path ?? "");
        var p = path.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) throw new SafeFsException("unc-or-device", path);
        if (p.Split('\\').Any(seg => seg == "..")) throw new SafeFsException("dotdot", path);
        if (p.Length < 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || p[2] != '\\') throw new SafeFsException("not-absolute", path);
        if (p.IndexOf(':', 2) >= 0) throw new SafeFsException("stream", path);
        if (p.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || p.Contains('*') || p.Contains('?')) throw new SafeFsException("chars", path);
        string full;
        try { full = Path.GetFullPath(p); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { throw new SafeFsException("invalid", path); }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new SafeFsException("unc-or-device", path);
        return full.Length > 3 ? full.TrimEnd('\\') : full;
    }

    private static string[] Rel(string full, string root)
    {
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return [];
        if (!full.StartsWith(root + Sep, StringComparison.OrdinalIgnoreCase)) return null!;
        return full[(root.Length + 1)..].Split(Sep);
    }

    private static bool Eq(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Vad får göras med en sökväg under spelkatalogen? (relativ sökväg i delar)</summary>
    private static Access? GameRule(string[] r, bool isDir)
    {
        int n = r.Length;
        if (n == 0) return Access.Probe; // spelkatalogen själv (existens, lista Wow*.exe är INTE tillåtet – se nedan)
        // Interface, Interface\AddOns: skapa/lista
        if (Eq(r[0], "Interface"))
        {
            if (n == 1) return Access.Write;
            if (Eq(r[1], "AddOns"))
            {
                if (n == 2) return Access.Write;
                var a = r[2];
                if (Eq(a, "ForeverScout") || Eq(a, "ForeverScout.new") || Eq(a, "ForeverScout.old")) return Access.Write;
            }
            return null;
        }
        if (Eq(r[0], "WTF"))
        {
            if (n == 1) return Access.Probe;
            if (!Eq(r[1], "Account")) return null;
            if (n == 2) return Access.Probe;                                  // lista kontokataloger
            if (n == 3) return isDir ? Access.Probe : null;                   // WTF\Account\<konto> (bara katalog)
            if (!Eq(r[3], "SavedVariables")) return null;
            if (n == 4) return Access.Probe;
            if (n == 5 && Eq(r[4], "ForeverScout.lua")) return Access.Read;
            if (n == 5 && _auctionFiles && AuctionData.Readers.Any(a => Eq(r[4], a.FileName))) return Access.Read; // bara när delningen är på
            return null;
        }
        if (Eq(r[0], "Cache"))
        {
            if (n == 1) return Access.Probe;
            if (!Eq(r[1], "ADB")) return null;
            if (n == 2) return Access.Probe;
            if (n == 3) return isDir ? Access.Probe : null;
            if (n == 4 && Eq(r[3], "DBCache.bin")) return Access.Read;
            return null;
        }
        return null;
    }

    /// <summary>Sökning efter spelkatalogen: rot, rot\&lt;under&gt;, rot\&lt;under&gt;\Interface|WTF, rot\.build.info.</summary>
    private static Access? ProbeRule(string[] r, bool isDir)
    {
        int n = r.Length;
        if (n == 0) return Access.Probe;
        if (n == 1) return isDir || Eq(r[0], ".build.info") ? Access.Probe : null;
        if (n == 2 && isDir && (Eq(r[1], "Interface") || Eq(r[1], "WTF"))) return Access.Probe;
        return null;
    }

    /// <summary>Bara för tester: låtsas att appens exe ligger här (Environment.ProcessPath är testvärden i tester).</summary>
    internal static string? OwnExeOverride { get; set; }

    private static string OwnExe => Normalize(OwnExeOverride ?? Environment.ProcessPath ?? throw new SafeFsException("no-exe", ""));

    /// <summary>
    /// Filerna självuppdateringen får röra: exe, .new.exe (+ dess .tmp under skrivning), .old.exe och
    /// .old-&lt;14 siffror&gt;.exe (tidsstämpel när .old.exe är låst). Inga andra filer i exe:ns mapp.
    /// </summary>
    private static bool IsOwnExeFile(string full)
    {
        if (Unix) return false; // macOS byter hela .app-paketet (MacRule), inga .new/.old-filer bredvid binären
        var exe = OwnExe;
        var stem = exe[..^4];
        if (Eq(full, exe) || Eq(full, stem + ".new.exe") || Eq(full, stem + ".new.exe.tmp") || Eq(full, stem + ".old.exe")) return true;
        if (full.Length != stem.Length + ".old-".Length + 14 + ".exe".Length) return false;
        if (!full.StartsWith(stem + ".old-", StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var ts = full.Substring(stem.Length + 5, 14);
        return ts.All(char.IsAsciiDigit);
    }

    /// <summary>Självuppdateringens filer som finns bredvid exe:n (.new.exe, .new.exe.tmp, .old.exe, .old-*.exe).</summary>
    public static List<string> OwnExeLeftovers()
    {
        if (Unix) return [];
        var exe = OwnExe;
        var dir = Path.GetDirectoryName(exe)!;
        var stemName = Path.GetFileNameWithoutExtension(exe);
        var list = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, stemName + ".*"))
            {
                var full = Normalize(f);
                if (!Eq(full, exe) && IsOwnExeFile(full)) list.Add(full);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>Kontrollerar en sökväg mot reglerna och returnerar den normaliserade sökvägen.</summary>
    public static string Check(string path, Access want, bool isDir) => CheckScoped(path, want, isDir).Full;

    /// <summary>Som <see cref="Check"/>, men returnerar även det tillåtna område (rot) som sökvägen godkändes under.</summary>
    private static (string Full, string Scope) CheckScoped(string path, Access want, bool isDir)
    {
        var full = Normalize(path);
        string? scopeRoot = null;
        Access? allowed = null;
        var app = AppDataDir;
        lock (Lock)
        {
            if (Rel(full, app) is not null) { allowed = Access.Write; scopeRoot = app; }
            if (allowed is null)
                foreach (var g in GameRoots)
                    if (Rel(full, g) is { } rg && GameRule(rg, isDir) is { } a) { allowed = a; scopeRoot = g; break; }
            if (allowed is null)
                foreach (var pr in ProbeRoots)
                    if (Rel(full, pr) is { } rp && ProbeRule(rp, isDir) is { } a) { allowed = a; scopeRoot = pr; break; }
        }
        if (allowed is null && Unix && MacRule(full, isDir) is ({ } ma, { } ms)) { allowed = ma; scopeRoot = ms; }
        if (allowed is null && !isDir && IsOwnExeFile(full)) { allowed = Access.Write; scopeRoot = full; }
        if (allowed is null || allowed < want) throw new SafeFsException(allowed is null ? "outside" : "access", full);
        RefuseReparsePoints(full, scopeRoot!);
        return (full, scopeRoot!);
    }

    // ------------------------------------------------------------------ macOS: paketet, uppdateringen och LaunchAgent

    /// <summary>Bara för tester: låtsas att appens .app-paket ligger här.</summary>
    internal static string? OwnBundleOverride { get; set; }

    /// <summary>
    /// macOS: det .app-paket som appen körs från (…/Forever Scout.app), eller null (t.ex. <c>dotnet run</c> utan paket).
    /// Binären ligger i <c>Contents/MacOS/</c> (eller <c>Contents/MacOS/&lt;arkitektur&gt;/</c> i det universella paketet).
    /// </summary>
    public static string? OwnBundle
    {
        get
        {
            if (!Unix) return null;
            if (OwnBundleOverride is { } o) return NormalizeUnix(o);
            if (OwnExeOverride is { } e) return BundleOf(e);
            // Läses en gång: under bytet flyttas paketet, och då får regeln inte börja peka på den gamla kopian.
            lock (Lock)
            {
                if (!_ownBundleRead) { _ownBundle = Environment.ProcessPath is { } exe ? BundleOf(exe) : null; _ownBundleRead = true; }
                return _ownBundle;
            }
        }
    }

    private static string? _ownBundle;
    private static bool _ownBundleRead;

    /// <summary>Närmaste överordnade <c>*.app</c>-katalog som innehåller <c>Contents/MacOS</c> (ren strängbehandling).</summary>
    internal static string? BundleOf(string exePath)
    {
        string full;
        try { full = NormalizeUnix(exePath); } catch (SafeFsException) { return null; }
        var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 3; i >= 0; i--)
            if (parts[i].EndsWith(".app", StringComparison.OrdinalIgnoreCase) && parts[i].Length > 4
                && Eq(parts[i + 1], "Contents") && Eq(parts[i + 2], "MacOS"))
                return "/" + string.Join('/', parts.Take(i + 1));
        return null;
    }

    /// <summary>macOS: uppdateringens arbetskatalog bredvid paketet (samma volym, så att bytet blir ett namnbyte).</summary>
    public static string? BundleUpdateDir => OwnBundle is { } b ? UpdateDirFor(b) : null;

    internal static string UpdateDirFor(string bundle) => (Path.GetDirectoryName(bundle) ?? "/").Replace('\\', '/').TrimEnd('/') + "/.ForeverScout-update";

    /// <summary>macOS: "Starta vid inloggning" = exakt den här filen.</summary>
    public static string LaunchAgentPath => NormalizeUnix(Home.TrimEnd('/') + "/Library/LaunchAgents/" + MacBundleId + ".plist");

    /// <summary>Vad får göras med en sökväg som hör till macOS-specifika delar? (åtkomst, område för symlänkskontrollen)</summary>
    private static (Access?, string?) MacRule(string full, bool isDir)
    {
        var bundle = OwnBundle;
        if (bundle is not null)
        {
            if (isDir && Eq(full, bundle)) return (Access.Write, full);                // hela paketet flyttas vid bytet
            var upd = UpdateDirFor(bundle);
            if (Rel(full, upd) is not null) return (Access.Write, upd);              // .ForeverScout-update/**
        }
        var agent = Linux ? LinuxAutostartPath : LaunchAgentPath;
        if (!isDir && (Eq(full, agent) || Eq(full, agent + ".tmp"))) return (Access.Write, full);
        if (isDir && Eq(full, NormalizeUnix(Path.GetDirectoryName(agent)!.Replace('\\', '/')))) return (Access.Write, full);
        return (null, null);
    }

    /// <summary>Nekar symlänkar/junctions i de komponenter som ligger inom det tillåtna området.</summary>
    private static void RefuseReparsePoints(string full, string scopeRoot)
    {
        var cur = full;
        while (cur.Length >= scopeRoot.Length)
        {
            try
            {
                var attr = File.GetAttributes(cur);
                if ((attr & FileAttributes.ReparsePoint) != 0) throw new SafeFsException("reparse-point", cur);
                if (Unix && new FileInfo(cur).LinkTarget is not null) throw new SafeFsException("reparse-point", cur);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (Eq(cur, scopeRoot)) break;
            var parent = Path.GetDirectoryName(cur);
            if (parent is null) break;
            cur = parent;
        }
    }

    // ------------------------------------------------------------------ operationer

    public static bool DirectoryExists(string path)
    {
        try { return Directory.Exists(Check(path, Access.Probe, isDir: true)); }
        catch (SafeFsException) { return false; }
    }

    public static bool FileExists(string path)
    {
        try { return File.Exists(Check(path, Access.Probe, isDir: false)); }
        catch (SafeFsException) { return false; }
    }

    /// <summary>Underkataloger (bara namn/sökvägar), där listning är tillåten.</summary>
    public static string[] GetDirectories(string path)
    {
        var p = Check(path, Access.Probe, isDir: true);
        try { return Directory.Exists(p) ? Directory.GetDirectories(p) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Filer i en katalog där filerna själva är tillåtna att läsa (t.ex. *.toc i addonkatalogen).</summary>
    public static string[] GetFiles(string path, string pattern)
    {
        var p = Check(path, Access.Read, isDir: true);
        try { return Directory.Exists(p) ? Directory.GetFiles(p, pattern).Where(f => { try { Check(f, Access.Read, false); return true; } catch (SafeFsException) { return false; } }).ToArray() : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Storlek och ändringstid (null om filen saknas).</summary>
    public static (long Length, DateTime LastWriteUtc)? Stat(string path)
    {
        var p = Check(path, Access.Read, isDir: false);
        try
        {
            var fi = new FileInfo(p);
            return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Läser hela filen med FileShare.ReadWrite (spelet kan ha den öppen). L11: filen öppnas först och sedan kontrolleras
    /// vart handtaget faktiskt pekar (<see cref="VerifyOpened"/>); innehållet läses ur samma handtag.
    /// </summary>
    public static byte[] ReadAllBytes(string path, long maxBytes = 64L * 1024 * 1024)
    {
        var (p, scope) = CheckScoped(path, Access.Read, isDir: false);
        BeforeOpen?.Invoke(p);
        using var h = File.OpenHandle(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        VerifyOpened(h, p, scope);
        using var fs = new FileStream(h, FileAccess.Read, bufferSize: 1);
        if (fs.Length > maxBytes) throw new ScoutException(ErrorCodes.FileTooLarge);
        // 1.0.11: rakt in i en array av rätt storlek (förr: MemoryStream som dubblar kapaciteten + ToArray-kopia –
        // en fil på 20 MB kostade ~50 MB i topp). Växer filen under läsningen läses resten som förut.
        var buf = new byte[fs.Length];
        int read = 0;
        while (read < buf.Length)
        {
            int n = fs.Read(buf, read, buf.Length - read);
            if (n == 0) break;
            read += n;
        }
        if (read < buf.Length) return buf.AsSpan(0, read).ToArray(); // filen krympte
        int next = fs.ReadByte();
        if (next < 0) return buf;
        using var ms = new MemoryStream();
        ms.Write(buf);
        ms.WriteByte((byte)next);
        fs.CopyTo(ms);
        if (ms.Length > maxBytes) throw new ScoutException(ErrorCodes.FileTooLarge);
        return ms.ToArray();
    }

    /// <summary>
    /// SHA-256 (hex, gemener) av filens innehåll, läst i block om 64 KB ur samma kontrollerade handtag som
    /// <see cref="ReadAllBytes"/> – så att en oförändrad fil kan kännas igen utan att hela filen hamnar i minnet.
    /// </summary>
    public static string Sha256Hex(string path, long maxBytes = 64L * 1024 * 1024)
    {
        var (p, scope) = CheckScoped(path, Access.Read, isDir: false);
        BeforeOpen?.Invoke(p);
        using var h = File.OpenHandle(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        VerifyOpened(h, p, scope);
        using var fs = new FileStream(h, FileAccess.Read, bufferSize: 1);
        if (fs.Length > maxBytes) throw new ScoutException(ErrorCodes.FileTooLarge);
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var block = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long total = 0;
            int n;
            while ((n = fs.Read(block, 0, block.Length)) > 0)
            {
                total += n;
                if (total > maxBytes) throw new ScoutException(ErrorCodes.FileTooLarge);
                sha.AppendData(block, 0, n);
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(block); }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>Läser text som UTF-8; en ev. BOM tas bort (t.ex. om settings.json sparats av ett annat program).</summary>
    public static string ReadAllText(string path) => System.Text.Encoding.UTF8.GetString(ReadAllBytes(path)).TrimStart((char)0xFEFF);

    /// <summary>Skriver atomärt (temporärfil + namnbyte).</summary>
    public static void WriteAllBytes(string path, byte[] data) => WriteAllBytes(path, data, null);

    /// <summary>
    /// Som ovan; <paramref name="retry"/> (t.ex. IoRetry) omsluter namnbytet, som antivirus kan blockera kort.
    /// Windows (L11): temporärfilen skapas, kontrolleras, skrivs och döps om via ETT handtag (namnbytet sker i samma
    /// katalog och anger bara det nya filnamnet), så ingen sökväg slås upp igen mellan kontroll och användning.
    /// </summary>
    public static void WriteAllBytes(string path, byte[] data, Action<Action>? retry)
    {
        var p = Check(path, Access.Write, isDir: false);
        var (tmp, tmpScope) = CheckScoped(p + ".tmp", Access.Write, isDir: false);
        BeforeOpen?.Invoke(tmp);
        if (Unix)
        {
            File.WriteAllBytes(tmp, data);
            void MoveUnix() => File.Move(tmp, p, overwrite: true);
            if (retry is null) MoveUnix(); else retry(MoveUnix);
            Check(p, Access.Write, isDir: false); // macOS: symlänkskontrollen igen efter användning (se VerifyOpened)
            return;
        }
        using var h = Win.CreateFileW(tmp, Win.GenericWrite | Win.Delete | Win.FileReadAttributes, Win.FileShareRead, IntPtr.Zero,
            Win.CreateAlways, Win.FileAttributeNormal | Win.FlagOpenReparsePoint, IntPtr.Zero);
        if (h.IsInvalid) throw Win.LastError();
        try
        {
            VerifyFinalPath(h, tmp, tmpScope);
            if ((File.GetAttributes(h) & FileAttributes.ReparsePoint) != 0) throw new SafeFsException("reparse-point", tmp);
        }
        catch (SafeFsException)
        {
            Win.TryDeleteByHandle(h); // det vi just skapat (eller en planterad länk) tas bort – aldrig länkens mål
            throw;
        }
        RandomAccess.Write(h, data, 0);
        var name = Path.GetFileName(p);
        void Rename() => Win.RenameByHandle(h, name, replace: true);
        if (retry is null) Rename(); else retry(Rename);
    }

    public static void WriteAllText(string path, string text) => WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Lägger till text (UTF-8, BOM i en ny fil som förut). Handtaget kontrolleras innan något skrivs (L11).</summary>
    public static void AppendText(string path, string text)
    {
        var (p, scope) = CheckScoped(path, Access.Write, isDir: false);
        BeforeOpen?.Invoke(p);
        using var h = File.OpenHandle(p, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        VerifyOpened(h, p, scope);
        long at = RandomAccess.GetLength(h);
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        if (at == 0) bytes = [.. System.Text.Encoding.UTF8.GetPreamble(), .. bytes];
        RandomAccess.Write(h, bytes, at);
    }

    /// <summary>
    /// Raderar en fil. Windows (L11): själva posten öppnas (en ev. länk, aldrig dess mål), kontrolleras och raderas via handtaget.
    /// </summary>
    public static void DeleteFile(string path)
    {
        var (p, scope) = CheckScoped(path, Access.Write, isDir: false);
        if (!File.Exists(p)) return;
        if (Unix) { File.Delete(p); return; }
        BeforeOpen?.Invoke(p);
        using var h = Win.CreateFileW(p, Win.Delete | Win.FileReadAttributes, Win.FileShareAll, IntPtr.Zero, Win.OpenExisting, Win.FlagOpenReparsePoint, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            if (err is Win.ErrorFileNotFound or Win.ErrorPathNotFound) return;
            throw Win.ErrorFor(err);
        }
        VerifyFinalPath(h, p, scope);
        Win.DeleteByHandle(h);
    }

    /// <summary>Flyttar/döper om en fil. Windows (L11): källan och målkatalogen kontrolleras via handtag precis före flytten.</summary>
    public static void MoveFile(string from, string to)
    {
        var (f, fScope) = CheckScoped(from, Access.Write, isDir: false);
        var (t, tScope) = CheckScoped(to, Access.Write, isDir: false);
        BeforeOpen?.Invoke(f);
        if (!Unix)
        {
            VerifyExisting(f, fScope, linkItself: true);
            if (Path.GetDirectoryName(t) is { } td) VerifyExisting(td, tScope, linkItself: false);
        }
        File.Move(f, t);
    }

    /// <summary>Skapar en katalog. Windows (L11): den skapade katalogen kontrolleras via handtag efteråt.</summary>
    public static void CreateDirectory(string path)
    {
        var (p, scope) = CheckScoped(path, Access.Write, isDir: true);
        Directory.CreateDirectory(p);
        if (!Unix) VerifyExisting(p, scope, linkItself: false);
    }

    /// <summary>Raderar en katalog rekursivt. Windows (L11): katalogen kontrolleras via handtag precis före raderingen.</summary>
    public static void DeleteDirectory(string path)
    {
        var (p, scope) = CheckScoped(path, Access.Write, isDir: true);
        if (!Directory.Exists(p)) return;
        BeforeOpen?.Invoke(p);
        if (!Unix) VerifyExisting(p, scope, linkItself: false);
        Directory.Delete(p, recursive: true);
    }

    /// <summary>Flyttar en katalog. Windows (L11): källan och målkatalogen kontrolleras via handtag precis före flytten.</summary>
    public static void MoveDirectory(string from, string to)
    {
        var (f, fScope) = CheckScoped(from, Access.Write, isDir: true);
        var (t, tScope) = CheckScoped(to, Access.Write, isDir: true);
        BeforeOpen?.Invoke(f);
        if (!Unix)
        {
            VerifyExisting(f, fScope, linkItself: false);
            if (Path.GetDirectoryName(t) is { } td) VerifyExisting(td, tScope, linkItself: false);
        }
        Directory.Move(f, t);
    }

    // ------------------------------------------------------------------ kontroll efter öppning (L11)

    /// <summary>Bara för tester: anropas mellan regelkontrollen och öppningen (för att byta ut en katalog mot en länk där).</summary>
    internal static Action<string>? BeforeOpen { get; set; }

    /// <summary>
    /// Kontrollen EFTER öppning. Windows: <see cref="VerifyFinalPath"/> (GetFinalPathNameByHandle). macOS: symlänkskontrollen
    /// görs om efter öppningen (<see cref="RefuseReparsePoints"/>). En riktig handtag-till-sökväg-kontroll
    /// (<c>fcntl(F_GETPATH)</c>) görs inte: <c>fcntl</c> är variadisk, och på Apple arm64 skickas variadiska argument på
    /// stacken – ett vanligt P/Invoke-anrop lägger bufferten i fel register och kan skriva i godtyckligt minne. Det kräver
    /// en egen signatur per arkitektur (eller en liten C-shim) och kan inte provas från Windows-byggdatorn.
    /// </summary>
    private static void VerifyOpened(SafeFileHandle h, string full, string scope)
    {
        if (Unix) { RefuseReparsePoints(full, scope); return; }
        VerifyFinalPath(h, full, scope);
    }

    /// <summary>Windows: öppnar en befintlig fil eller katalog (bara attribut) och kontrollerar vart den faktiskt pekar.</summary>
    /// <param name="linkItself">Öppna en ev. länk själv (radera/flytta posten) i stället för att följa den.</param>
    private static void VerifyExisting(string full, string scope, bool linkItself)
    {
        using var h = Win.OpenForQuery(full, linkItself);
        if (h.IsInvalid) throw Win.LastError();
        VerifyFinalPath(h, full, scope);
    }

    /// <summary>
    /// Windows (L11): sökvägen som handtaget faktiskt pekar på (GetFinalPathNameByHandle, efter alla junctions och
    /// symlänkar) måste vara exakt det tillåtna områdets verkliga sökväg + samma relativa sökväg som kontrollerades.
    /// Byttes en katalog ut mot en junction mellan kontrollen och öppningen pekar handtaget någon annanstans → nekas.
    /// Både området och filen slås upp med samma flaggor (DOS-namn, annars volym-GUID), så korta 8.3-namn, subst-enheter
    /// och mappade enheter jämförs i samma form.
    /// </summary>
    private static void VerifyFinalPath(SafeFileHandle h, string full, string scope)
    {
        // Området: en katalog (appdata, spelkatalogen …) – eller, när området är en enskild fil (exe:n), dess katalog.
        var s = scope.TrimEnd(Sep);
        var root = full.StartsWith(s + Sep, StringComparison.OrdinalIgnoreCase) ? s : (Path.GetDirectoryName(scope) ?? "").TrimEnd(Sep);
        if (root.Length == 0 || !(Eq(full, root) || full.StartsWith(root + Sep, StringComparison.OrdinalIgnoreCase)))
            throw new SafeFsException("final-path", full);
        var rel = full[root.Length..];
        using var rh = Win.OpenForQuery(root.Length == 2 ? root + Sep : root, linkItself: false);
        if (rh.IsInvalid) throw new SafeFsException("final-path", full);
        foreach (var flags in Win.FinalPathFlags)
        {
            var file = Win.FinalPath(h, flags);
            var rootFinal = file is null ? null : Win.FinalPath(rh, flags);
            if (file is null || rootFinal is null) continue;
            if (Eq(file, rootFinal.TrimEnd(Sep) + rel)) return;
            throw new SafeFsException("final-path", full);
        }
        throw new SafeFsException("final-path", full);
    }

    /// <summary>Windows-API:er för kontrollen efter öppning. Används bara när <see cref="Unix"/> är falskt.</summary>
    private static class Win
    {
        public const uint GenericWrite = 0x40000000, Delete = 0x00010000, FileReadAttributes = 0x80;
        public const uint FileShareRead = 1, FileShareAll = 1 | 2 | 4;
        public const uint CreateAlways = 2, OpenExisting = 3;
        public const uint FileAttributeNormal = 0x80, FlagOpenReparsePoint = 0x00200000, FlagBackupSemantics = 0x02000000;
        public const int ErrorFileNotFound = 2, ErrorPathNotFound = 3;
        private const int FileDispositionInfo = 4;

        /// <summary>FILE_NAME_NORMALIZED + VOLUME_NAME_DOS, sedan VOLUME_NAME_GUID (volym utan enhetsbokstav), sedan FILE_NAME_OPENED.</summary>
        public static readonly uint[] FinalPathFlags = [0x0, 0x1, 0x8];

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, [Out] char[] buffer, uint length, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, IntPtr info, uint size);

        /// <summary>Öppnar fil eller katalog utan läs-/skrivrätt (bara attribut), delbar för alla.</summary>
        public static SafeFileHandle OpenForQuery(string path, bool linkItself) =>
            CreateFileW(path, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FlagBackupSemantics | (linkItself ? FlagOpenReparsePoint : 0), IntPtr.Zero);

        /// <summary>Handtagets slutliga sökväg (som Windows returnerar den, t.ex. <c>\\?\C:\…</c>), eller null.</summary>
        public static string? FinalPath(SafeFileHandle h, uint flags)
        {
            var buf = new char[512];
            for (int i = 0; i < 3; i++)
            {
                uint n = GetFinalPathNameByHandleW(h, buf, (uint)buf.Length, flags);
                if (n == 0) return null;
                if (n < buf.Length) return new string(buf, 0, (int)n);
                buf = new char[n + 1];
            }
            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }

        [DllImport("ntdll.dll")]
        private static extern int NtSetInformationFile(SafeFileHandle h, out IoStatusBlock iosb, IntPtr info, uint length, int infoClass);

        [DllImport("ntdll.dll")]
        private static extern int RtlNtStatusToDosError(int status);

        private const int FileRenameInformation = 10;

        /// <summary>
        /// Döper om filen bakom handtaget till <paramref name="newName"/> i SAMMA katalog: FILE_RENAME_INFORMATION utan
        /// RootDirectory och med bara ett filnamn – då slår I/O-hanteraren inte upp någon sökväg alls. (kernel32:s
        /// SetFileInformationByHandle gör om ett ensamt filnamn till en sökväg relativt processens aktuella katalog, därför
        /// NtSetInformationFile direkt.) Layout: BOOLEAN, HANDLE, ULONG, WCHAR[].
        /// </summary>
        public static void RenameByHandle(SafeFileHandle h, string newName, bool replace)
        {
            if (newName.Length == 0 || newName.IndexOfAny(['\\', '/', ':']) >= 0) throw new SafeFsException("invalid", newName);
            int rootOff = IntPtr.Size, lenOff = rootOff + IntPtr.Size, nameOff = lenOff + 4;
            int nameBytes = newName.Length * 2;
            int size = Math.Max(nameOff + nameBytes + 2, nameOff + IntPtr.Size);
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(new byte[size], 0, buf, size);
                Marshal.WriteByte(buf, 0, replace ? (byte)1 : (byte)0);
                Marshal.WriteInt32(buf, lenOff, nameBytes);
                Marshal.Copy(newName.ToCharArray(), 0, buf + nameOff, newName.Length);
                int status = NtSetInformationFile(h, out _, buf, (uint)size, FileRenameInformation);
                if (status < 0) throw ErrorFor(RtlNtStatusToDosError(status));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>Markerar filen bakom handtaget för radering (FILE_DISPOSITION_INFO); den försvinner när handtaget stängs.</summary>
        public static void DeleteByHandle(SafeFileHandle h)
        {
            var buf = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(buf, 0, 1);
                if (!SetFileInformationByHandle(h, FileDispositionInfo, buf, 1)) throw LastError();
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public static void TryDeleteByHandle(SafeFileHandle h)
        {
            try { DeleteByHandle(h); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>Senaste Win32-felet som IOException/UnauthorizedAccessException (som File.* kastar), så att IoRetry m.fl. fungerar.</summary>
        public static Exception LastError() => ErrorFor(Marshal.GetLastPInvokeError());

        public static Exception ErrorFor(int win32) =>
            Marshal.GetExceptionForHR(unchecked((int)0x80070000 | (win32 & 0xFFFF))) is { } ex and (IOException or UnauthorizedAccessException)
                ? ex : new IOException("win32 " + win32, unchecked((int)0x80070000 | (win32 & 0xFFFF)));
    }

    /// <summary>FileSystemWatcher på en tillåten katalog (t.ex. WTF\Account) för ett filnamn.</summary>
    public static FileSystemWatcher CreateWatcher(string dir, string fileName)
    {
        var p = Check(dir, Access.Probe, isDir: true);
        // Filnamnet måste vara läsbart i någon underkatalog enligt reglerna (kontrolleras när filen sedan läses).
        return new FileSystemWatcher(p, fileName);
    }

    /// <summary>Öppnar en fil i appdata (t.ex. uploads.log) med standardprogrammet.</summary>
    public static void OpenWithShell(string path)
    {
        var p = Check(path, Access.Read, isDir: false);
        if (!p.StartsWith(AppDataDir + Sep, StringComparison.OrdinalIgnoreCase)) throw new SafeFsException("not-appdata", p);
        Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ standardsökvägar och register

    /// <summary>Standardsökvägar där WoW brukar ligga (bara existens kontrolleras).</summary>
    public static List<string> StandardWowRoots()
    {
        // macOS: Battle.net installerar i /Applications/World of Warcraft (spelet i _forever_ under den), ibland i
        // ~/Applications. Andra volymer listas inte – där väljer användaren själv.
        if (Linux) return LinuxWowRoots();
        if (Unix) return ["/Applications/World of Warcraft", NormalizeUnix(Home.TrimEnd('/') + "/Applications/World of Warcraft")];
        var list = new List<string>();
        foreach (var b in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            if (!string.IsNullOrEmpty(b)) list.Add(Path.Combine(b, "World of Warcraft"));
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed) continue;
                var r = d.Name; // "C:\"
                list.Add(Path.Combine(r, "World of Warcraft"));
                list.Add(Path.Combine(r, "Games", "World of Warcraft"));
                list.Add(Path.Combine(r, "Program Files (x86)", "World of Warcraft"));
                list.Add(Path.Combine(r, "Program Files", "World of Warcraft"));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Högst så många Wine-prefix per startare (Steam, Bottles, Faugus) gås igenom.</summary>
    private const int LinuxMaxPrefixes = 200;

    /// <summary>
    /// Linux: spelet körs genom Wine, så det ligger i ett Wine-prefix (…/drive_c/Program Files (x86)/World of Warcraft).
    /// Här listas de vanliga platserna för Lutris, Steam/Proton, Bottles, Faugus Launcher och vanlig Wine. Under Steam,
    /// Bottles och Faugus har varje prefix ett eget namn: där listas BARA katalognamnen i den kända föräldern (inga
    /// filer läses), högst <see cref="LinuxMaxPrefixes"/> per förälder. Allt annat väljer användaren själv.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static List<string> LinuxWowRoots()
    {
        var home = Home.TrimEnd('/');
        var prefixes = new List<string>
        {
            home + "/.wine",
            home + "/Games/battlenet", home + "/Games/battle.net", home + "/Games/blizzard-battlenet",
            home + "/Games/world-of-warcraft", home + "/Games/wow",
        };
        void Children(string parent, string suffix)
        {
            try
            {
                foreach (var d in Directory.EnumerateDirectories(parent).Take(LinuxMaxPrefixes))
                    prefixes.Add(d.TrimEnd('/') + suffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var steam in new[] { home + "/.local/share/Steam", home + "/.steam/steam", home + "/.var/app/com.valvesoftware.Steam/data/Steam" })
            Children(steam + "/steamapps/compatdata", "/pfx");
        Children(home + "/.var/app/com.usebottles.bottles/data/bottles/bottles", "");
        Children(home + "/.local/share/bottles/bottles", "");
        Children(home + "/Faugus", "");

        var list = new List<string> { home + "/Games/World of Warcraft" };
        foreach (var p in prefixes)
        {
            list.Add(p + "/drive_c/Program Files (x86)/World of Warcraft");
            list.Add(p + "/drive_c/Program Files/World of Warcraft");
        }
        var roots = new List<string>();
        foreach (var r in list)
        {
            try { roots.Add(NormalizeUnix(r)); } catch (SafeFsException) { }
        }
        return roots.Distinct(StringComparer.Ordinal).ToList();
    }

    public const string WowRegistryKey = @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft";
    public const string WowRegistryValue = "InstallPath";

    /// <summary>Den ENDA registerläsningen: HKLM\SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft, värdet InstallPath.</summary>
    public static string? ReadWowInstallPath()
    {
        if (!OperatingSystem.IsWindows()) return null; // macOS har inget register; standardsökvägarna och valet räcker
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(WowRegistryKey, writable: false);
            return key?.GetValue(WowRegistryValue) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "ForeverScout";

    /// <summary>"Starta med Windows": läser/skriver bara värdet ForeverScout under HKCU\…\Run.</summary>
    public static bool AutostartEnabled
    {
        get
        {
            if (Linux) return FileExists(LinuxAutostartPath); // Linux: finns .desktop-filen i ~/.config/autostart?
            if (!OperatingSystem.IsWindows()) return FileExists(LaunchAgentPath); // macOS: finns LaunchAgent-filen?
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: false); return k?.GetValue(RunName) is string; }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }
    }

    public static void SetAutostart(bool on)
    {
        if (Linux) { SetLinuxAutostart(on); return; }
        if (!OperatingSystem.IsWindows()) { SetLaunchAgent(on); return; }
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunName, $"\"{OwnExe}\" --tray");
        else k.DeleteValue(RunName, throwOnMissingValue: false);
    }

    // ------------------------------------------------------------------ Linux: autostart och nyckelfilen

    /// <summary>Linux "Starta vid inloggning" = exakt den här filen (XDG Autostart).</summary>
    public static string LinuxAutostartPath => LinuxConfigHome + "/autostart/" + MacBundleId + ".desktop";

    /// <summary>
    /// Linux: filen som startar appen. I en AppImage körs binären från en tillfällig monteringspunkt
    /// (/tmp/.mount_…), som försvinner när appen avslutas – då gäller själva AppImage-filen ($APPIMAGE, satt av
    /// AppImage-startaren). Annars binären själv. Bara sökvägen läses; filen rörs inte.
    /// </summary>
    public static string? LinuxLaunchPath
    {
        get
        {
            if (OwnExeOverride is { } o) return NormalizeUnix(o);
            var image = Environment.GetEnvironmentVariable("APPIMAGE");
            if (!string.IsNullOrWhiteSpace(image) && image.StartsWith('/'))
            {
                try { return NormalizeUnix(image); } catch (SafeFsException) { }
            }
            return Environment.ProcessPath is { } exe ? NormalizeUnix(exe) : null;
        }
    }

    /// <summary>Skriver eller tar bort ~/.config/autostart/se.wow4ever.foreverscout.desktop (startar appen med --tray).</summary>
    [SupportedOSPlatform("linux")]
    private static void SetLinuxAutostart(bool on)
    {
        var file = LinuxAutostartPath;
        if (!on) { DeleteFile(file); return; }
        var exe = LinuxLaunchPath ?? throw new SafeFsException("no-exe", "");
        CreateDirectory(Path.GetDirectoryName(file)!.Replace('\\', '/'));
        WriteAllText(file, LinuxAutostartDesktop(exe));
    }

    /// <summary>.desktop-filens innehåll (ren funktion, testas). Sökvägen citeras enligt Desktop Entry-specifikationen.</summary>
    internal static string LinuxAutostartDesktop(string exe)
    {
        // Exec: argument inom citattecken, där " ` $ och \ föregås av bakstreck; i strängvärden skrivs \ dessutom som \\.
        var quoted = new System.Text.StringBuilder("\"");
        foreach (var c in exe)
        {
            if (c is '\n' or '\r') throw new SafeFsException("bad-path", exe);
            if (c == '\\') quoted.Append("\\\\\\\\");
            else if (c is '"' or '`' or '$') quoted.Append("\\\\").Append(c);
            else if (c == '%') quoted.Append("%%");
            else quoted.Append(c);
        }
        quoted.Append('"');
        return "[Desktop Entry]\nType=Application\nName=Forever Scout\nExec=" + quoted + " --tray\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";
    }

    /// <summary>
    /// Linux: nyckelfilen i appens datakatalog (katalogen 0700) får rättigheterna 0600 – bara användaren själv.
    /// Linux har ingen nyckelring som finns på alla skrivbord (Steam Deck, fönsterhanterare utan Secret Service).
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static void WritePrivateText(string path, string text)
    {
        var p = Check(path, Access.Write, isDir: false);
        if (!p.StartsWith(AppDataDir + Sep, StringComparison.Ordinal)) throw new SafeFsException("not-appdata", p);
        WriteAllText(p, text);
        File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // ------------------------------------------------------------------ macOS: inloggning, verktyg och lås

    /// <summary>
    /// macOS "Starta vid inloggning": skriver eller tar bort ~/Library/LaunchAgents/se.wow4ever.foreverscout.plist.
    /// Programmet startas via <c>/usr/bin/open -g -a &lt;paket&gt; --args --tray</c> (LaunchServices, ingen Dock-ikon,
    /// inget fönster). Utan paket (utvecklingskörning) startas binären direkt.
    /// </summary>
    private static void SetLaunchAgent(bool on)
    {
        var agent = LaunchAgentPath;
        if (!on) { DeleteFile(agent); return; }
        string[] args = OwnBundle is { } b
            ? ["/usr/bin/open", "-g", "-a", b, "--args", "--tray"]
            : [Normalize(OwnExeOverride ?? Environment.ProcessPath ?? throw new SafeFsException("no-exe", "")), "--tray"];
        CreateDirectory(Path.GetDirectoryName(agent)!);
        WriteAllText(agent, LaunchAgentPlist(args));
    }

    /// <summary>LaunchAgent-filens innehåll (ren funktion, testas).</summary>
    internal static string LaunchAgentPlist(IReadOnlyList<string> programArguments)
    {
        static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";
        var sb = new System.Text.StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n<dict>\n");
        sb.Append("  <key>Label</key>\n  <string>").Append(MacBundleId).Append("</string>\n");
        sb.Append("  <key>ProgramArguments</key>\n  <array>\n");
        foreach (var a in programArguments) sb.Append("    <string>").Append(X(a)).Append("</string>\n");
        sb.Append("  </array>\n");
        sb.Append("  <key>RunAtLoad</key>\n  <true/>\n");
        sb.Append("  <key>LimitLoadToSessionType</key>\n  <string>Aqua</string>\n");
        sb.Append("  <key>ProcessType</key>\n  <string>Interactive</string>\n");
        // Systeminställningar → Inloggningsobjekt visar då appens namn och ikon i stället för utvecklarens namn.
        sb.Append("  <key>AssociatedBundleIdentifiers</key>\n  <array>\n    <string>").Append(MacBundleId).Append("</string>\n  </array>\n");
        sb.Append("</dict>\n</plist>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Exklusivt lås på en fil i appdatakatalogen (på macOS ett flock-lås som släpps när processen dör). Null om en
    /// annan process redan håller det. Används för "en instans" på macOS.
    /// </summary>
    public static IDisposable? TryLockFile(string path)
    {
        var (p, scope) = CheckScoped(path, Access.Write, isDir: false);
        if (!p.StartsWith(AppDataDir + Sep, StringComparison.OrdinalIgnoreCase)) throw new SafeFsException("not-appdata", p);
        FileStream fs;
        try { fs = new FileStream(p, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        try { VerifyOpened(fs.SafeFileHandle, p, scope); } // L11: kontroll efter öppning
        catch { fs.Dispose(); throw; }
        return fs;
    }

    /// <summary>
    /// macOS: packar upp en (redan verifierad) zip med <c>/usr/bin/ditto -x -k</c>, som bevarar kodsignaturen,
    /// rättigheter och symlänkar i ett .app-paket. Både zip-filen och målet måste vara tillåtna (uppdateringskatalogen).
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    public static void MacExtractZip(string zip, string destDir)
    {
        var z = Check(zip, Access.Read, isDir: false);
        var d = Check(destDir, Access.Write, isDir: true);
        var (code, _, err) = RunTool("/usr/bin/ditto", ["-x", "-k", z, d], TimeSpan.FromMinutes(3));
        if (code != 0) throw new IOException("ditto " + code + ": " + Privacy.Redact(err.Trim()));
    }

    /// <summary>macOS: Team ID ur ett pakets kodsignatur (<c>codesign -dv</c>), eller null om paketet inte är signerat.</summary>
    [UnsupportedOSPlatform("windows")]
    public static string? MacCodesignTeam(string bundle)
    {
        var b = Check(bundle, Access.Read, isDir: true);
        var (code, _, err) = RunTool("/usr/bin/codesign", ["-dv", "--verbose=2", b], TimeSpan.FromMinutes(1));
        return code == 0 ? ParseTeamIdentifier(err) : null;
    }

    /// <summary>"TeamIdentifier=ABCDE12345" ur codesigns utskrift; null för "not set" eller ogiltigt.</summary>
    internal static string? ParseTeamIdentifier(string codesignOutput)
    {
        foreach (var raw in codesignOutput.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("TeamIdentifier=", StringComparison.Ordinal)) continue;
            var v = line["TeamIdentifier=".Length..].Trim();
            return v.Length is >= 1 and <= 20 && v.All(char.IsAsciiLetterOrDigit) ? v : null;
        }
        return null;
    }

    /// <summary>
    /// macOS: <c>codesign --verify --deep --strict -R=&lt;krav&gt;</c> på ett paket i uppdateringskatalogen eller det egna.
    /// True bara om signaturen är giltig och uppfyller kravet (t.ex. samma Team ID och paket-id som den körande appen).
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    public static bool MacVerifyCodeSignature(string bundle, string requirement)
    {
        var b = Check(bundle, Access.Read, isDir: true);
        var (code, _, _) = RunTool("/usr/bin/codesign", ["--verify", "--deep", "--strict", "-R=" + requirement, b], TimeSpan.FromMinutes(3));
        return code == 0;
    }

    /// <summary>Kör ett av systemets verktyg (fast sökväg, inget skal, argument som lista).</summary>
    [UnsupportedOSPlatform("windows")]
    private static (int Code, string Out, string Err) RunTool(string tool, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException(tool);
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new IOException(Path.GetFileName(tool) + " timeout");
        }
        p.WaitForExit();
        return (p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }
}
