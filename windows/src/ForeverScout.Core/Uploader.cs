namespace ForeverScout.Core;

public sealed class SyncReport
{
    public int FilesUploaded { get; set; }
    public int FilesUnchanged { get; set; }
    /// <summary>Filer som inte skickades eftersom addonversionen i dem är spärrad.</summary>
    public int FilesBlocked { get; set; }
    /// <summary>Filer som inte gick att parsa helt (spelet skriver kanske fortfarande) – skickas inte.</summary>
    public int FilesIncomplete { get; set; }
    /// <summary>Filer som innehåller annat än exakt en global, ForeverScoutDB – vägras och skickas aldrig.</summary>
    public int FilesRefused { get; set; }
    /// <summary>Filer som servern avvisade (4xx, t.ex. bad-file). Räknas inte som uppladdade – skickas igen senare.</summary>
    public int FilesRejected { get; set; }
    public int HotfixStored { get; set; }
    /// <summary>Auktionspriser (rader) som servern sparade i den här körningen.</summary>
    public int AuctionRows { get; set; }
    public List<ConfirmedItem> NewConfirmed { get; } = new();
    /// <summary>Fel som ska visas (översätts med <see cref="Strings.Error"/>).</summary>
    public Exception? Error { get; set; }
    /// <summary>Felet är tillfälligt (nät, 5xx, 429) – ett nytt försök är schemalagt.</summary>
    public bool WillRetry { get; set; }
    /// <summary>Servern (422) eller spärrlistan säger att addonen måste uppdateras.</summary>
    public bool AddonBlocked { get; set; }
    public bool NothingToDo => FilesUploaded == 0 && HotfixStored == 0 && AuctionRows == 0 && Error is null && !AddonBlocked;
}

/// <summary>
/// Laddar upp SavedVariables och DBCache.bin. Kön är tillståndsbaserad: varje försök skickar det som ändrats sedan
/// senaste lyckade uppladdning (servern deduplicerar ändå), och vid nätfel schemaläggs nytt försök med exponentiell backoff.
/// </summary>
public sealed class Uploader(ApiClient api, Settings settings)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>Antal POST till ladda-upp (för tester och diagnostik).</summary>
    public int UploadCount { get; private set; }

    /// <summary>Tidszonen som auktionsaddonerna räknar dagar i (spelarens; tester sätter en fast).</summary>
    public TimeZoneInfo AuctionTimeZone { get; set; } = TimeZoneInfo.Local;
    /// <summary>"I dag" för urvalet av auktionsdagar (null = <see cref="AuctionData.Today"/>; tester sätter en fast dag).</summary>
    public Func<DateOnly>? AuctionToday { get; set; }

    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Max(0, attempts - 1))));

    public bool RetryDue => settings.NextRetryUtc is { } t && t <= DateTime.UtcNow;

    /// <summary>
    /// Laddar upp ändrade filer. Körningar köas (aldrig två samtidigt). En fil skickas bara om den går att parsa helt
    /// och dess sha256 skiljer sig från förra lyckade uppladdningen för samma kontokatalog (nyckeln är en hash av sökvägen).
    /// </summary>
    /// <param name="force">Skicka även oförändrade filer (används inte av appen; servern deduplicerar ändå).</param>
    public async Task<SyncReport> SyncAsync(GameDir dir, bool force, CancellationToken ct = default)
    {
        var report = new SyncReport();
        await _gate.WaitAsync(ct);
        try
        {
            foreach (var file in WowLocator.SavedVariableFiles(dir))
            {
                var key = Privacy.FileKey(file);
                byte[] bytes = ScoutStats.ReadShared(file);
                var sha = ApiClient.Sha256Hex(bytes);
                if (!force && settings.UploadedSha.TryGetValue(key, out var prev) && prev == sha) { report.FilesUnchanged++; continue; }
                var peek = Peek(bytes);
                if (!peek.Complete)
                {
                    // Halvskriven fil (eller trasig): skicka inte. En ny ändringshändelse/poll försöker igen.
                    report.FilesIncomplete++;
                    continue;
                }
                if (!peek.OnlyOurs)
                {
                    // Bara vår addons data får lämna datorn: exakt en global, ForeverScoutDB.
                    report.FilesRefused++;
                    report.Error = new ScoutException(ErrorCodes.NotOurFile);
                    UploadLog.Append("ladda-upp", bytes.Length, sha, "refused:not-our-file");
                    continue;
                }
                var (addonVersion, maxSeq) = (peek.Addon, peek.Seq);
                if (addonVersion != null && settings.BlockedAddons.Contains(addonVersion))
                {
                    // Servern skulle svara 422 – spara bandbredden och be om en ny addon.
                    report.FilesBlocked++;
                    report.AddonBlocked = true;
                    continue;
                }
                UploadResult res;
                UploadCount++;
                try { res = await api.UploadAsync(bytes, ct); }
                catch (ApiException ex)
                {
                    UploadLog.Append("ladda-upp", bytes.Length, sha, "error:" + ex.Code + (ex.Status is { } st ? ":" + (int)st : "")
                        + (ex.ServerDetail is { } det ? " " + det : ""));
                    if (ex.Rejected && ex.Code != ErrorCodes.AddonBlocked)
                    {
                        // Servern avvisade filen (t.ex. bad-file "Okänd eller saknad klientbuild"). Den markeras INTE som
                        // uppladdad (UploadedSha/UploadedSeq rörs inte), så den skickas igen vid "Ladda upp", nästa start,
                        // efter en versionskontroll och när filen ändras. Övriga filer fortsätter.
                        report.FilesRejected++;
                        report.Error = ex;
                        continue;
                    }
                    if (!(ex.FromServer && ex.Code == ErrorCodes.AddonBlocked)) throw;
                    report.FilesBlocked++;
                    report.AddonBlocked = true;
                    if (addonVersion != null && !settings.BlockedAddons.Contains(addonVersion)) settings.BlockedAddons.Add(addonVersion);
                    continue;
                }
                UploadLog.Append("ladda-upp", bytes.Length, sha,
                    $"ok lastSeq={res.LastSeq} newConfirmed={res.NewConfirmed?.Count ?? 0} points={res.Points}");
                report.FilesUploaded++;
                settings.UploadedSha[key] = sha;
                if (res.LastSeq is long seq) settings.UploadedSeq[key] = seq;
                else if (maxSeq is long local) settings.UploadedSeq[key] = local;
                if (res.Points is int p) settings.Points = p;
                if (res.Rank is int r) settings.Rank = r;
                if (res.NewConfirmed is { Count: > 0 } nc)
                {
                    report.NewConfirmed.AddRange(nc);
                    settings.LastConfirmed = nc.Take(20).ToList();
                }
                settings.LastUploadUtc = DateTime.UtcNow;
                settings.Save();
            }

            if (settings.UploadHotfix)
                foreach (var file in WowLocator.HotfixFiles(dir))
                {
                    var key = Privacy.FileKey(file);
                    // Kopiera först (spelet kan ha filen öppen), hasha sedan.
                    byte[] bytes = ScoutStats.ReadShared(file);
                    var sha = ApiClient.Sha256Hex(bytes);
                    if (settings.HotfixSha.TryGetValue(key, out var prev) && prev == sha) continue;
                    HotfixResult res;
                    try { res = await api.UploadHotfixAsync(bytes, ct); }
                    catch (ApiException ex)
                    {
                        UploadLog.Append("hotfix", bytes.Length, sha, "error:" + ex.Code + (ex.ServerDetail is { } det ? " " + det : ""));
                        if (ex.Rejected) { report.Error ??= ex; continue; } // inte markerad som skickad – nytt försök senare
                        throw;
                    }
                    UploadLog.Append("hotfix", bytes.Length, sha, res.Stored ? "ok stored" : "ok duplicate");
                    settings.HotfixSha[key] = sha;
                    if (res.Stored) report.HotfixStored++;
                    settings.Save();
                }

            if (settings.ShareAuctionPrices && SafeFs.AuctionFilesAllowed)
                await SyncAuctionsAsync(dir, report, ct);

            // Inga nätfel: nollställ backoff-kön (ett avvisat svar är inte ett nätfel och schemalägger inget).
            settings.RetryAttempts = 0;
            settings.NextRetryUtc = null;
            settings.Save();
        }
        catch (ApiException ex)
        {
            report.Error = ex;
            if (ex.Transient) Schedule(report);
        }
        catch (IOException ex)
        {
            report.Error = new ScoutException(ErrorCodes.Io, inner: ex);
            Schedule(report);
        }
        catch (UnauthorizedAccessException ex)
        {
            report.Error = new ScoutException(ErrorCodes.Io, inner: ex);
        }
        catch (SafeFsException ex)
        {
            report.Error = ex; // åtkomst utanför det tillåtna – skyddet slog till, inget skickades
        }
        finally { _gate.Release(); }
        return report;
    }

    /// <summary>
    /// "Dela auktionspriser": läser Auctionator.lua/Auc-Stat-Simple.lua (bara om filen ändrats sedan sist), plockar ut
    /// föremål, realm/fraktion, dag, lägsta pris per styck och antal (<see cref="IAuctionReader"/>) och skickar dagarna
    /// från vattenmärket och framåt. Filen själv skickas aldrig. Nätfel kastas vidare (backoff som för övriga filer);
    /// ett avvisat svar (4xx) markerar inget som skickat, så samma dagar skickas igen nästa gång.
    /// </summary>
    private async Task SyncAuctionsAsync(GameDir dir, SyncReport report, CancellationToken ct)
    {
        var today = AuctionToday?.Invoke() ?? AuctionData.Today(AuctionTimeZone);
        foreach (var (file, reader) in WowLocator.AuctionFiles(dir))
        {
            var key = Privacy.FileKey(file);
            var mtime = SafeFs.Stat(file)?.LastWriteUtc ?? DateTime.UtcNow;
            byte[] bytes = ScoutStats.ReadShared(file);
            var sha = ApiClient.Sha256Hex(bytes);
            if (settings.AuctionSha.TryGetValue(key, out var prevSha) && prevSha == sha) continue;
            var read = reader.Read(bytes, mtime, AuctionTimeZone);
            if (!read.Complete) { report.FilesIncomplete++; continue; }
            DateOnly? mark = settings.AuctionDay.TryGetValue(key, out var w)
                && DateOnly.TryParseExact(w, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var wd) ? wd : null;
            var rows = AuctionData.Select(read.Rows, mark, today);
            if (rows.Count == 0)
            {
                UploadLog.Append("auktioner", 0, sha, $"nothing-new {reader.Source}" + (read.Problem is { } p ? " " + p : ""));
                settings.AuctionSha[key] = sha;
                settings.Save();
                continue;
            }
            bool rejected = false;
            int accepted = 0;
            for (int i = 0; i < rows.Count && !rejected; i += AuctionData.ChunkRows)
            {
                var chunk = rows.GetRange(i, Math.Min(AuctionData.ChunkRows, rows.Count - i));
                var payload = AuctionData.Payload(reader.Source, chunk);
                var psha = ApiClient.Sha256Hex(payload);
                try
                {
                    var res = await api.UploadAuctionsAsync(payload, ct);
                    accepted += res.Accepted;
                    UploadLog.Append("auktioner", payload.Length, psha, $"ok {reader.Source} rows={chunk.Count} accepted={res.Accepted} skipped={res.Skipped}");
                }
                catch (ApiException ex)
                {
                    UploadLog.Append("auktioner", payload.Length, psha, "error:" + ex.Code + (ex.Status is { } st ? ":" + (int)st : "")
                        + (ex.ServerDetail is { } det ? " " + det : ""));
                    if (!ex.Rejected) throw;
                    report.Error ??= ex;
                    rejected = true;
                }
            }
            report.AuctionRows += accepted;
            if (rejected) continue; // inte markerad som skickad – nytt försök när filen ändras eller appen startar om
            settings.AuctionSha[key] = sha;
            settings.AuctionDay[key] = rows.Max(r => r.Day).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            settings.Save();
        }
    }

    private void Schedule(SyncReport report)
    {
        settings.RetryAttempts++;
        settings.NextRetryUtc = DateTime.UtcNow + Backoff(settings.RetryAttempts);
        report.WillRetry = true;
        try { settings.Save(); } catch { }
    }

    public readonly record struct PeekResult(bool Complete, bool OnlyOurs, string? Addon, long? Seq);

    /// <summary>
    /// Går filen att parsa helt (annars är den kanske halvskriven), och innehåller den EXAKT en global –
    /// ForeverScoutDB som tabell? Plus addonversion och högsta seq.
    /// </summary>
    public static PeekResult Peek(byte[] bytes)
    {
        Dictionary<string, object?> g;
        try { g = LuaParser.ParseGlobals(bytes); }
        catch (LuaParseException) { return new(false, false, null, null); }
        if (!g.TryGetValue("ForeverScoutDB", out var v) || v is not LuaTable t) return new(g.Count > 0, false, null, null);
        return new(true, g.Count == 1, t.Str("addon"), t.Int("seq"));
    }
}
