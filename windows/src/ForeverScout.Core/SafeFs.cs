using System.Diagnostics;
using Microsoft.Win32;

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
/// </summary>
public static class SafeFs
{
    private static readonly object Lock = new();
    private static readonly HashSet<string> GameRoots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ProbeRoots = new(StringComparer.OrdinalIgnoreCase);
    private static string? _appData;

    public enum Access { Probe, Read, Write }

    // ------------------------------------------------------------------ konfiguration

    /// <summary>%APPDATA%\ForeverScout (i Debug-byggen kan FOREVERSCOUT_DATADIR peka om den för test).</summary>
    public static string AppDataDir
    {
        get
        {
            lock (Lock)
            {
                if (_appData != null) return _appData;
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ForeverScout");
#if DEBUG
                var env = Environment.GetEnvironmentVariable("FOREVERSCOUT_DATADIR");
                if (!string.IsNullOrWhiteSpace(env)) dir = env;
#endif
                var n = Normalize(dir);
                Directory.CreateDirectory(n);
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
            Directory.CreateDirectory(n);
            _appData = RealPath(n);
        }
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

    // ------------------------------------------------------------------ policy

    /// <summary>Normaliserar och nekar trasiga/farliga former. Kastar <see cref="SafeFsException"/>.</summary>
    public static string Normalize(string path)
    {
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
        if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return null!;
        return full[(root.Length + 1)..].Split('\\');
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

    /// <summary>Filerna självuppdateringen får röra: exe, .new.exe (+ dess .tmp under skrivning) och .old.exe.</summary>
    private static bool IsOwnExeFile(string full)
    {
        var exe = OwnExe;
        var stem = exe[..^4];
        return Eq(full, exe) || Eq(full, stem + ".new.exe") || Eq(full, stem + ".new.exe.tmp") || Eq(full, stem + ".old.exe");
    }

    /// <summary>Kontrollerar en sökväg mot reglerna och returnerar den normaliserade sökvägen.</summary>
    public static string Check(string path, Access want, bool isDir)
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
        if (allowed is null && !isDir && IsOwnExeFile(full)) { allowed = Access.Write; scopeRoot = full; }
        if (allowed is null || allowed < want) throw new SafeFsException(allowed is null ? "outside" : "access", full);
        RefuseReparsePoints(full, scopeRoot!);
        return full;
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

    /// <summary>Läser hela filen med FileShare.ReadWrite (spelet kan ha den öppen).</summary>
    public static byte[] ReadAllBytes(string path, long maxBytes = 64L * 1024 * 1024)
    {
        var p = Check(path, Access.Read, isDir: false);
        using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > maxBytes) throw new ScoutException(ErrorCodes.FileTooLarge);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Läser text som UTF-8; en ev. BOM tas bort (t.ex. om settings.json sparats av ett annat program).</summary>
    public static string ReadAllText(string path) => System.Text.Encoding.UTF8.GetString(ReadAllBytes(path)).TrimStart((char)0xFEFF);

    /// <summary>Skriver atomärt (temporärfil + namnbyte).</summary>
    public static void WriteAllBytes(string path, byte[] data)
    {
        var p = Check(path, Access.Write, isDir: false);
        var tmp = p + ".tmp";
        Check(tmp, Access.Write, isDir: false);
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, p, overwrite: true);
    }

    public static void WriteAllText(string path, string text) => WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(text));

    public static void AppendText(string path, string text) =>
        File.AppendAllText(Check(path, Access.Write, isDir: false), text, System.Text.Encoding.UTF8);

    public static void DeleteFile(string path)
    {
        var p = Check(path, Access.Write, isDir: false);
        if (File.Exists(p)) File.Delete(p);
    }

    public static void MoveFile(string from, string to) =>
        File.Move(Check(from, Access.Write, isDir: false), Check(to, Access.Write, isDir: false));

    public static void CreateDirectory(string path) => Directory.CreateDirectory(Check(path, Access.Write, isDir: true));

    public static void DeleteDirectory(string path)
    {
        var p = Check(path, Access.Write, isDir: true);
        if (Directory.Exists(p)) Directory.Delete(p, recursive: true);
    }

    public static void MoveDirectory(string from, string to) =>
        Directory.Move(Check(from, Access.Write, isDir: true), Check(to, Access.Write, isDir: true));

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
        if (!p.StartsWith(AppDataDir + "\\", StringComparison.OrdinalIgnoreCase)) throw new SafeFsException("not-appdata", p);
        Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ standardsökvägar och register

    /// <summary>Standardsökvägar där WoW brukar ligga (bara existens kontrolleras).</summary>
    public static List<string> StandardWowRoots()
    {
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

    public const string WowRegistryKey = @"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft";
    public const string WowRegistryValue = "InstallPath";

    /// <summary>Den ENDA registerläsningen: HKLM\SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft, värdet InstallPath.</summary>
    public static string? ReadWowInstallPath()
    {
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
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: false); return k?.GetValue(RunName) is string; }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
        }
    }

    public static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(RunName, $"\"{OwnExe}\" --tray");
        else k.DeleteValue(RunName, throwOnMissingValue: false);
    }
}
