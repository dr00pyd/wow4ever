using System.IO.Compression;
using System.Xml.Linq;
using ForeverScout.Core;
using ForeverScout.Tests;

namespace ForeverScout.Mac.Tests;

/// <summary>SafeFs-reglerna för macOS som rena funktioner (körs på alla plattformar).</summary>
public class MacPathRuleTests
{
    [Theory]
    [InlineData("/Applications/World of Warcraft/_forever_", "/Applications/World of Warcraft/_forever_")]
    [InlineData("/Applications/World of Warcraft/_forever_/", "/Applications/World of Warcraft/_forever_")]
    [InlineData("/Applications//World of Warcraft/./_forever_", "/Applications/World of Warcraft/_forever_")]
    [InlineData("  /Users/a/Library/Application Support/ForeverScout  ", "/Users/a/Library/Application Support/ForeverScout")]
    [InlineData("/", "/")]
    public void NormalizeUnixAcceptsAbsolutePaths(string raw, string expected) => Assert.Equal(expected, SafeFs.NormalizeUnix(raw));

    [Theory]
    [InlineData("", "empty")]
    [InlineData("Applications/World of Warcraft", "not-absolute")]
    [InlineData("~/Library", "not-absolute")]
    [InlineData("//server/share/WTF", "unc-or-device")]
    [InlineData("/Applications/World of Warcraft/_forever_/WTF/../Config.wtf", "dotdot")]
    [InlineData("/a/ForeverScout.lua/..namedfork/rsrc", "stream")]
    [InlineData("/a/b\\c", "chars")]
    [InlineData("/a/*.lua", "chars")]
    [InlineData("/a/\0b", "chars")]
    [InlineData(@"C:\World of Warcraft\_forever_", "not-absolute")]
    public void NormalizeUnixRefusesOddPaths(string raw, string reason) =>
        Assert.Equal(reason, Assert.Throws<SafeFsException>(() => SafeFs.NormalizeUnix(raw)).Reason);

    [Theory]
    [InlineData("/Applications/Forever Scout.app/Contents/MacOS/ForeverScout", "/Applications/Forever Scout.app")]
    [InlineData("/Applications/Forever Scout.app/Contents/MacOS/arm64/ForeverScout", "/Applications/Forever Scout.app")]
    [InlineData("/Users/a/Applications/Forever Scout.app/Contents/MacOS/x64/ForeverScout", "/Users/a/Applications/Forever Scout.app")]
    [InlineData("/private/var/folders/x/AppTranslocation/ABC/d/Forever Scout.app/Contents/MacOS/ForeverScout", "/private/var/folders/x/AppTranslocation/ABC/d/Forever Scout.app")]
    [InlineData("/Users/a/src/bin/Debug/net10.0/ForeverScout", null)]
    [InlineData("/x/.app/Contents/MacOS/ForeverScout", null)]
    [InlineData("/x/Scout.app/Resources/ForeverScout", null)]
    public void BundleIsFoundFromTheExecutable(string exe, string? bundle) => Assert.Equal(bundle, SafeFs.BundleOf(exe));

    [Fact]
    public void UpdateDirIsAHiddenSiblingOfTheBundle() =>
        Assert.Equal("/Applications/.ForeverScout-update", SafeFs.UpdateDirFor("/Applications/Forever Scout.app"));

    [Fact]
    public void LaunchAgentPlistIsValidAndEscaped()
    {
        var xml = SafeFs.LaunchAgentPlist(["/usr/bin/open", "-g", "-a", "/Applications/Tom & Jerry <Scout>.app", "--args", "--tray"]);
        var doc = XDocument.Parse(xml);
        var dict = doc.Root!.Element("dict")!;
        var keys = dict.Elements("key").Select(k => k.Value).ToList();
        Assert.Equal(["Label", "ProgramArguments", "RunAtLoad", "LimitLoadToSessionType", "ProcessType", "AssociatedBundleIdentifiers"], keys);
        Assert.Equal("se.wow4ever.foreverscout", dict.Elements("string").First().Value);
        var args = dict.Element("array")!.Elements("string").Select(s => s.Value).ToList();
        Assert.Equal(["/usr/bin/open", "-g", "-a", "/Applications/Tom & Jerry <Scout>.app", "--args", "--tray"], args);
        Assert.Contains("<true/>", xml);
        Assert.DoesNotContain("KeepAlive", xml); // startas en gång vid inloggning, startas inte om om användaren avslutar
    }

    [Theory]
    [InlineData("Executable=/Applications/Forever Scout.app/Contents/MacOS/ForeverScout\nIdentifier=se.wow4ever.foreverscout\nTeamIdentifier=AB12CD34EF\n", "AB12CD34EF")]
    [InlineData("Identifier=x\nTeamIdentifier=not set\n", null)]
    [InlineData("Identifier=x\n", null)]
    [InlineData("TeamIdentifier=AB12\" or 1\n", null)]
    public void TeamIdentifierIsParsedStrictly(string output, string? team) => Assert.Equal(team, SafeFs.ParseTeamIdentifier(output));

    [Fact]
    public void BundleUpdaterHelpers()
    {
        Assert.True(MacBundleUpdater.IsTranslocated("/private/var/folders/x/T/AppTranslocation/1234/d/Forever Scout.app"));
        Assert.False(MacBundleUpdater.IsTranslocated("/Applications/Forever Scout.app"));
        Assert.Equal("/Applications/Forever Scout.app/Contents/MacOS/ForeverScout", MacBundleUpdater.ExecutableIn("/Applications/Forever Scout.app"));
        Assert.Equal("identifier \"se.wow4ever.foreverscout\" and anchor apple generic and certificate leaf[subject.OU] = \"AB12CD34EF\"",
            MacBundleUpdater.Requirement("AB12CD34EF"));
    }

    [Fact]
    public void MacTextsFallBackToTheSharedStrings()
    {
        Strings.Apply("sv");
        Assert.Equal("Starta vid inloggning (i menyraden)", Texts.T("set.autostart"));
        Assert.Equal(Strings.S("menu.upload"), Texts.T("menu.upload"));
        Strings.Apply("en");
        try { Assert.Equal("Hide in the menu bar", Texts.T("close.tray")); }
        finally { Strings.Apply("sv"); }
    }

    [Fact]
    public void EveryMacOverrideHasTheSamePlaceholdersAsItsBase()
    {
        foreach (var (key, v) in Strings.All.Where(kv => kv.Key.StartsWith("mac.", StringComparison.Ordinal)))
        {
            Assert.False(string.IsNullOrWhiteSpace(v.Sv), key);
            Assert.False(string.IsNullOrWhiteSpace(v.En), key);
            var baseKey = key["mac.".Length..];
            if (!Strings.All.TryGetValue(baseKey, out var b)) continue; // ny text som bara finns på macOS
            Assert.Equal(System.Text.RegularExpressions.Regex.Matches(b.Sv, @"\{\d\}").Count, System.Text.RegularExpressions.Regex.Matches(v.Sv, @"\{\d\}").Count);
        }
    }
}

/// <summary>En instans på macOS: flock-låset och Unix-socketen (fungerar även på Windows 10+, som har AF_UNIX).</summary>
public sealed class UnixInstanceTests : IDisposable
{
    public UnixInstanceTests()
    {
        var dir = Path.Combine(SafeFs.AppDataDir, "inst-" + Guid.NewGuid().ToString("N")[..8]);
        SafeFs.CreateDirectory(dir);
        UnixInstance.DirOverride = dir;
    }

    public void Dispose() => UnixInstance.DirOverride = null;

    [Fact]
    public void SecondInstanceIsRefusedAndAskedViaTheSocket()
    {
        using var first = UnixInstance.TryAcquire();
        Assert.NotNull(first);
        Assert.Null(UnixInstance.TryAcquire());

        TakeoverRequest? seen = null;
        var acted = new ManualResetEventSlim();
        first!.Listen(req => { seen = req; return Takeover.Decide(new Version(1, 0, 6), "/Applications/Forever Scout.app/Contents/MacOS/ForeverScout", req); },
            _ => acted.Set());

        // Samma version och sökväg: visa fönstret.
        Assert.Equal(TakeoverOutcome.Shown, UnixInstance.Request(new TakeoverRequest("1.0.6", "/Applications/Forever Scout.app/Contents/MacOS/ForeverScout"), TimeSpan.FromSeconds(5)));
        Assert.True(acted.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal("1.0.6", seen!.Version);

        // Nyare version: den körande avslutar sig.
        acted.Reset();
        Assert.Equal(TakeoverOutcome.TakeOver, UnixInstance.Request(new TakeoverRequest("1.0.7", "/Applications/Forever Scout.app/Contents/MacOS/ForeverScout"), TimeSpan.FromSeconds(5)));
        Assert.True(acted.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void LockIsReleasedOnDisposeAndWaitForPreviousWorks()
    {
        var first = UnixInstance.TryAcquire();
        Assert.NotNull(first);
        var releaser = Task.Run(async () => { await Task.Delay(500); first!.Dispose(); });
        using var next = UnixInstance.TryAcquire(TimeSpan.FromSeconds(10));
        Assert.NotNull(next);
        releaser.Wait();
    }

    [Fact]
    public void NobodyListeningGivesNoAnswer() =>
        Assert.Equal(TakeoverOutcome.NoAnswer, UnixInstance.Request(new TakeoverRequest("1.0.6", "/x"), TimeSpan.FromSeconds(1)));

    [Fact]
    public void GarbageOnTheSocketIsIgnored()
    {
        using var first = UnixInstance.TryAcquire();
        int acts = 0;
        first!.Listen(_ => TakeoverDecision.Exit, _ => Interlocked.Increment(ref acts));
        var sock = Path.Combine(UnixInstance.DirOverride!, "instance.sock");
        using (var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified))
        {
            s.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(sock));
            s.Send("not json\n"u8.ToArray());
        }
        Thread.Sleep(500);
        Assert.Equal(0, acts);
        // Lyssnaren lever fortfarande.
        Assert.Equal(TakeoverOutcome.TakeOver, UnixInstance.Request(new TakeoverRequest("9.0.0", "/y"), TimeSpan.FromSeconds(5)));
    }
}

/// <summary>Riktiga filsystemstester på macOS (symlänkar, 0700, ditto och paketbytet).</summary>
public sealed class MacFileSystemTests : IDisposable
{
    private readonly DirectoryInfo _tmp = TestEnv.Temp("fs-mac-");
    private readonly string _game = "";

    public MacFileSystemTests()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var g = Path.Combine(_tmp.FullName, "World of Warcraft", "_forever_");
        Directory.CreateDirectory(Path.Combine(g, "Interface", "AddOns", "ForeverScout"));
        Directory.CreateDirectory(Path.Combine(g, "WTF", "Account", "ACC", "SavedVariables"));
        Directory.CreateDirectory(Path.Combine(g, "Cache", "ADB", "enUS"));
        File.WriteAllText(Path.Combine(g, "WTF", "Account", "ACC", "SavedVariables", "ForeverScout.lua"), "ForeverScoutDB = {}");
        File.WriteAllText(Path.Combine(g, "WTF", "Account", "ACC", "SavedVariables", "Other.lua"), "Secret = 1");
        File.WriteAllText(Path.Combine(g, "WTF", "Config.wtf"), "SET x");
        File.WriteAllBytes(Path.Combine(g, "Cache", "ADB", "enUS", "DBCache.bin"), [1]);
        _game = SafeFs.SetGameDir(g)!;
    }

    public void Dispose()
    {
        SafeFs.SetGameDir(null);
        SafeFs.OwnBundleOverride = null;
        try { _tmp.Delete(true); } catch (IOException) { }
    }

    private string G(params string[] p) => Path.Combine([_game, .. p]);

    [MacFact]
    public void GameDirRulesMatchWindows()
    {
        Assert.StartsWith("/private/", _game); // /var → /private/var följs en gång i valet
        Assert.Equal("ForeverScoutDB = {}", SafeFs.ReadAllText(G("WTF", "Account", "ACC", "SavedVariables", "ForeverScout.lua")));
        Assert.Single(SafeFs.ReadAllBytes(G("Cache", "ADB", "enUS", "DBCache.bin")));
        SafeFs.WriteAllText(G("Interface", "AddOns", "ForeverScout", "ForeverScout.toc"), "## Version: 1");
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(G("WTF", "Account", "ACC", "SavedVariables", "Other.lua")));
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(G("WTF", "Config.wtf")));
        Assert.Throws<SafeFsException>(() => SafeFs.WriteAllText(G("Interface", "AddOns", "Evil", "x.lua"), "x"));
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes("/etc/hosts"));
    }

    [MacFact]
    public void CaseInsensitiveRulesOnApfs() =>
        Assert.Equal("ForeverScoutDB = {}", SafeFs.ReadAllText(G("wtf", "ACCOUNT", "ACC", "savedvariables", "FOREVERSCOUT.LUA")));

    [MacFact]
    public void SymlinksInsideTheGameDirAreRefused()
    {
        var outside = Path.Combine(_tmp.FullName, "secret");
        Directory.CreateDirectory(Path.Combine(outside, "SavedVariables"));
        File.WriteAllText(Path.Combine(outside, "SavedVariables", "ForeverScout.lua"), "ForeverScoutDB = {}");
        Directory.CreateSymbolicLink(G("WTF", "Account", "LINKED"), outside);
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(G("WTF", "Account", "LINKED", "SavedVariables", "ForeverScout.lua")));
        // Symlänk direkt på en tillåten fil.
        var sv2 = G("Cache", "ADB", "deDE");
        Directory.CreateDirectory(sv2);
        File.CreateSymbolicLink(Path.Combine(sv2, "DBCache.bin"), "/etc/hosts");
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(Path.Combine(sv2, "DBCache.bin")));
    }

    [MacFact]
    public void AppDataDirIsPrivate()
    {
        var mode = File.GetUnixFileMode(SafeFs.AppDataDir);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
    }

    [MacFact]
    public void BundleIsSwappedForTheNewVersionAndOldIsCleanedUp()
    {
        var apps = Path.Combine(_tmp.FullName, "Applications");
        var bundle = Path.Combine(apps, "Forever Scout.app");
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(bundle, "Contents", "Info.plist"), "old");
        File.WriteAllText(Path.Combine(bundle, "Contents", "MacOS", "ForeverScout"), "old");
        bundle = SafeFs.RealPath(SafeFs.Normalize(bundle));
        SafeFs.OwnBundleOverride = bundle;

        // Den nya versionen som zip (som ditto -c -k --keepParent skapar den).
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[] { ("Forever Scout.app/Contents/Info.plist", "new"), ("Forever Scout.app/Contents/MacOS/ForeverScout", "new") })
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open());
                w.Write(text);
            }
        }
        var staged = MacBundleUpdater.Stage(ms.ToArray()); // ditto + (osignerat) ingen kodsignaturkontroll
        Assert.EndsWith("/.ForeverScout-update/new/Forever Scout.app", staged);
        MacBundleUpdater.Swap(staged);
        Assert.Equal("new", File.ReadAllText(Path.Combine(bundle, "Contents", "MacOS", "ForeverScout")));
        Assert.True(Directory.Exists(Path.Combine(SafeFs.BundleUpdateDir!, "old.app")));
        MacBundleUpdater.CleanupOld();
        Assert.False(Directory.Exists(SafeFs.BundleUpdateDir!));

        // Inget annat bredvid paketet får röras.
        File.WriteAllText(Path.Combine(apps, "Other.txt"), "x");
        Assert.Throws<SafeFsException>(() => SafeFs.DeleteFile(Path.Combine(bundle[..bundle.LastIndexOf('/')], "Other.txt")));
    }

    [MacFact]
    public void TranslocatedAppRefusesToUpdate()
    {
        SafeFs.OwnBundleOverride = "/private/var/folders/x/T/AppTranslocation/1234/d/Forever Scout.app";
        Assert.Equal(ErrorCodes.AppTranslocated, Assert.IsAssignableFrom<ScoutException>(Record.Exception(() => MacBundleUpdater.Stage([1, 2, 3]))).Code);
    }
}
