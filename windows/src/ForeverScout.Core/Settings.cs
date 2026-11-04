using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForeverScout.Core;

public sealed class ConfirmedItem
{
    public string Kind { get; set; } = "";
    /// <summary>Serverns id (sträng i API:t, t.ex. "198").</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Relativ sökväg på sajten, eller null om posten inte har en sida.</summary>
    public string? Url { get; set; }
}

/// <summary>Inställningar och tillstånd i %APPDATA%\ForeverScout\settings.json (nyckeln ligger separat, krypterad).</summary>
public sealed class Settings
{
    public string? GameDir { get; set; }
    /// <summary>"auto", "sv" eller "en".</summary>
    public string Language { get; set; } = "auto";
    public bool AutoUpload { get; set; } = true;
    public bool UploadHotfix { get; set; } = true;
    public string? ContributorName { get; set; }
    public string? ContributorId { get; set; }

    // Nycklarna nedan är Privacy.FileKey(sökväg) – aldrig sökvägen, eftersom den innehåller kontonamnet.
    /// <summary>Senast uppladdade seq per SavedVariables-fil.</summary>
    public Dictionary<string, long> UploadedSeq { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>SHA-256 för senast uppladdade innehåll per SavedVariables-fil.</summary>
    public Dictionary<string, string> UploadedSha { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>SHA-256 för senast skickade DBCache.bin per fil.</summary>
    public Dictionary<string, string> HotfixSha { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Offline-kö: antal misslyckade försök i rad och när nästa försök görs (null = inget väntar).</summary>
    public int RetryAttempts { get; set; }
    public DateTime? NextRetryUtc { get; set; }

    /// <summary>"Pausa automatisk uppladdning" i fältmenyn (tillfälligt, utan att ändra AutoUpload).</summary>
    public bool AutoUploadPaused { get; set; }
    /// <summary>Stäng-dialogen: fråga inte igen, och vad som då ska hända ("exit" eller "tray").</summary>
    public bool DontAskOnClose { get; set; }
    public string CloseAction { get; set; } = "tray";
    /// <summary>Har tipset "körs i bakgrunden" visats en gång?</summary>
    public bool TrayHintShown { get; set; }
    /// <summary>Spärrade addonversioner från senaste versionsinfo.</summary>
    public List<string> BlockedAddons { get; set; } = new();

    public DateTime? LastUploadUtc { get; set; }
    public int? Points { get; set; }
    public int? Rank { get; set; }
    public List<ConfirmedItem> LastConfirmed { get; set; } = new();

    [JsonIgnore] public string FilePath { get; private set; } = "";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>%APPDATA%\ForeverScout (se <see cref="SafeFs.AppDataDir"/>).</summary>
    public static string DataDir => SafeFs.AppDataDir;

    /// <param name="dir">Annan datakatalog (tester) – sätter SafeFs appdatakatalog.</param>
    public static Settings Load(string? dir = null)
    {
        if (dir != null) SafeFs.UseAppDataDir(dir);
        var path = Path.Combine(DataDir, "settings.json");
        Settings s;
        try { s = SafeFs.FileExists(path) ? JsonSerializer.Deserialize<Settings>(SafeFs.ReadAllText(path), Json) ?? new() : new(); }
        catch (Exception ex) when (ex is JsonException or IOException or SafeFsException or NotSupportedException) { s = new(); }
        s.UploadedSeq = new(s.UploadedSeq, StringComparer.OrdinalIgnoreCase);
        s.HotfixSha = new(s.HotfixSha, StringComparer.OrdinalIgnoreCase);
        s.UploadedSha = new(s.UploadedSha, StringComparer.OrdinalIgnoreCase);
        s.FilePath = path;
        return s;
    }

    private readonly object _lock = new();

    public void Save()
    {
        lock (_lock)
        {
            SafeFs.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
    }
}
