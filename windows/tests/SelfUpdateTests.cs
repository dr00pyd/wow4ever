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
}
