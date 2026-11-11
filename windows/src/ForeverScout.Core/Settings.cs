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

    /// <summary>
    /// "Dela auktionspriser (Auctionator/Auctioneer)" – av som standard. Bara när den är på får SafeFs läsa
    /// Auctionator.lua/Auc-Stat-Simple.lua, och då skickas bara föremål, realm/fraktion, dag, lägsta pris och antal.
    /// </summary>
    public bool ShareAuctionPrices { get; set; }
    /// <summary>SHA-256 för senast hanterade auktionsfil (nyckel = Privacy.FileKey(sökväg)) – oförändrad fil läses inte om.</summary>
    public Dictionary<string, string> AuctionSha { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Vattenmärke per auktionsfil: senaste dag ("yyyy-MM-dd") som skickats. Nästa gång skickas den dagen och nyare.</summary>
    public Dictionary<string, string> AuctionDay { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
    /// <summary>1.0.12: frågan "Starta med Windows?" har ställts (välkomstskärmen eller en gång efter uppdatering).</summary>
    public bool AutostartAsked { get; set; }
    /// <summary>Spärrade addonversioner från senaste versionsinfo.</summary>
    public List<string> BlockedAddons { get; set; } = new();

    public DateTime? LastUploadUtc { get; set; }
    public int? Points { get; set; }
    public int? Rank { get; set; }
    /// <summary>Misslyckade självuppdateringar i rad (från 2 erbjuds nedladdningssidan). Nollas när en uppdatering lyckas.</summary>
    public int UpdateFailures { get; set; }
    /// <summary>Sätts (till den gamla versionen) precis före Swap; den nya versionen visar då fönstret och "Uppdaterad till …".</summary>
    public string? JustUpdatedFrom { get; set; }
    /// <summary>1.0.10: uppdateringen gjordes automatiskt medan fönstret var stängt – visa inte fönstret efter omstarten (spelet kan vara igång).</summary>
    public bool QuietUpdate { get; set; }
    /// <summary>Versionen som körde senast (1.0.6+). Skiljer den sig från den som körs nu har appen uppdaterats.</summary>
    public string? LastRunVersion { get; set; }
    /// <summary>Från /api/bidrag/jag: poster som servern godtagit (summa av counts) resp. bekräftade (fältet "confirmed", om det finns).</summary>
    public int? AcceptedTotal { get; set; }
    public int? ConfirmedTotal { get; set; }
    public List<ConfirmedItem> LastConfirmed { get; set; } = new();
    /// <summary>L10: högsta signerade "issued" i en godtagen versionsinfo. Äldre versionsinfo avvisas (ingen återuppspelning).</summary>
    public string? ReleaseIssued { get; set; }
    /// <summary>
    /// L11: den spelkatalog som användaren valt eller bekräftat. En automatiskt hittad katalog används först när den
    /// bekräftats (en gång). Null i inställningar från ≤ 1.0.6 – då frågar 1.0.7 en gång.
    /// </summary>
    public string? GameDirConfirmed { get; set; }
    /// <summary>L11: en automatiskt hittad katalog som användaren svarat Nej på – den föreslås inte igen.</summary>
    public string? GameDirDeclined { get; set; }

    [JsonIgnore] public string FilePath { get; private set; } = "";

    /// <summary>Är den sparade spelkatalogen vald eller bekräftad av användaren?</summary>
    public bool IsGameDirConfirmed() =>
        !string.IsNullOrEmpty(GameDir) && string.Equals(GameDir, GameDirConfirmed, StringComparison.OrdinalIgnoreCase);

    /// <summary>Användaren har valt (Inställningar) eller bekräftat (frågan) <see cref="GameDir"/>.</summary>
    public void ConfirmGameDir()
    {
        GameDirConfirmed = GameDir;
        if (string.Equals(GameDirDeclined, GameDir, StringComparison.OrdinalIgnoreCase)) GameDirDeclined = null;
    }

    /// <summary>
    /// L10: godkänner en (signaturkontrollerad) versionsinfo bara om dess "issued" inte är äldre än den senaste vi sett,
    /// och sparar det nya högsta värdet. Kastar <see cref="ApiException"/> (release-stale) annars.
    /// </summary>
    public SignedRelease AcceptRelease(SignedRelease release)
    {
        var highest = ReleaseVerifier.AcceptIssued(release.Issued, ReleaseIssued);
        if (!string.Equals(highest, ReleaseIssued, StringComparison.Ordinal))
        {
            ReleaseIssued = highest;
            Save();
        }
        return release;
    }

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
        s.AuctionSha = new(s.AuctionSha ?? new(), StringComparer.OrdinalIgnoreCase);
        s.AuctionDay = new(s.AuctionDay ?? new(), StringComparer.OrdinalIgnoreCase);
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
