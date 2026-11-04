namespace ForeverScout.Core;

/// <summary>
/// Byter klientens exe vid omstart: den nya filen (sha256-kontrollerad) sparas bredvid som ForeverScout.new.exe;
/// vid "Starta om" döps den körande exe:n om till .old.exe (tillåtet i Windows medan den körs) och den nya flyttas in.
/// Ingen kod laddas ner och körs utanför den vanliga exe:n, inga skript, ingen admin.
/// </summary>
public static class SelfUpdater
{
    public static string CurrentExe => Environment.ProcessPath ?? throw new ScoutException(ErrorCodes.ExePath);
    public static string NewExe => Path.ChangeExtension(CurrentExe, ".new.exe");
    public static string OldExe => Path.ChangeExtension(CurrentExe, ".old.exe");

    public static Version CurrentVersion => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    public static bool IsNewer(string? remote) =>
        Version.TryParse(remote, out var v) && v > new Version(CurrentVersion.Major, CurrentVersion.Minor, Math.Max(0, CurrentVersion.Build));

    /// <summary>Städar bort .old.exe från en tidigare uppdatering.</summary>
    public static void CleanupOld()
    {
        try { SafeFs.DeleteFile(OldExe); } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (SafeFsException) { }
    }

    public static void Stage(byte[] exe) => SafeFs.WriteAllBytes(NewExe, exe);

    public static bool HasStaged => SafeFs.FileExists(NewExe);

    /// <summary>Byter filer; anroparen startar sedan den nya exe:n och avslutar sig själv.</summary>
    public static void Swap()
    {
        if (!HasStaged) return;
        SafeFs.DeleteFile(OldExe);
        SafeFs.MoveFile(CurrentExe, OldExe);
        try { SafeFs.MoveFile(NewExe, CurrentExe); }
        catch { SafeFs.MoveFile(OldExe, CurrentExe); throw; }
    }
}
