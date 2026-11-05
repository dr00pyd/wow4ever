using ForeverScout.Core;

namespace ForeverScout.Tests;

/// <summary>
/// Kör självuppdateringen genom SafeFs på riktiga filer (1.0.0/1.0.1 nekade ForeverScout.new.exe.tmp och kunde
/// därför aldrig uppdatera sig). Stage → Swap → CleanupOld, med exe:n i en vanlig mapp utanför WoW-katalogen.
/// </summary>
[Collection("SafeFs")]
public sealed class SelfUpdateTests : IDisposable
{
    private readonly DirectoryInfo _tmp = TestEnv.Temp("fs-selfupdate-");
    private readonly string _exe;

    public SelfUpdateTests()
    {
        var downloads = Directory.CreateDirectory(Path.Combine(_tmp.FullName, "Downloads"));
        _exe = Path.Combine(downloads.FullName, "ForeverScout.exe");
        File.WriteAllBytes(_exe, [1, 1, 1]);
        SafeFs.OwnExeOverride = _exe;
    }

    public void Dispose()
    {
        SafeFs.OwnExeOverride = null;
        try { _tmp.Delete(true); } catch (IOException) { }
    }

    [Fact]
    public void Stage_writes_new_exe_via_tmp_file()
    {
        SelfUpdater.Stage([2, 2, 2, 2]);
        Assert.True(SelfUpdater.HasStaged);
        Assert.Equal([2, 2, 2, 2], File.ReadAllBytes(SelfUpdater.NewExe));
        Assert.False(File.Exists(SelfUpdater.NewExe + ".tmp"));
    }

    [Fact]
    public void Swap_replaces_exe_and_keeps_old_until_cleanup()
    {
        SelfUpdater.Stage([2, 2]);
        SelfUpdater.Swap();
        Assert.Equal([2, 2], File.ReadAllBytes(_exe));
        Assert.Equal([1, 1, 1], File.ReadAllBytes(SelfUpdater.OldExe));
        Assert.False(File.Exists(SelfUpdater.NewExe));
        SelfUpdater.CleanupOld();
        Assert.False(File.Exists(SelfUpdater.OldExe));
    }

    [Fact]
    public void Other_files_next_to_exe_stay_blocked()
    {
        var other = Path.Combine(Path.GetDirectoryName(_exe)!, "secret.txt");
        File.WriteAllText(other, "x");
        Assert.Throws<SafeFsException>(() => SafeFs.Check(other, SafeFs.Access.Probe, isDir: false));
        Assert.Throws<SafeFsException>(() => SafeFs.Check(_exe + ".tmp", SafeFs.Access.Write, isDir: false));
    }

    // ---------------------------------------------------------------- 1.0.5: låsta filer (antivirus), rollback, diagnostik

    /// <summary>Håller filen öppen med FileShare.None i <paramref name="ms"/> ms (som Defender som skannar en ny exe).</summary>
    private static Task LockFor(string path, int ms)
    {
        var ready = new ManualResetEventSlim();
        var t = Task.Run(() =>
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            ready.Set();
            Thread.Sleep(ms);
        });
        ready.Wait();
        return t;
    }

    [Fact]
    public void Stage_succeeds_when_old_new_exe_is_briefly_locked()
    {
        File.WriteAllBytes(SelfUpdater.NewExe, [9]);
        File.WriteAllBytes(SelfUpdater.NewExe + ".tmp", [8]); // rest från ett avbrutet försök
        var retries = new List<string>();
        IoRetry.OnRetry = (step, n, ex) => retries.Add($"{step}:{n}");
        try
        {
            var lockTask = LockFor(SelfUpdater.NewExe, 1500);
            SelfUpdater.Stage([2, 2, 2]);
            lockTask.Wait();
        }
        finally { IoRetry.OnRetry = null; }
        Assert.Equal([2, 2, 2], File.ReadAllBytes(SelfUpdater.NewExe));
        Assert.False(File.Exists(SelfUpdater.NewExe + ".tmp"));
        Assert.Contains(retries, r => r.StartsWith("stage:"));
    }

    [Fact]
    public void Swap_succeeds_when_new_exe_is_briefly_locked()
    {
        SelfUpdater.Stage([2, 2]);
        var lockTask = LockFor(SelfUpdater.NewExe, 1500);
        SelfUpdater.Swap();
        lockTask.Wait();
        Assert.Equal([2, 2], File.ReadAllBytes(_exe));
        Assert.Equal([1, 1, 1], File.ReadAllBytes(SelfUpdater.OldExe));
    }

    [Fact]
    public void Swap_succeeds_when_current_exe_is_briefly_locked()
    {
        SelfUpdater.Stage([2, 2]);
        var lockTask = LockFor(_exe, 1200);
        SelfUpdater.Swap();
        lockTask.Wait();
        Assert.Equal([2, 2], File.ReadAllBytes(_exe));
    }

    [Fact]
    public void Locked_old_exe_uses_timestamped_name_and_cleanup_removes_all_old_files()
    {
        File.WriteAllBytes(SelfUpdater.OldExe, [7]);
        SelfUpdater.Stage([2, 2]);
        var saved = IoRetry.DelaysMs;
        IoRetry.DelaysMs = [50, 50];
        string ts;
        using (new FileStream(SelfUpdater.OldExe, FileMode.Open, FileAccess.Read, FileShare.None)) // en gammal process kör kvar
        {
            var now = new DateTime(2026, 11, 5, 12, 34, 56);
            SelfUpdater.Swap(now);
            ts = SelfUpdater.Stem + ".old-20261105123456.exe";
        }
        IoRetry.DelaysMs = saved;
        Assert.Equal([2, 2], File.ReadAllBytes(_exe));
        Assert.Equal([1, 1, 1], File.ReadAllBytes(ts));
        Assert.Equal([7], File.ReadAllBytes(SelfUpdater.OldExe));
        Assert.Equal(2, SafeFs.OwnExeLeftovers().Count);
        SelfUpdater.CleanupOld();
        Assert.False(File.Exists(SelfUpdater.OldExe));
        Assert.False(File.Exists(ts));
        Assert.Empty(SafeFs.OwnExeLeftovers());
    }

    [Fact]
    public void Failed_second_move_rolls_back_and_reports_the_step()
    {
        SelfUpdater.Stage([2, 2]);
        var saved = IoRetry.DelaysMs;
        IoRetry.DelaysMs = [50, 50, 50];
        UpdateStepException ex;
        using (new FileStream(SelfUpdater.NewExe, FileMode.Open, FileAccess.Read, FileShare.None)) // låst längre än alla försök
            ex = Assert.Throws<UpdateStepException>(() => SelfUpdater.Swap());
        IoRetry.DelaysMs = saved;
        Assert.Equal(UpdateSteps.SwapMove2, ex.Step);
        Assert.True(ex.FileInUse);
        Assert.Equal("ForeverScout.new.exe", ex.FileName);
        Assert.Matches(@"^step=swap-move2 IOException 0x[0-9A-F]{8} file=ForeverScout\.new\.exe$", ex.Diagnostic);
        Assert.DoesNotContain(Path.GetDirectoryName(_exe)!, ex.Diagnostic);
        Assert.Equal([1, 1, 1], File.ReadAllBytes(_exe)); // rullat tillbaka: den gamla exe:n ligger kvar på sin plats
        Assert.False(File.Exists(SelfUpdater.OldExe));
        Assert.True(File.Exists(SelfUpdater.NewExe)); // den nya finns kvar till nästa försök
    }

    [Fact]
    public void Update_error_message_names_the_locked_file_in_plain_language()
    {
        var ex = new UpdateStepException(UpdateSteps.SwapMove1, new IOException("x"), "ForeverScout.exe");
        Assert.True(ex.FileInUse);
        try
        {
            Strings.Apply("sv");
            Assert.StartsWith("Kunde inte ersätta ForeverScout.exe (filen används av ett annat program", Strings.S("update.inuse"));
            Assert.Contains("swap-move1", Strings.Error(ex));
        }
        finally { Strings.Apply("sv"); }
    }

    [Theory]
    [InlineData("ForeverScout.old-20261105123456.exe", true)]
    [InlineData("ForeverScout.old.exe", true)]
    [InlineData("ForeverScout.new.exe.tmp", true)]
    [InlineData("ForeverScout.old-123.exe", false)]
    [InlineData("ForeverScout.old-2026110512345a.exe", false)]
    [InlineData("ForeverScout.old-20261105123456.exe.tmp", false)]
    [InlineData("Other.old-20261105123456.exe", false)]
    [InlineData("ForeverScout.older.exe", false)]
    public void SafeFs_allows_exactly_the_self_update_files(string name, bool allowed)
    {
        var path = Path.Combine(Path.GetDirectoryName(_exe)!, name);
        var ok = Record.Exception(() => SafeFs.Check(path, SafeFs.Access.Write, isDir: false)) is null;
        Assert.Equal(allowed, ok);
    }
}

public class UpdateNoticeTests
{
    [Theory]
    [InlineData("1.0.5", "1.0.5", "1.0.6", true, true, "1.0.5")]   // markör satt av 1.0.5→1.0.6 (framtida väg)
    [InlineData(null, "1.0.6", "1.0.7", false, true, "1.0.6")]     // annan version än förra körningen
    [InlineData(null, null, "1.0.6", true, true, null)]            // omstart från en klient ≤ 1.0.5 (ingen markör)
    [InlineData(null, "1.0.6", "1.0.6", true, false, null)]        // språkbyte/omstart av samma version
    [InlineData(null, null, "1.0.6", false, false, null)]          // vanlig start (t.ex. ny installation)
    [InlineData(null, "1.0.6", "1.0.6", false, false, null)]       // vanlig start, samma version
    public void Detects_update_after_restart(string? marker, string? lastRun, string current, bool restarted, bool updated, string? from)
    {
        var r = SelfUpdater.DetectUpdate(marker, lastRun, current, restarted);
        Assert.Equal(updated, r.Updated);
        Assert.Equal(from, r.From);
    }

    [Fact]
    public void Update_notice_texts_exist_in_both_languages()
    {
        try
        {
            Strings.Apply("sv");
            Assert.Equal("Uppdaterad till 1.0.6 ✓", Strings.F("updated.to", "1.0.6"));
            Strings.Apply("en");
            Assert.Equal("Updated to 1.0.6 ✓", Strings.F("updated.to", "1.0.6"));
        }
        finally { Strings.Apply("sv"); }
    }
}
