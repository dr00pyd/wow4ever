using ForeverScout.Core;

namespace ForeverScout.Tests;

public sealed class WowLocatorTests : IDisposable
{
    private readonly DirectoryInfo _tmp = TestEnv.Temp("fs-loc-");
    private string Root => Path.Combine(_tmp.FullName, "World of Warcraft");

    private string MakeFlavor(string name, bool iface = true, bool wtf = false)
    {
        var d = Path.Combine(Root, name);
        if (iface) Directory.CreateDirectory(Path.Combine(d, "Interface", "AddOns"));
        if (wtf) Directory.CreateDirectory(Path.Combine(d, "WTF", "Account"));
        Directory.CreateDirectory(d);
        return d;
    }

    public void Dispose()
    {
        SafeFs.SetGameDir(null);
        try { _tmp.Delete(true); } catch (IOException) { }
    }

    [Fact]
    public void FindsForeverFirstAmongFlavors()
    {
        MakeFlavor("_retail_");
        MakeFlavor("_classic_era_", iface: false, wtf: true);
        var forever = MakeFlavor("_forever_");
        MakeFlavor("Data");
        var found = WowLocator.FindGameDirs([Root]);
        Assert.Equal(3, found.Count);
        Assert.Equal(forever, found[0].Path);
        Assert.DoesNotContain(found, d => d.Flavor == "Data");
    }

    [Fact]
    public void RegistryStylePathToSiblingFlavorFindsForever()
    {
        var retail = MakeFlavor("_retail_");
        var forever = MakeFlavor("_forever_");
        var found = WowLocator.FindGameDirs([retail + "\\"]);
        Assert.Equal(forever, found[0].Path);
    }

    [Fact]
    public void IgnoresFlavorWithoutInterfaceOrWtf()
    {
        Directory.CreateDirectory(Path.Combine(Root, "_empty_"));
        Assert.Empty(WowLocator.FindGameDirs([Root]));
    }

    [Fact]
    public void FreshForeverDirAcceptedWhenBuildInfoExists()
    {
        Directory.CreateDirectory(Path.Combine(Root, "_forever_"));
        File.WriteAllText(Path.Combine(Root, ".build.info"), "x"); // bara existensen räknas – innehållet läses aldrig
        Assert.Equal("_forever_", Assert.Single(WowLocator.FindGameDirs([Root])).Flavor);
    }

    [Fact]
    public void MissingAndBadRootsAreSkipped() =>
        Assert.Empty(WowLocator.FindGameDirs([Path.Combine(_tmp.FullName, "nope"), "", "::bad::", @"\\server\share\World of Warcraft", @"C:\x\..\World of Warcraft"]));

    [Fact]
    public void FindsSavedVariablesAndHotfixFiles()
    {
        var forever = MakeFlavor("_forever_", wtf: true);
        var sv = Path.Combine(forever, "WTF", "Account", "ACC1", "SavedVariables");
        Directory.CreateDirectory(sv);
        File.WriteAllText(Path.Combine(sv, "ForeverScout.lua"), "ForeverScoutDB = {}");
        File.WriteAllText(Path.Combine(sv, "OtherAddon.lua"), "Other = {}");
        Directory.CreateDirectory(Path.Combine(forever, "WTF", "Account", "ACC2", "SavedVariables"));
        var adb = Path.Combine(forever, "Cache", "ADB", "enUS");
        Directory.CreateDirectory(adb);
        File.WriteAllBytes(Path.Combine(adb, "DBCache.bin"), [1, 2, 3]);
        var real = SafeFs.SetGameDir(forever)!;
        var gd = new GameDir(real, "_forever_");
        Assert.Single(WowLocator.SavedVariableFiles(gd));
        Assert.Single(WowLocator.HotfixFiles(gd));
        // Andra addons SavedVariables är inte läsbara.
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(Path.Combine(sv, "OtherAddon.lua")));
    }

    [Fact]
    public void PrivacyRedactsAccountName()
    {
        var s = Privacy.Redact(@"Fel i C:\WoW\_forever_\WTF\Account\MYACCOUNT#1\SavedVariables\ForeverScout.lua");
        Assert.DoesNotContain("MYACCOUNT", s);
        Assert.Contains(@"WTF\Account\***\SavedVariables", s);
        Assert.DoesNotContain("MYACCOUNT", Privacy.FileKey(@"C:\WTF\Account\MYACCOUNT\x.lua"));
    }

    [DebugFact]
    public void BaseUrlOnlyHttpsOrLoopbackInDebug()
    {
        Assert.Equal("https://guide.wow4ever.se/", ApiClient.ResolveBaseUri(null).ToString());
        Assert.Equal("https://guide.wow4ever.se/", ApiClient.ResolveBaseUri("http://evil.example").ToString());
        Assert.Equal("http://127.0.0.1:3999/", ApiClient.ResolveBaseUri("http://127.0.0.1:3999").ToString());
    }

    [Fact]
    public void AddonZipRejectsTraversalAndExecutables()
    {
        using var ms = new MemoryStream();
        using (var za = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            void Add(string n, string c) { using var w = new StreamWriter(za.CreateEntry(n).Open()); w.Write(c); }
            Add("ForeverScout/ForeverScout.toc", "## Version: 1.2.0\nCore.lua");
            Add("ForeverScout/Core.lua", "-- x");
            Add("ForeverScout/../../evil.lua", "x");
            Add("ForeverScout/run.exe", "MZ");
        }
        var files = AddonInstaller.FromZip(ms.ToArray());
        Assert.Equal(["ForeverScout.toc", "Core.lua"], files.Select(f => f.RelativePath).ToArray());
        Assert.Equal("1.2.0", AddonInstaller.BundledVersion(files));

        var real = SafeFs.SetGameDir(MakeFlavor("_forever_"))!;
        var gd = new GameDir(real, "_forever_");
        Assert.True(AddonInstaller.NeedsInstall(gd, files));
        AddonInstaller.Install(gd, files);
        Assert.Equal("1.2.0", AddonInstaller.InstalledVersion(gd));
        Assert.False(AddonInstaller.NeedsInstall(gd, files));
    }

    [Fact]
    public void BackoffGrowsAndCaps()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), Uploader.Backoff(1));
        Assert.Equal(TimeSpan.FromSeconds(60), Uploader.Backoff(2));
        Assert.Equal(TimeSpan.FromHours(1), Uploader.Backoff(20));
    }
}
