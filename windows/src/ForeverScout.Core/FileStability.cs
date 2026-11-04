namespace ForeverScout.Core;

/// <summary>
/// Hjälp för att vänta in att spelet skrivit klart en fil. Appen listar INTE processer och läser inget ur spelets
/// minne; uppladdning startas av att ForeverScout.lua ändras (<see cref="SyncCoordinator"/>).
/// </summary>
public static class FileStability
{
    /// <summary>Väntar tills filerna inte ändrats (storlek + tid) under <paramref name="quiet"/>, högst <paramref name="max"/>.</summary>
    public static async Task WaitUntilStableAsync(IReadOnlyList<string> files, TimeSpan quiet, TimeSpan max, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + max;
        var last = Snapshot(files);
        var stableSince = DateTime.UtcNow;
        var step = TimeSpan.FromMilliseconds(Math.Clamp(quiet.TotalMilliseconds / 5, 20, 500));
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(step, ct);
            var now = Snapshot(files);
            if (now != last) { last = now; stableSince = DateTime.UtcNow; continue; }
            if (DateTime.UtcNow - stableSince >= quiet) return;
        }
    }

    /// <summary>Storlek och ändringstid för filerna (via SafeFs).</summary>
    public static string Snapshot(IReadOnlyList<string> files) => string.Join("|", files.Select(f =>
    {
        try { return SafeFs.Stat(f) is { } st ? $"{st.Length}:{st.LastWriteUtc.Ticks}" : "-"; }
        catch (SafeFsException) { return "!"; }
        catch (IOException) { return "?"; }
    }));
}
