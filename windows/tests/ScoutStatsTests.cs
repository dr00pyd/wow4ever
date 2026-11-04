using System.Text;
using ForeverScout.Core;

namespace ForeverScout.Tests;

public class ScoutStatsTests
{
    [Fact]
    public void CountsEachSection()
    {
        var db = (LuaTable)LuaParser.ParseGlobals(LuaParserTests.Sample)["ForeverScoutDB"]!;
        var s = new ScoutStats();
        s.Add(db, uploadedSeq: 0);
        Assert.Equal(2, s[DataKind.Npcs].Count);
        Assert.Equal(1, s[DataKind.Objects].Count);
        Assert.Equal(1, s[DataKind.Quests].Count);
        Assert.Equal(1, s[DataKind.Trainers].Count);
        Assert.Equal(1, s[DataKind.Vendors].Count);
        Assert.Equal(2, s[DataKind.Loot].Count);
        Assert.Equal(417, s.MaxSeq);
        Assert.True(s[DataKind.Npcs].Bytes > 100);
        Assert.Equal(0, s[DataKind.Npcs].Uploaded);
    }

    [Fact]
    public void UploadedCountsEntriesUpToSeq()
    {
        var db = (LuaTable)LuaParser.ParseGlobals(LuaParserTests.Sample)["ForeverScoutDB"]!;
        var s = new ScoutStats();
        s.Add(db, uploadedSeq: 413);
        Assert.Equal(1, s[DataKind.Npcs].Uploaded);    // 198 (q 412), inte 823 (q 414)
        Assert.Equal(1, s[DataKind.Vendors].Uploaded); // q 413
        Assert.Equal(0, s[DataKind.Loot].Uploaded);
    }

    [Fact]
    public void BrokenFileIsReportedWithoutAccountName()
    {
        var dir = Directory.CreateTempSubdirectory("fs-stats-");
        try
        {
            var sv = Path.Combine(dir.FullName, "WTF", "Account", "SECRETACCOUNT", "SavedVariables");
            Directory.CreateDirectory(sv);
            var f = Path.Combine(sv, "ForeverScout.lua");
            SafeFs.SetGameDir(dir.FullName);
            File.WriteAllText(f, "ForeverScoutDB = { npcs = os.exit() }", Encoding.UTF8);
            var s = new ScoutStats();
            s.AddFile(f, 0);
            var err = Assert.Single(s.Errors);
            Assert.DoesNotContain("SECRETACCOUNT", err);
        }
        finally { SafeFs.SetGameDir(null); dir.Delete(true); }
    }

    [Fact]
    public void FixturesParse()
    {
        var fixtures = FixtureFiles();
        foreach (var f in fixtures)
        {
            var g = LuaParser.ParseGlobals(File.ReadAllBytes(f));
            if (g.TryGetValue("ForeverScoutDB", out var v))
            {
                var s = new ScoutStats();
                s.Add(Assert.IsType<LuaTable>(v), 0);
                Assert.True(s[DataKind.Npcs].Count > 0, Path.GetFileName(f) + ": inga NPC:er");
                Assert.True(s.MaxSeq > 0, Path.GetFileName(f) + ": seq saknas");
                // Allt uppladdat om uppladdad seq = högsta seq.
                var all = new ScoutStats();
                all.Add((LuaTable)v!, s.MaxSeq);
                foreach (var k in all.ByKind.Values) Assert.Equal(k.Count, k.Uploaded);
            }
        }
    }

    /// <summary>client/fixtures/*.lua (skrivs av addon-agenten) – tom lista om de inte finns än.</summary>
    internal static List<string> FixtureFiles()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            if (d.Name.Equals("client", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.Combine(d.FullName, "fixtures");
                return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.lua").ToList() : [];
            }
        }
        return [];
    }
}
