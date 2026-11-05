using System.Globalization;

namespace ForeverScout.Core;

/// <summary>Steg i självuppdateringen (för diagnostik och ett begripligt felmeddelande).</summary>
public static class UpdateSteps
{
    public const string Download = "download";
    public const string Stage = "stage";
    public const string SwapDelete = "swap-delete";
    public const string SwapMove1 = "swap-move1";
    public const string SwapMove2 = "swap-move2";
    public const string Restart = "restart";
}

/// <summary>Självuppdateringen misslyckades i ett visst steg. Bär bara filnamnet (aldrig hela sökvägen).</summary>
public sealed class UpdateStepException(string step, Exception inner, string? fileName)
    : ScoutException(ErrorCodes.UpdateFailed, step, inner)
{
    public string Step { get; } = step;
    public string ExceptionType => InnerException!.GetType().Name;
    public int HResult32 => InnerException!.HResult;
    public string? FileName { get; } = fileName;
    /// <summary>Filen var låst/upptagen (t.ex. antivirus som skannar den nyss skrivna exe:n).</summary>
    public bool FileInUse => InnerException is IOException or UnauthorizedAccessException;

    /// <summary>En rad till uploads.log och lägerjournalen: steg, typ, HResult och filnamn.</summary>
    public string Diagnostic => $"step={Step} {ExceptionType} 0x{HResult32:X8}" + (FileName is null ? "" : $" file={FileName}");
}

/// <summary>
/// Försöker igen när Windows håller en fil (Defender/SmartScreen skannar ofta en nyss skriven 108 MB-exe i några
/// sekunder, och då misslyckas flytt/radering med "sharing violation"). Ungefär 10 försök under ~15 s.
/// </summary>
public static class IoRetry
{
    /// <summary>Väntetider mellan försöken (ms). Summa ≈ 15 s.</summary>
    public static int[] DelaysMs { get; set; } = [200, 400, 700, 1000, 1400, 1800, 2200, 2600, 3000];

    /// <summary>Anropas för varje misslyckat försök som följs av ett nytt (steg, försök, undantag).</summary>
    public static Action<string, int, Exception>? OnRetry { get; set; }

    public static void Run(string step, Action action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < DelaysMs.Length)
            {
                OnRetry?.Invoke(step, attempt + 1, ex);
                Thread.Sleep(DelaysMs[attempt]);
            }
        }
    }
}

/// <summary>
/// Byter klientens exe vid omstart: den nya filen (sha256-kontrollerad) sparas bredvid som ForeverScout.new.exe;
/// vid "Starta om" döps den körande exe:n om till .old.exe (tillåtet i Windows medan den körs) och den nya flyttas in.
/// Är .old.exe låst (en gammal process kör fortfarande från den) används .old-&lt;tidsstämpel&gt;.exe. Alla flyttar och
/// raderingar görs med <see cref="IoRetry"/>; ett misslyckat andra steg rullas tillbaka.
/// Ingen kod laddas ner och körs utanför den vanliga exe:n, inga skript, ingen admin.
/// </summary>
public static class SelfUpdater
{
    public static string CurrentExe => SafeFs.OwnExeOverride ?? Environment.ProcessPath ?? throw new ScoutException(ErrorCodes.ExePath);
    public static string NewExe => Path.ChangeExtension(CurrentExe, ".new.exe");
    public static string OldExe => Path.ChangeExtension(CurrentExe, ".old.exe");
    public static string Stem => CurrentExe[..^4];

    public static Version CurrentVersion => System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    public static bool IsNewer(string? remote) =>
        Version.TryParse(remote, out var v) && v > new Version(CurrentVersion.Major, CurrentVersion.Minor, Math.Max(0, CurrentVersion.Build));

    /// <summary>Städar bort .old.exe och .old-&lt;tidsstämpel&gt;.exe från tidigare uppdateringar (bästa försök, inga omförsök).</summary>
    public static void CleanupOld()
    {
        foreach (var f in SafeFs.OwnExeLeftovers())
        {
            if (!Path.GetFileName(f).Contains(".old", StringComparison.OrdinalIgnoreCase)) continue;
            try { SafeFs.DeleteFile(f); } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (SafeFsException) { }
        }
    }

    private static UpdateStepException Fail(string step, Exception ex, string path) => new(step, ex, Path.GetFileName(path));

    /// <summary>Sparar den nya exe:n som .new.exe (rester från ett tidigare försök tas bort först), med omförsök.</summary>
    public static void Stage(byte[] exe)
    {
        try
        {
            IoRetry.Run(UpdateSteps.Stage, () => SafeFs.DeleteFile(NewExe + ".tmp"));
            IoRetry.Run(UpdateSteps.Stage, () => SafeFs.DeleteFile(NewExe));
            SafeFs.WriteAllBytes(NewExe, exe, move => IoRetry.Run(UpdateSteps.Stage, move));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Fail(UpdateSteps.Stage, ex, NewExe); }
    }

    public static bool HasStaged => SafeFs.FileExists(NewExe);

    /// <summary>
    /// Har appen just uppdaterats? Markören (1.0.6+, sätts före Swap) eller en annan version än förra körningen ger
    /// "uppdaterad från X". En omstart (--restarted) utan känd tidigare version (uppdaterad från en klient ≤ 1.0.5, som
    /// inte skriver LastRunVersion) ger "uppdaterad" utan X. En vanlig omstart av samma version (t.ex. språkbyte) gör inte det.
    /// </summary>
    public static (bool Updated, string? From) DetectUpdate(string? justUpdatedFrom, string? lastRunVersion, string current, bool restarted)
    {
        var from = justUpdatedFrom ?? (lastRunVersion is { } last && last != current ? last : null);
        if (from == current) from = null;
        return (from != null || (restarted && lastRunVersion is null), from);
    }

    /// <summary>Byter filer; anroparen startar sedan den nya exe:n och avslutar sig själv.</summary>
    public static void Swap() => Swap(DateTime.Now);

    internal static void Swap(DateTime now)
    {
        if (!HasStaged) return;
        // 1) En ledig plats för den körande exe:n: .old.exe, eller .old-<tidsstämpel>.exe om .old.exe är låst.
        var oldTarget = OldExe;
        if (SafeFs.FileExists(oldTarget))
        {
            try { IoRetry.Run(UpdateSteps.SwapDelete, () => SafeFs.DeleteFile(oldTarget)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                oldTarget = $"{Stem}.old-{now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}.exe";
            }
        }
        // 2) Den körande exe:n åt sidan (tillåtet medan den körs).
        try { IoRetry.Run(UpdateSteps.SwapMove1, () => SafeFs.MoveFile(CurrentExe, oldTarget)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Fail(UpdateSteps.SwapMove1, ex, CurrentExe); }
        // 3) Den nya på plats. Misslyckas det: flytta tillbaka den gamla så att programmet fortfarande kan startas.
        try { IoRetry.Run(UpdateSteps.SwapMove2, () => SafeFs.MoveFile(NewExe, CurrentExe)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { IoRetry.Run(UpdateSteps.SwapMove2, () => SafeFs.MoveFile(oldTarget, CurrentExe)); }
            catch (Exception rb) when (rb is IOException or UnauthorizedAccessException) { /* rapporteras via det första felet */ }
            throw Fail(UpdateSteps.SwapMove2, ex, NewExe);
        }
    }
}
