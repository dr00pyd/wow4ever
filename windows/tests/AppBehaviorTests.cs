using System.Text.RegularExpressions;
using ForeverScout.Core;

namespace ForeverScout.Tests;

public class StringsTests
{
    private static int[] Placeholders(string s) =>
        Regex.Matches(s, @"\{(\d+)").Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(x => x).ToArray();

    [Fact]
    public void EveryKeyHasSwedishAndEnglishWithSamePlaceholders()
    {
        Assert.NotEmpty(Strings.All);
        foreach (var (key, (sv, en)) in Strings.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(sv), $"{key}: sv saknas");
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key}: en saknas");
            Assert.Equal(Placeholders(sv), Placeholders(en));
        }
    }

    [Fact]
    public void EveryErrorCodeIsTranslated()
    {
        foreach (var code in ErrorCodes.All) Assert.True(Strings.Has("err." + code), "err." + code);
        foreach (var code in ErrorCodes.Server) Assert.True(Strings.Has("srv." + code), "srv." + code);
    }

    [Fact]
    public void EveryDataKindHasNameAndDescription()
    {
        foreach (var k in Enum.GetValues<DataKind>())
        {
            Assert.True(Strings.Has("kind." + k));
            Assert.True(Strings.Has("desc." + k));
        }
    }

    [Fact]
    public void ErrorsAreTranslatedNotPassedThrough()
    {
        try
        {
            Strings.Apply("en");
            Assert.StartsWith("That name is taken.", Strings.Error(new ApiException("name-taken", System.Net.HttpStatusCode.Conflict) { FromServer = true }));
            Assert.Contains("weird-code", Strings.Error(new ApiException("weird-code", System.Net.HttpStatusCode.BadRequest) { FromServer = true }));
            Assert.Equal("ForeverScout.lua could not be read (line 3).", Strings.Error(Assert.Throws<LuaParseException>(() => LuaParser.ParseGlobals("A = {\n x = 1,\n y = @\n}"))));
            Assert.Equal("Could not read or write a file.", Strings.Error(new IOException("Åtkomst nekad C:\\WTF\\Account\\SECRET")));
            Strings.Apply("sv");
            Assert.StartsWith("Namnet är upptaget.", Strings.Error(new ApiException("name-taken", System.Net.HttpStatusCode.Conflict) { FromServer = true }));
        }
        finally { Strings.Apply("sv"); }
    }
}

public class CloseBehaviorTests
{
    [Theory]
    [InlineData(CloseCause.WindowsShutDown)]
    [InlineData(CloseCause.TaskManagerClosing)]
    [InlineData(CloseCause.ApplicationExit)]
    public void ShutdownIsNeverBlocked(CloseCause cause)
    {
        var s = new Settings { DontAskOnClose = true, CloseAction = "tray" };
        Assert.Equal(CloseDecision.Exit, CloseBehavior.Decide(cause, exitRequested: false, s));
        Assert.Equal(CloseDecision.Exit, CloseBehavior.Decide(cause, exitRequested: false, new Settings()));
    }

    [Fact]
    public void XAsksByDefault() =>
        Assert.Equal(CloseDecision.Ask, CloseBehavior.Decide(CloseCause.UserClosing, false, new Settings()));

    [Fact]
    public void ExitFromMenuNeverAsks() =>
        Assert.Equal(CloseDecision.Exit, CloseBehavior.Decide(CloseCause.UserClosing, exitRequested: true, new Settings()));

    [Theory]
    [InlineData(CloseDecision.Exit, "exit")]
    [InlineData(CloseDecision.Tray, "tray")]
    public void DontAskAgainRemembersChoice(CloseDecision choice, string stored)
    {
        var s = new Settings();
        CloseBehavior.Remember(s, choice, dontAskAgain: true);
        Assert.True(s.DontAskOnClose);
        Assert.Equal(stored, s.CloseAction);
        Assert.Equal(choice, CloseBehavior.Decide(CloseCause.UserClosing, false, s));
    }

    [Fact]
    public void WithoutCheckboxNothingIsRemembered()
    {
        var s = new Settings();
        CloseBehavior.Remember(s, CloseDecision.Exit, dontAskAgain: false);
        Assert.False(s.DontAskOnClose);
        Assert.Equal(CloseDecision.Ask, CloseBehavior.Decide(CloseCause.UserClosing, false, s));
    }

    [Fact]
    public void CloseSettingsSurviveSaveAndLoad()
    {
        var dir = Directory.CreateTempSubdirectory("fs-set-");
        try
        {
            var s = Settings.Load(dir.FullName);
            CloseBehavior.Remember(s, CloseDecision.Exit, true);
            s.TrayHintShown = true;
            s.Save();
            var again = Settings.Load(dir.FullName);
            Assert.True(again.DontAskOnClose);
            Assert.Equal("exit", again.CloseAction);
            Assert.True(again.TrayHintShown);
        }
        finally { SafeFs.UseAppDataDir(TestEnv.AppData); dir.Delete(true); }
    }
}

public class SingleInstanceTests
{
    [Fact]
    public void SecondStartSignalsFirstAndIsRefused()
    {
        var id = "ForeverScoutTest." + Guid.NewGuid().ToString("N");
        using var activated = new ManualResetEventSlim();
        using var first = SingleInstance.TryAcquire(id, activated.Set);
        Assert.NotNull(first);
        // Andra starten: annan tråd (en mutex är återinträdbar för sin egen tråd), som i en annan process.
        SingleInstance? second = null;
        OnOtherThread(() => { second = SingleInstance.TryAcquire(id, () => { }); if (second is null) SingleInstance.SignalExisting(id); });
        Assert.Null(second);
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)), "första instansen fick ingen signal");
    }

    [Fact]
    public void AfterFirstExitsANewInstanceCanStart()
    {
        var id = "ForeverScoutTest." + Guid.NewGuid().ToString("N");
        // Ta och släpp på samma tråd (som appen gör vid avslut).
        bool firstOk = false, nextOk = false;
        OnOtherThread(() => { var f = SingleInstance.TryAcquire(id, () => { }); firstOk = f != null; f?.Dispose(); });
        Assert.True(firstOk);
        OnOtherThread(() => { var n = SingleInstance.TryAcquire(id, () => { }, TimeSpan.FromSeconds(2)); nextOk = n != null; n?.Dispose(); });
        Assert.True(nextOk);
    }

    /// <summary>Egen tråd (inte Task.Run – .Result kan köra uppgiften på samma tråd, och en mutex är återinträdbar).</summary>
    private static void OnOtherThread(Action a)
    {
        var t = new Thread(() => a());
        t.Start();
        t.Join();
    }

    [Fact]
    public void SignalWithoutRunningInstanceReturnsFalse() =>
        Assert.False(SingleInstance.SignalExisting("ForeverScoutTest." + Guid.NewGuid().ToString("N")));
}

public class V1DataTests
{
    [Fact]
    public void SettingsWithBomAreRead()
    {
        var dir = Directory.CreateTempSubdirectory("fs-bom-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "settings.json"), "{\"Language\":\"en\",\"AutoUpload\":false}", new System.Text.UTF8Encoding(true));
            var s = Settings.Load(dir.FullName);
            Assert.Equal("en", s.Language);
            Assert.False(s.AutoUpload);
        }
        finally { SafeFs.UseAppDataDir(TestEnv.AppData); dir.Delete(true); }
    }

    [Fact]
    public void MeCountsAreAcceptedNotConfirmed()
    {
        // Serverns faktiska form (lib/scout/db.ts me()): counts = godtagna poster per typ; "confirmed" finns inte.
        var json = """{"name":"x","points":143,"rank":4,"counts":{"npcs":21,"trainers":6,"vendors":7,"quests":9,"objects":4,"loot":8,"flights":3,"hotfix":1},"uploads":3,"held":0,"lastUpload":1793750000000,"recent":[]}""";
        var me = System.Text.Json.JsonSerializer.Deserialize<MeResult>(json)!;
        Assert.Equal(58, me.AcceptedTotal()); // hotfix räknas inte
        Assert.Null(me.Confirmed);            // visas som "–" tills servern skickar fältet
        var withConfirmed = System.Text.Json.JsonSerializer.Deserialize<MeResult>("""{"confirmed":12}""")!;
        Assert.Equal(12, withConfirmed.Confirmed);
        Assert.Null(withConfirmed.AcceptedTotal());
    }

    [Fact]
    public void NameTakenPointsToLinkCode()
    {
        try
        {
            Strings.Apply("sv");
            Assert.Equal("Namnet är upptaget. Valde du det på webbsidan? Koppla med en kod från guide.wow4ever.se/bidra i stället.",
                Strings.Error(new ApiException("name-taken", System.Net.HttpStatusCode.Conflict) { FromServer = true }));
            Strings.Apply("en");
            Assert.Contains("Link with a code", Strings.Error(new ApiException("name-taken", System.Net.HttpStatusCode.Conflict) { FromServer = true }));
        }
        finally { Strings.Apply("sv"); }
    }
}
