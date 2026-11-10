using System.Text.RegularExpressions;
using ForeverScout.Core;

namespace ForeverScout.Tests;

public sealed class SafeFsTests : IDisposable
{
    private readonly DirectoryInfo _tmp = TestEnv.Temp("fs-safe-");
    private readonly string _game;

    public SafeFsTests()
    {
        _game = Path.Combine(_tmp.FullName, "World of Warcraft", "_forever_");
        Directory.CreateDirectory(Path.Combine(_game, "Interface", "AddOns", "ForeverScout"));
        Directory.CreateDirectory(Path.Combine(_game, "WTF", "Account", "ACC", "SavedVariables"));
        Directory.CreateDirectory(Path.Combine(_game, "Cache", "ADB", "enUS"));
        File.WriteAllText(Path.Combine(_game, "WTF", "Account", "ACC", "SavedVariables", "ForeverScout.lua"), "ForeverScoutDB = {}");
        File.WriteAllText(Path.Combine(_game, "WTF", "Account", "ACC", "SavedVariables", "Other.lua"), "Secret = 1");
        File.WriteAllText(Path.Combine(_game, "WTF", "Account", "ACC", "config-cache.wtf"), "SET accountName x");
        File.WriteAllText(Path.Combine(_game, "WTF", "Config.wtf"), "SET x");
        File.WriteAllBytes(Path.Combine(_game, "Cache", "ADB", "enUS", "DBCache.bin"), [1]);
        File.WriteAllText(Path.Combine(_tmp.FullName, "outside.txt"), "nope");
        _game = SafeFs.SetGameDir(_game)!;
    }

    public void Dispose()
    {
        SafeFs.SetGameDir(null);
        try { _tmp.Delete(true); } catch (IOException) { }
    }

    private string G(params string[] p) => Path.Combine([_game, .. p]);

    [Fact]
    public void Sha256HexStreamsAndMatchesReadAllBytes()
    {
        // 1.0.11: uppladdaren känner igen oförändrade filer med en strömmad hash – den måste vara identisk med hashen
        // av hela innehållet, och ReadAllBytes (exakt storlek, ingen MemoryStream) måste ge filen byte för byte.
        var path = G("WTF", "Account", "ACC", "SavedVariables", "ForeverScout.lua");
        var data = new byte[300_000]; // flera 64 KB-block + en rest
        new Random(42).NextBytes(data);
        File.WriteAllBytes(path, data);
        Assert.Equal(data, SafeFs.ReadAllBytes(path));
        Assert.Equal(ApiClient.Sha256Hex(data), SafeFs.Sha256Hex(path));
        File.WriteAllBytes(path, []);
        Assert.Empty(SafeFs.ReadAllBytes(path));
        Assert.Equal(ApiClient.Sha256Hex(Array.Empty<byte>()), SafeFs.Sha256Hex(path));
        Assert.Throws<ScoutException>(() => SafeFs.Sha256Hex(path, maxBytes: -1));
        Assert.Throws<SafeFsException>(() => SafeFs.Sha256Hex(G("WTF", "Account", "ACC", "SavedVariables", "Other.lua")));
    }

    [Fact]
    public void AllowedReadsWork()
    {
        Assert.Equal("ForeverScoutDB = {}", SafeFs.ReadAllText(G("WTF", "Account", "ACC", "SavedVariables", "ForeverScout.lua")));
        Assert.Single(SafeFs.ReadAllBytes(G("Cache", "ADB", "enUS", "DBCache.bin")));
        SafeFs.WriteAllText(G("Interface", "AddOns", "ForeverScout", "ForeverScout.toc"), "## Version: 1");
        SafeFs.WriteAllText(Path.Combine(SafeFs.AppDataDir, "x.json"), "{}");
    }

    [Theory]
    [InlineData("WTF", "Account", "ACC", "SavedVariables", "Other.lua")]
    [InlineData("WTF", "Account", "ACC", "config-cache.wtf")]
    [InlineData("WTF", "Config.wtf")]
    [InlineData("Interface", "AddOns", "OtherAddon", "x.lua")]
    [InlineData("Wow.exe")]
    public void OtherFilesInTheWowFolderAreRefused(params string[] rel)
    {
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(G(rel)));
        Assert.False(SafeFs.FileExists(G(rel)));
    }

    [Fact]
    public void WritingOutsideOurAddonIsRefused()
    {
        Assert.Throws<SafeFsException>(() => SafeFs.WriteAllText(G("WTF", "Account", "ACC", "SavedVariables", "ForeverScout.lua"), "x"));
        Assert.Throws<SafeFsException>(() => SafeFs.WriteAllText(G("Interface", "AddOns", "Evil", "x.lua"), "x"));
        Assert.Throws<SafeFsException>(() => SafeFs.CreateDirectory(G("Fonts")));
    }

    [Fact]
    public void PathTraversalIsRefused()
    {
        var sneaky = G("WTF", "Account", "ACC", "SavedVariables", "..", "..", "..", "Config.wtf");
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(sneaky));
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(G("Interface", "AddOns", "ForeverScout", "..", "..", "..", "WTF", "Config.wtf")));
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(Path.Combine(SafeFs.AppDataDir, "..", "Other", "x")));
    }

    [Theory]
    [InlineData(@"\\server\share\WTF\Account\A\SavedVariables\ForeverScout.lua")]
    [InlineData(@"\\?\C:\Windows\win.ini")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"Z:\World of Warcraft\_forever_\WTF\Account\A\SavedVariables\ForeverScout.lua")]
    [InlineData(@"relative\ForeverScout.lua")]
    [InlineData(@"C:\x\ForeverScout.lua:secret")]
    [InlineData("")]
    public void OtherDrivesUncDevicesAndOddPathsAreRefused(string path)
    {
        Assert.ThrowsAny<SafeFsException>(() => SafeFs.ReadAllBytes(path));
        Assert.False(SafeFs.FileExists(path));
    }

    [Fact]
    public void OutsideFileNextToWowFolderIsRefused() =>
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(Path.Combine(_tmp.FullName, "outside.txt")));

    [Fact]
    public void CaseInsensitiveRulesStillMatch() =>
        Assert.Equal("ForeverScoutDB = {}", SafeFs.ReadAllText(G("wtf", "ACCOUNT", "ACC", "savedvariables", "FOREVERSCOUT.LUA")));

    [Fact]
    public void SymlinkInsideTheWowFolderIsRefused()
    {
        // Junction (kräver inte admin) från ett tillåtet ställe till en katalog utanför.
        var outside = Path.Combine(_tmp.FullName, "secret-dir");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "ForeverScout.lua"), "Secret = 1");
        var link = G("WTF", "Account", "LINKED");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false };
        using (var p = System.Diagnostics.Process.Start(psi)!) p.WaitForExit();
        Assert.True(Directory.Exists(link), "kunde inte skapa junction");
        var viaLink = Path.Combine(link, "SavedVariables", "ForeverScout.lua");
        Directory.CreateDirectory(Path.Combine(outside, "SavedVariables"));
        File.WriteAllText(Path.Combine(outside, "SavedVariables", "ForeverScout.lua"), "ForeverScoutDB = {}");
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(viaLink));
        Directory.Delete(link); // tar bara bort länken
    }

    // ---------------------------------------------------------------- L11: kontroll efter öppning (GetFinalPathNameByHandle)

    private static void Junction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using (var p = System.Diagnostics.Process.Start(psi)!) { p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
        Assert.True(Directory.Exists(link), "kunde inte skapa junction");
    }

    /// <summary>
    /// Byter <paramref name="dir"/> mot en junction till <paramref name="target"/> EFTER regelkontrollen men före
    /// öppningen (det fönster som en annan lokal användare med skrivrätt i spelkatalogen skulle utnyttja).
    /// </summary>
    private static IDisposable SwapAfterCheck(string dir, string target)
    {
        var backup = dir + ".bak";
        bool swapped = false;
        SafeFs.BeforeOpen = _ =>
        {
            if (swapped) return;
            swapped = true;
            Directory.Move(dir, backup);
            Junction(dir, target);
        };
        return new Restore(() =>
        {
            SafeFs.BeforeOpen = null;
            if (!swapped) return;
            Directory.Delete(dir); // bara länken
            Directory.Move(backup, dir);
        });
    }

    private sealed class Restore(Action a) : IDisposable { public void Dispose() => a(); }

    [Fact]
    public void L11_JunctionSwappedInAfterTheCheckIsNotRead()
    {
        var outside = Path.Combine(_tmp.FullName, "evil-acc");
        Directory.CreateDirectory(Path.Combine(outside, "SavedVariables"));
        File.WriteAllText(Path.Combine(outside, "SavedVariables", "ForeverScout.lua"), "ForeverScoutDB = { evil = 1 }");
        var file = G("WTF", "Account", "ACC", "SavedVariables", "ForeverScout.lua");
        using (SwapAfterCheck(G("WTF", "Account", "ACC"), outside))
        {
            var ex = Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(file));
            Assert.Equal("final-path", ex.Reason);
        }
        Assert.Equal("ForeverScoutDB = {}", SafeFs.ReadAllText(file)); // utan bytet fungerar läsningen
    }

    [Fact]
    public void L11_JunctionSwappedInAfterTheCheckIsNotWrittenTo()
    {
        var outside = Path.Combine(_tmp.FullName, "evil-addon");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "ForeverScout.toc"), "victim");
        var toc = G("Interface", "AddOns", "ForeverScout", "ForeverScout.toc");
        using (SwapAfterCheck(G("Interface", "AddOns", "ForeverScout"), outside))
            Assert.Equal("final-path", Assert.Throws<SafeFsException>(() => SafeFs.WriteAllText(toc, "## Version: 9")).Reason);
        // Målet utanför är orört, och temporärfilen som hann skapas där är borttagen via handtaget.
        Assert.Equal("victim", File.ReadAllText(Path.Combine(outside, "ForeverScout.toc")));
        Assert.False(File.Exists(Path.Combine(outside, "ForeverScout.toc.tmp")));
    }

    [Fact]
    public void L11_AppendAndDeleteThroughSwappedJunctionAreRefused()
    {
        var outside = Path.Combine(_tmp.FullName, "evil-dir");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "ForeverScout.lua"), "keep");
        var target = G("Interface", "AddOns", "ForeverScout", "ForeverScout.lua");
        File.WriteAllText(target, "ours");
        using (SwapAfterCheck(G("Interface", "AddOns", "ForeverScout"), outside))
            Assert.Equal("final-path", Assert.Throws<SafeFsException>(() => SafeFs.DeleteFile(target)).Reason);
        using (SwapAfterCheck(G("Interface", "AddOns", "ForeverScout"), outside))
            Assert.Equal("final-path", Assert.Throws<SafeFsException>(() => SafeFs.AppendText(target, "more")).Reason);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "ForeverScout.lua")));
        Assert.Equal("ours", File.ReadAllText(target));
        SafeFs.DeleteFile(target); // utan bytet raderas vår egen fil
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void L11_HandleBasedWriteReplacesInPlaceAndLeavesNoTmp()
    {
        var toc = G("Interface", "AddOns", "ForeverScout", "ForeverScout.toc");
        SafeFs.WriteAllText(toc, "## Version: 1");
        SafeFs.WriteAllText(toc, "## Version: 2"); // ersätter befintlig fil (namnbyte på handtaget)
        Assert.Equal("## Version: 2", File.ReadAllText(toc));
        Assert.False(File.Exists(toc + ".tmp"));
        Assert.False(File.Exists(Path.Combine(Environment.CurrentDirectory, "ForeverScout.toc"))); // namnbytet hamnar i samma katalog
        SafeFs.WriteAllBytes(toc, []);
        Assert.Empty(File.ReadAllBytes(toc));
        // Appdata: skriv, lägg till och läs tillbaka.
        var log = Path.Combine(SafeFs.AppDataDir, "l11-append.log");
        SafeFs.DeleteFile(log);
        SafeFs.AppendText(log, "a\n");
        SafeFs.AppendText(log, "b\n");
        Assert.Equal("a\nb\n", SafeFs.ReadAllText(log));
        SafeFs.DeleteFile(log);
    }

    [Fact]
    public void L11_ShortNamesInTheChosenFolderStillWork()
    {
        // Datorer med 8.3-namn (t.ex. C:\Users\ADMINI~1\…): handtagets sökväg har långa namn, området jämförs i samma form.
        var buf = new System.Text.StringBuilder(1024);
        var shortGame = GetShortPathNameW(_game, buf, buf.Capacity) > 0 ? buf.ToString() : _game;
        var root = SafeFs.SetGameDir(shortGame)!;
        Assert.Equal("ForeverScoutDB = {}", SafeFs.ReadAllText(Path.Combine(root, "WTF", "Account", "ACC", "SavedVariables", "ForeverScout.lua")));
        SafeFs.WriteAllText(Path.Combine(root, "Interface", "AddOns", "ForeverScout", "x.lua"), "x");
        Assert.Equal("x", File.ReadAllText(G("Interface", "AddOns", "ForeverScout", "x.lua")));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetShortPathNameW(string longPath, System.Text.StringBuilder shortPath, int size);

    [Fact]
    public void L11_DirectoryOperationsThroughSwappedJunctionAreRefused()
    {
        var outside = Path.Combine(_tmp.FullName, "evil-addons");
        Directory.CreateDirectory(Path.Combine(outside, "ForeverScout.old"));
        File.WriteAllText(Path.Combine(outside, "ForeverScout.old", "precious.txt"), "keep");
        Directory.CreateDirectory(G("Interface", "AddOns", "ForeverScout.old"));
        using (SwapAfterCheck(G("Interface", "AddOns"), outside))
            Assert.Equal("final-path", Assert.Throws<SafeFsException>(() => SafeFs.DeleteDirectory(G("Interface", "AddOns", "ForeverScout.old"))).Reason);
        Assert.True(File.Exists(Path.Combine(outside, "ForeverScout.old", "precious.txt")));
        SafeFs.DeleteDirectory(G("Interface", "AddOns", "ForeverScout.old"));
        Assert.False(Directory.Exists(G("Interface", "AddOns", "ForeverScout.old")));
    }

    [Fact]
    public void ProbeRootsAllowOnlyExistenceNotContent()
    {
        var root = Path.GetDirectoryName(_game)!;
        SafeFs.AllowProbeRoot(root);
        File.WriteAllText(Path.Combine(root, ".build.info"), "Product|wowf");
        Assert.True(SafeFs.FileExists(Path.Combine(root, ".build.info")));
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(Path.Combine(root, ".build.info")));
    }

    [Fact]
    public void ErrorsNeverShowThePath()
    {
        var ex = Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(G("WTF", "Account", "ACC", "config-cache.wtf")));
        Strings.Apply("en");
        try { Assert.DoesNotContain("ACC", Strings.Error(ex)); }
        finally { Strings.Apply("sv"); }
    }
}

/// <summary>
/// Statisk vakt: ingen källfil utom SafeFs.cs får röra filsystem, register eller processlistor direkt,
/// och bara ApiClient.cs får använda HttpClient. Gäller Windows-klienten (src/) OCH macOS-appen
/// (client/mac/ForeverScout.Mac), som bygger på samma Core. Enda undantaget för Socket är UnixInstance.cs
/// (macOS: en instans), och där bara med AddressFamily.Unix.
/// </summary>
public class StaticGuardTests
{
    private static string SrcDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "src", "ForeverScout.Core"))) return Path.Combine(d.FullName, "src");
        throw new DirectoryNotFoundException("src");
    }

    /// <summary>client/mac/ForeverScout.Mac (macOS-appen) – finns alltid i repot.</summary>
    private static string MacDir()
    {
        var mac = Path.GetFullPath(Path.Combine(SrcDir(), "..", "..", "mac", "ForeverScout.Mac"));
        Assert.True(Directory.Exists(mac), "hittar inte " + mac);
        return mac;
    }

    /// <summary>client/linux/ForeverScout.Linux (Linux-appens egna filer; resten är macOS-appens källfiler).</summary>
    private static string LinuxDir()
    {
        var linux = Path.GetFullPath(Path.Combine(SrcDir(), "..", "..", "linux", "ForeverScout.Linux"));
        Assert.True(Directory.Exists(linux), "hittar inte " + linux);
        return linux;
    }

    private static IEnumerable<(string File, int Line, string Text)> Sources()
    {
        foreach (var f in Directory.GetFiles(SrcDir(), "*.cs", SearchOption.AllDirectories).Concat(Directory.GetFiles(MacDir(), "*.cs", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(LinuxDir(), "*.cs", SearchOption.AllDirectories)))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith("//") || t.StartsWith("///") || t.StartsWith("*")) continue;
                yield return (f, i + 1, lines[i]);
            }
        }
    }

    private static readonly (string Name, Regex Rx)[] Banned =
    [
        ("File.", new Regex(@"(?<![\w.])File\.(?!Exists\b)", RegexOptions.Compiled)),
        ("File.Exists", new Regex(@"(?<![\w.])File\.Exists\b", RegexOptions.Compiled)),
        ("Directory.", new Regex(@"(?<![\w.])Directory\.", RegexOptions.Compiled)),
        ("FileStream", new Regex(@"\bFileStream\b", RegexOptions.Compiled)),
        ("StreamReader(path)", new Regex(@"new\s+StreamReader\s*\(\s*(?!\w*[Ss]tream\b)", RegexOptions.Compiled)),
        ("StreamWriter(path)", new Regex(@"new\s+StreamWriter\s*\(\s*(?!\w*[Ss]tream\b)", RegexOptions.Compiled)),
        ("FileInfo", new Regex(@"\bnew\s+FileInfo\b", RegexOptions.Compiled)),
        ("DirectoryInfo", new Regex(@"\bnew\s+DirectoryInfo\b", RegexOptions.Compiled)),
        ("DriveInfo", new Regex(@"\bDriveInfo\b", RegexOptions.Compiled)),
        ("FileSystemWatcher ctor", new Regex(@"new\s+FileSystemWatcher\b", RegexOptions.Compiled)),
        ("Process.GetProcess", new Regex(@"Process\.GetProcess", RegexOptions.Compiled)),
        ("ManagementObject", new Regex(@"ManagementObject|System\.Management", RegexOptions.Compiled)),
        ("Registry.", new Regex(@"(?<![\w.])Registry\.", RegexOptions.Compiled)),
        ("Environment.GetFolderPath", new Regex(@"Environment\.GetFolderPath", RegexOptions.Compiled)),
        ("Icon.ExtractAssociatedIcon", new Regex(@"ExtractAssociatedIcon", RegexOptions.Compiled)),
        ("ReadProcessMemory/OpenProcess", new Regex(@"ReadProcessMemory|OpenProcess|CreateToolhelp32Snapshot|EnumProcesses", RegexOptions.Compiled)),
    ];

    [Fact]
    public void OnlySafeFsTouchesFilesRegistryAndProcesses()
    {
        var hits = new List<string>();
        foreach (var (file, line, text) in Sources())
        {
            if (Path.GetFileName(file) == "SafeFs.cs") continue;
            foreach (var (name, rx) in Banned)
                if (rx.IsMatch(text)) hits.Add($"{Path.GetFileName(file)}:{line}: {name}: {text.Trim()}");
        }
        Assert.True(hits.Count == 0, "Direkt fil-/register-/processåtkomst utanför SafeFs.cs:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void OnlyApiClientUsesHttpClient()
    {
        var hits = Sources().Where(s => Path.GetFileName(s.File) != "ApiClient.cs" && Regex.IsMatch(s.Text, @"\bHttpClient\b|\bWebClient\b|\bHttpWebRequest\b|\bSocket\b|\bTcpClient\b"))
            .Where(s => !(Path.GetFileName(s.File) == "UnixInstance.cs" && Regex.IsMatch(s.Text, @"\bSocket\b") && !Regex.IsMatch(s.Text, @"\bHttpClient\b|\bWebClient\b|\bHttpWebRequest\b|\bTcpClient\b")))
            .Select(s => $"{Path.GetFileName(s.File)}:{s.Line}: {s.Text.Trim()}").ToList();
        Assert.True(hits.Count == 0, string.Join("\n", hits));
    }

    [Fact]
    public void UnixInstanceUsesOnlyUnixDomainSockets()
    {
        var src = File.ReadAllText(Path.Combine(SrcDir(), "ForeverScout.Core", "UnixInstance.cs"));
        var families = Regex.Matches(src, @"AddressFamily\.(\w+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Equal(["Unix"], families);
        Assert.DoesNotContain("IPEndPoint", src);
        Assert.DoesNotContain("DnsEndPoint", src);
        Assert.DoesNotContain("InterNetwork", src);
    }

    [Fact]
    public void MacToolsAreFixedSystemBinaries()
    {
        // SafeFs kör bara /usr/bin/ditto och /usr/bin/codesign (fasta sökvägar, inget skal).
        var safe = File.ReadAllText(Path.Combine(SrcDir(), "ForeverScout.Core", "SafeFs.cs"));
        var tools = Regex.Matches(safe, @"RunTool\(""([^""]+)""").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(["/usr/bin/codesign", "/usr/bin/ditto"], tools);
        Assert.DoesNotContain("/bin/sh", safe);
        Assert.DoesNotContain("/bin/bash", safe);
    }

    [Fact]
    public void RegistryAccessIsTheTwoDocumentedValuesOnly()
    {
        var safe = File.ReadAllText(Path.Combine(SrcDir(), "ForeverScout.Core", "SafeFs.cs"));
        var uses = Regex.Matches(safe, @"Registry\.(\w+)").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["LocalMachine", "CurrentUser", "CurrentUser"], uses);
        Assert.Contains(@"SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft", safe);
        Assert.Contains("\"InstallPath\"", safe);
        Assert.DoesNotContain("GetValueNames", safe);
        Assert.DoesNotContain("GetSubKeyNames", safe);
    }

    [Fact]
    public void ApiClientHostIsFixedAndOverrideIsDebugOnly()
    {
        var api = File.ReadAllText(Path.Combine(SrcDir(), "ForeverScout.Core", "ApiClient.cs"));
        Assert.Contains("DefaultBaseUrl = \"https://guide.wow4ever.se\"", api);
        // FOREVERSCOUT_BASEURL får bara förekomma inom #if DEBUG … #else/#endif.
        foreach (Match m in Regex.Matches(api, "FOREVERSCOUT_BASEURL"))
        {
            var before = api[..m.Index];
            int lastIf = before.LastIndexOf("#if DEBUG", StringComparison.Ordinal);
            int lastEnd = Math.Max(before.LastIndexOf("#else", StringComparison.Ordinal), before.LastIndexOf("#endif", StringComparison.Ordinal));
            var lineStart = before.LastIndexOf('\n') + 1;
            if (api[lineStart..m.Index].Contains("//")) continue; // kommentar
            Assert.True(lastIf > lastEnd, "FOREVERSCOUT_BASEURL används utanför #if DEBUG");
        }
        Assert.Matches(@"#if !DEBUG\s+return new Uri\(DefaultBaseUrl\);", api);
        Assert.Matches(@"#else\s+BaseUri = ResolveBaseUri\(null\);", api);
        Assert.Equal("guide.wow4ever.se", ApiClient.Host);
    }

    [Fact]
    public void UserAgentIsOnlyProductAndVersion() =>
        Assert.Matches(@"^ForeverScout/\d+\.\d+\.\d+$", ApiClient.UserAgent);
}
