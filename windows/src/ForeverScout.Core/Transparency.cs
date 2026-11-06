using System.Globalization;
using System.Text;

namespace ForeverScout.Core;

/// <summary>
/// Lokal logg över allt som skickats: %APPDATA%\ForeverScout\uploads.log, en rad per anrop:
/// tid (UTC) · typ · storlek (byte före gzip) · sha256 · svar. Inga sökvägar eller kontonamn.
/// </summary>
public static class UploadLog
{
    public static string PathFor => Path.Combine(SafeFs.AppDataDir, "uploads.log");
    private static readonly object Lock = new();
    private const long MaxBytes = 1024 * 1024; // roteras till uploads.log.1 vid 1 MB

    public static void Append(string kind, long bytes, string sha256, string response)
    {
        var line = string.Join('\t',
            DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            kind, bytes.ToString(CultureInfo.InvariantCulture), sha256, response.Replace('\t', ' ').Replace('\n', ' ')) + Environment.NewLine;
        lock (Lock)
        {
            try
            {
                if (SafeFs.FileExists(PathFor) && SafeFs.Stat(PathFor) is { } st && st.Length > MaxBytes)
                {
                    SafeFs.DeleteFile(PathFor + ".1");
                    SafeFs.MoveFile(PathFor, PathFor + ".1");
                }
                if (!SafeFs.FileExists(PathFor))
                    SafeFs.AppendText(PathFor, "# Forever Scout uploads.log – time(UTC)\ttype\tbytes(before gzip)\tsha256\tresponse" + Environment.NewLine);
                SafeFs.AppendText(PathFor, line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SafeFsException) { }
        }
    }
}

/// <summary>"Visa vad som skickas": exakt vad nästa (eller senaste) uppladdning innehåller, läsbart.</summary>
public static class UploadPreview
{
    public static string Build(ApiClient api, GameDir? dir, Settings settings)
    {
        var sv = Strings.IsSv;
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append("\r\n");
        Line(sv ? "Forever Scout skickar bara det här – inget annat om din dator, ditt konto eller dina filer."
                : "Forever Scout sends only this – nothing else about your computer, your account or your files.");
        var scheme = api.BaseUri.Scheme.ToUpperInvariant();
        Line(sv ? $"Mottagare: {api.BaseUri.Host} ({scheme})." : $"Recipient: {api.BaseUri.Host} ({scheme}).");
        Line(sv ? $"Rubriker: User-Agent: {ApiClient.UserAgent} · Authorization: Bearer <din nyckel> · Content-Type · Content-Encoding: gzip"
                : $"Headers: User-Agent: {ApiClient.UserAgent} · Authorization: Bearer <your key> · Content-Type · Content-Encoding: gzip");
        Line();
        if (dir is null) { Line(Strings.S("game.none")); return sb.ToString(); }

        var files = WowLocator.SavedVariableFiles(dir);
        if (files.Count == 0) Line(sv ? "Ingen ForeverScout.lua hittades än – spela en stund (eller skriv /scout sync)." : "No ForeverScout.lua found yet – play for a while (or type /scout sync).");
        int i = 0;
        foreach (var f in files)
        {
            i++;
            byte[] bytes;
            try { bytes = SafeFs.ReadAllBytes(f); }
            catch (Exception ex) when (ex is IOException or SafeFsException or ScoutException) { Line(Strings.Error(ex)); continue; }
            var sha = ApiClient.Sha256Hex(bytes);
            var key = Privacy.FileKey(f);
            bool same = settings.UploadedSha.TryGetValue(key, out var prev) && prev == sha;
            var peek = Uploader.Peek(bytes);
            Line(new string('=', 78));
            Line(sv ? $"Fil {i} (konto {i} – kontots namn skickas inte): POST {api.BaseUri.Host}/api/bidrag/ladda-upp"
                    : $"File {i} (account {i} – the account name is not sent): POST {api.BaseUri.Host}/api/bidrag/ladda-upp");
            Line(sv ? $"Status: {(same ? "senast skickat (oförändrat sedan dess – skickas inte igen)" : "skickas vid nästa uppladdning")}"
                    : $"Status: {(same ? "last sent (unchanged since – will not be sent again)" : "will be sent at the next upload")}");
            Line(sv ? $"Kontroll: {(peek.Complete ? (peek.OnlyOurs ? "OK – bara ForeverScoutDB" : "VÄGRAS – innehåller annat än ForeverScoutDB") : "ofullständig – väntar på att spelet skrivit klart")}"
                    : $"Check: {(peek.Complete ? (peek.OnlyOurs ? "OK – ForeverScoutDB only" : "REFUSED – contains more than ForeverScoutDB") : "incomplete – waiting for the game to finish writing")}");
            Line(sv ? $"Storlek {bytes.Length:N0} byte (före gzip), sha256 {sha}" : $"Size {bytes.Length:N0} bytes (before gzip), sha256 {sha}");
            Line(new string('-', 78));
            Line(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Replace("\n", "\r\n")); // exakt innehåll; radslut bara för visning
        }

        if (settings.UploadHotfix)
            foreach (var f in WowLocator.HotfixFiles(dir))
            {
                try
                {
                    var bytes = SafeFs.ReadAllBytes(f);
                    var sha = ApiClient.Sha256Hex(bytes);
                    bool same = settings.HotfixSha.TryGetValue(Privacy.FileKey(f), out var prev) && prev == sha;
                    Line(new string('=', 78));
                    Line(sv ? $"Hotfix-cache (binär, spelets egen fil): POST {api.BaseUri.Host}/api/bidrag/hotfix – {bytes.Length:N0} byte, sha256 {sha} – {(same ? "redan skickad" : "skickas")}"
                            : $"Hotfix cache (binary, the game's own file): POST {api.BaseUri.Host}/api/bidrag/hotfix – {bytes.Length:N0} bytes, sha256 {sha} – {(same ? "already sent" : "will be sent")}");
                }
                catch (Exception ex) when (ex is IOException or SafeFsException or ScoutException) { Line(Strings.Error(ex)); }
            }

        Line(new string('=', 78));
        if (!settings.ShareAuctionPrices)
            Line(sv ? "Auktionspriser: delas inte (Inställningar → \"Dela auktionspriser\" är av). Auctionator.lua och Auc-Stat-Simple.lua läses inte."
                    : "Auction prices: not shared (Settings → \"Share auction prices\" is off). Auctionator.lua and Auc-Stat-Simple.lua are not read.");
        else
        {
            var today = AuctionData.Today(TimeZoneInfo.Local);
            var found = WowLocator.AuctionFiles(dir);
            if (found.Count == 0) Line(sv ? "Auktionspriser: ingen Auctionator.lua eller Auc-Stat-Simple.lua hittades." : "Auction prices: no Auctionator.lua or Auc-Stat-Simple.lua found.");
            foreach (var (f, reader) in found)
            {
                try
                {
                    var bytes = SafeFs.ReadAllBytes(f);
                    var key = Privacy.FileKey(f);
                    var read = reader.Read(bytes, SafeFs.Stat(f)?.LastWriteUtc ?? DateTime.UtcNow, TimeZoneInfo.Local);
                    DateOnly? mark = settings.AuctionDay.TryGetValue(key, out var w) && DateOnly.TryParse(w, CultureInfo.InvariantCulture, out var wd) ? wd : null;
                    var rows = AuctionData.Select(read.Rows, mark, today);
                    Line(sv ? $"Auktionspriser ur {reader.FileName}: POST {api.BaseUri.Host}/api/bidrag/auktioner – {rows.Count:N0} rader skickas (bara föremål, realm, fraktion, dag, lägsta pris per styck i koppar, antal). Filen själv skickas inte."
                            : $"Auction prices from {reader.FileName}: POST {api.BaseUri.Host}/api/bidrag/auktioner – {rows.Count:N0} rows will be sent (only item, realm, faction, day, lowest price per unit in copper, quantity). The file itself is not sent.");
                    if (!read.Complete) Line(sv ? "Filen är ofullständig – väntar på att spelet skrivit klart." : "The file is incomplete – waiting for the game to finish writing.");
                    var sample = AuctionData.Payload(reader.Source, rows.Take(20).ToList());
                    Line(Encoding.UTF8.GetString(sample) + (rows.Count > 20 ? (sv ? $"  (+ {rows.Count - 20:N0} rader till)" : $"  (+ {rows.Count - 20:N0} more rows)") : ""));
                }
                catch (Exception ex) when (ex is IOException or SafeFsException or ScoutException) { Line(Strings.Error(ex)); }
            }
        }
        return sb.ToString();
    }
}
