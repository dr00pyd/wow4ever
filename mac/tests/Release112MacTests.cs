using System.Text.RegularExpressions;
using ForeverScout.Core;
using static ForeverScout.Mac.Rules112;

namespace ForeverScout.Mac.Tests;

/// <summary>
/// 1.0.12 i macOS- och Linux-appen (granskningen 2026-11-11, docs/review/CLIENT-WINDOWS-2026-11-11.md): samma regler som
/// Windows-klientens ScoutApp/MainForm, som rena funktioner (<see cref="Rules112"/>). Core-delen (M1–M4) testas av den
/// länkade Release112Tests.cs.
/// </summary>
public class Release112MacTests
{
    // ------------------------------------------------------------------ H1: autostart

    [Theory]
    [InlineData(new string[0], true)]                          // Finder, Launchpad, programmenyn
    [InlineData(new[] { "-psn_0_12345" }, true)]               // äldre macOS skickar ett processnummer
    [InlineData(new[] { "--tray" }, false)]                    // LaunchAgent (macOS) och XDG-autostart (Linux)
    [InlineData(new[] { "--restarted" }, false)]               // efter uppdatering, språkbyte, frånkoppling
    [InlineData(new[] { "--minimized" }, false)]
    [InlineData(new[] { "--tray", "--restarted" }, false)]
    public void OnlyAStartByTheUserCountsAsUserStarted(string[] args, bool expected) =>
        Assert.Equal(expected, UserStarted(args));

    [Fact]
    public void AutostartAndRestartArgumentsMatchWhatTheAppIsStartedWith()
    {
        // XDG-autostart startar med --tray – annars skulle autostart räknas som "användaren startade". (LaunchAgent:
        // SafeFs.SetLaunchAgent skickar "--args --tray" till open; plist-texten byggs av argumenten och testas i MacSafeFsTests.)
        Assert.Contains(" --tray\n", SafeFs.LinuxAutostartDesktop("/home/u/ForeverScout.AppImage"));
        Assert.False(UserStarted(["--tray"]));
    }

    [Theory]
    [InlineData(false, false, false, "Nothing")]   // autostart/omstart: aldrig en fråga över spelet
    [InlineData(false, false, true, "Nothing")]
    [InlineData(true, true, false, "Nothing")]     // redan frågat (välkomstskärmen eller tidigare)
    [InlineData(true, false, true, "MarkAsked")]   // redan på: inget att fråga om
    [InlineData(true, false, false, "Ask")]        // befintlig användare, startade själv: en gång
    public void ExistingUsersAreAskedAboutAutostartOnceAndOnlyWhenTheyStartedTheApp(bool userStarted, bool asked, bool enabled, string expected) =>
        Assert.Equal(expected, DecideAutostartAsk(userStarted, asked, enabled).ToString());

    [Fact]
    public void AnUpgradedSettingsFileHasNotBeenAskedYet()
    {
        // settings.json från 1.0.11 saknar fältet: frågan ska ställas (en gång) nästa gång användaren startar appen.
        var s = System.Text.Json.JsonSerializer.Deserialize<Settings>("""{"Language":"sv"}""")!;
        Assert.False(s.AutostartAsked);
        Assert.Equal(AutostartAsk.Ask, DecideAutostartAsk(userStarted: true, s.AutostartAsked, autostartEnabled: false));
    }

    // ------------------------------------------------------------------ H5: utan spelkatalog

    [Theory]
    [InlineData(WowLocator.StartStep.Ready, true, "None")]
    [InlineData(WowLocator.StartStep.Ready, false, "None")]
    [InlineData(WowLocator.StartStep.Pick, true, "Pick")]
    [InlineData(WowLocator.StartStep.Pick, false, "Notify")]       // autostart: ingen dialog
    [InlineData(WowLocator.StartStep.Confirm, true, "Confirm")]
    [InlineData(WowLocator.StartStep.Confirm, false, "Notify")]    // inte heller bekräftelsefrågan
    public void WithoutAGameFolderOnlyAnInteractiveStartOpensADialog(WowLocator.StartStep step, bool interactive, string expected) =>
        Assert.Equal(expected, DecideGameDir(step, interactive).ToString());

    [Fact]
    public void WithoutAGameFolderTheMainButtonAndFooterSaySo()
    {
        Assert.Equal("pick.dir", MainButtonKey(busy: false, hasDir: false));
        Assert.Equal("upload", MainButtonKey(busy: false, hasDir: true));
        Assert.Equal("uploading", MainButtonKey(busy: true, hasDir: false));
        Assert.Equal("footer.next.nodir", FooterNextKey(retryPending: false, hasDir: false, autoUploadActive: true));
        Assert.Equal("game.none", IdleStatusKey(hasDir: false, paused: false, autoUpload: true, uploadedBefore: true));
    }

    // ------------------------------------------------------------------ H4: ärlig status

    [Theory]
    [InlineData(true, false, true, false, "auto.off")]   // förr: grönt och "Inget uppladdat än"
    [InlineData(true, false, true, true, "auto.off")]
    [InlineData(true, true, true, true, "paused")]
    [InlineData(true, true, false, true, "paused")]
    [InlineData(true, false, false, true, "last")]       // allt på: senaste uppladdningen
    public void StatusWhenAutomaticUploadIsOffOrPaused(bool hasDir, bool paused, bool autoUploadOff, bool uploadedBefore, string expected) =>
        Assert.Equal(expected, IdleStatusKey(hasDir, paused, autoUpload: !autoUploadOff, uploadedBefore));

    [Fact]
    public void NeverUploadedWithEverythingOnSaysSo() =>
        Assert.Equal("never", IdleStatusKey(hasDir: true, paused: false, autoUpload: true, uploadedBefore: false));

    [Fact]
    public void AllGoodOnlyWhenEverythingIsTrue()
    {
        Assert.True(Healthy(true, true, true, true, false, false, true));
        // Varje villkor för sig gör "Allt fungerar" falskt – t.ex. bredvid "Addonen saknas" (förr grönt ändå).
        Assert.False(Healthy(false, true, true, true, false, false, true));  // fel/omförsök i ikonen
        Assert.False(Healthy(true, false, true, true, false, false, true));  // ingen spelkatalog
        Assert.False(Healthy(true, true, false, true, false, false, true));  // addonen saknas
        Assert.False(Healthy(true, true, true, false, false, false, true));  // addonen har inte sparat något än
        Assert.False(Healthy(true, true, true, true, true, false, true));    // fel
        Assert.False(Healthy(true, true, true, true, false, true, true));    // omförsök väntar
        Assert.False(Healthy(true, true, true, true, false, false, false));  // automatisk uppladdning av/pausad
    }

    [Theory]
    [InlineData(true, true, true, "retry")]
    [InlineData(true, false, false, "retry")]
    [InlineData(false, false, true, "footer.next.nodir")]
    [InlineData(false, true, false, "footer.next.manual")]
    [InlineData(false, true, true, "footer.next")]
    public void NextSyncTellsWhatActuallyHappensNext(bool retry, bool hasDir, bool auto, string expected) =>
        Assert.Equal(expected, FooterNextKey(retry, hasDir, auto));

    [Fact]
    public void AddonPillSaysInstalledNotConnected()
    {
        Strings.Apply("en");
        try { Assert.Equal("Addon 1.0.8 installed", Texts.TF("pill.addon", "1.0.8")); }
        finally { Strings.Apply("sv"); }
    }

    // ------------------------------------------------------------------ M5: addonens fel

    [Fact]
    public void TheAddonErrorSurvivesASyncWithNothingToDo()
    {
        // Synken sätter uppladdningens fel (null när inget gick fel) – addonens fel syns fortfarande.
        Assert.Equal("addon", ShownError(uploadError: null, addonError: "addon"));
        Assert.Equal("upload", ShownError(uploadError: "upload", addonError: "addon"));
        Assert.Null(ShownError(null, null));
    }

    [Fact]
    public void TheAddonErrorClearsOnlyWhenAnUnblockedAddonIsInstalled()
    {
        Assert.False(AddonErrorResolved(null, []));                  // installationen misslyckades: inget på plats
        Assert.False(AddonErrorResolved("1.0.7", ["1.0.7"]));        // spärrad version väntar på en signerad
        Assert.True(AddonErrorResolved("1.0.8", ["1.0.7"]));
        Assert.True(AddonErrorResolved("1.0.8", []));
    }

    // ------------------------------------------------------------------ texter (sv + en, inga "Windows" på Mac/Linux)

    private static int Placeholders(string s) => Regex.Matches(s, @"\{\d\}").Count;

    [Fact]
    public void EveryRuleKeyHasAText()
    {
        string[] keys = ["game.none", "paused", "auto.off", "last", "never", "retry", "footer.next", "footer.next.nodir",
            "footer.next.manual", "footer.ok", "pick.dir", "st.nodir", "upload", "uploading", "pill.addon", "menu.upload",
            "onb.autostart", "autostart.ask", "autostart.ask.title", "gamedir.toast"];
        foreach (var k in keys) Assert.True(Strings.Has(k), k);
    }

    [Fact]
    public void LocalPlatformTextsHaveBothLanguagesAndTheBasePlaceholders()
    {
        Assert.NotEmpty(Texts.Local);
        foreach (var (key, (sv, en)) in Texts.Local)
        {
            Assert.True(key.StartsWith("mac.", StringComparison.Ordinal) || key.StartsWith("linux.", StringComparison.Ordinal), key);
            Assert.False(string.IsNullOrWhiteSpace(sv), key);
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            Assert.NotEqual(sv, en);
            var baseKey = key[(key.IndexOf('.') + 1)..];
            Assert.True(Strings.All.TryGetValue(baseKey, out var b), "basnyckel saknas: " + baseKey);
            Assert.Equal(Placeholders(b.Sv), Placeholders(sv));
            Assert.Equal(Placeholders(b.En), Placeholders(en));
            // Core-texterna nämner Windows och klockan – just därför finns de här.
            Assert.DoesNotContain("Windows", sv + en);
            Assert.DoesNotContain("klockan", sv);
            Assert.DoesNotContain("clock", en);
        }
    }

    [Theory]
    [InlineData("mac.")]
    [InlineData("linux.")]
    public void BothPlatformsHaveTheirOwnAutostartAndGameFolderTexts(string prefix)
    {
        foreach (var k in new[] { "onb.autostart", "autostart.ask", "autostart.ask.title", "gamedir.toast" })
            Assert.True(Texts.Local.ContainsKey(prefix + k) || Strings.Has(prefix + k), prefix + k);
    }

    [Fact]
    public void MacTextsPreferTheLocalPlatformTextOverTheWindowsWording()
    {
        // Testprojektet bygger Mac-appen (utan LINUX): "mac."-texterna gäller.
        Assert.Equal("mac.", Texts.Prefix);
        var (sv, en) = Texts.Local["mac.onb.autostart"];
        Assert.Contains(Texts.T("onb.autostart"), new[] { sv, en });
        Assert.DoesNotContain("Windows", Texts.T("autostart.ask"));
        Assert.DoesNotContain("Windows", Texts.T("autostart.ask.title"));
        // Delade 1.0.12-texter utan Windows-ord tas direkt från Core.
        Assert.Equal(Strings.S("footer.next.manual"), Texts.T("footer.next.manual"));
        Assert.Equal(Strings.S("pick.dir"), Texts.T("pick.dir"));
    }
}
