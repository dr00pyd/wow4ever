using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ForeverScout.Core;

/// <summary>
/// Ett auktionspris som laddas upp: föremål, realm + fraktion, dag, lägsta utköpspris per styck (koppar) och antal
/// till salu (null om addonen inte vet det). Inget annat – inga säljarnamn, inga karaktärer, inga klockslag.
/// </summary>
public sealed record AuctionRow(int ItemId, string Realm, string Faction, DateOnly Day, long MinPrice, int? Qty);

/// <param name="Complete">Gick filen att parsa helt? (annars skriver spelet kanske fortfarande – försök igen senare)</param>
/// <param name="Problem">Varför filen inte gav några rader (t.ex. "unknown-version"), för loggen. Null = inget fel.</param>
public sealed record AuctionReadResult(bool Complete, List<AuctionRow> Rows, string? Problem = null);

/// <summary>Läser en auktionsaddons SavedVariables och plockar ut <see cref="AuctionRow"/> – bara de fälten.</summary>
public interface IAuctionReader
{
    /// <summary>"auctionator" eller "auctioneer" (skickas till servern).</summary>
    string Source { get; }
    /// <summary>Filnamnet under <c>WTF\Account\*\SavedVariables</c> (SafeFs tillåter bara just dessa, och bara när delningen är på).</summary>
    string FileName { get; }
    /// <param name="lastWriteUtc">Filens ändringstid (Auctioneer behöver den för att veta vilken dag priserna gäller).</param>
    /// <param name="tz">Spelarens tidszon (addonerna räknar dagar i lokal tid).</param>
    AuctionReadResult Read(byte[] bytes, DateTime lastWriteUtc, TimeZoneInfo tz);
}

/// <summary>Gemensamt: gränser, realm/fraktion, urval av dagar och JSON-kroppen som skickas.</summary>
public static class AuctionData
{
    public static readonly IAuctionReader[] Readers = [new AuctionatorReader(), new AuctioneerReader()];

    /// <summary>Högsta rimliga pris per styck: 50 000 guld (servern har samma tak).</summary>
    public const long MaxPrice = 50_000L * 100 * 100;
    public const int MaxQty = 1_000_000;
    public const int MaxItemId = 9_999_999;
    /// <summary>Dagar bakåt som skickas (servern godtar 35).</summary>
    public const int MaxAgeDays = 30;
    /// <summary>Rader per anrop (servern godtar 50 000).</summary>
    public const int ChunkRows = 20_000;

    /// <summary>
    /// Realmnamn → kompakt form utan mellanslag, bindestreck och apostrofer ("Mirage Raceway" → "MirageRaceway",
    /// som Auctioneer skriver det), så att båda addonernas data hamnar på samma realm. Null om namnet inte är rimligt.
    /// </summary>
    public static string? CompactRealm(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new StringBuilder();
        foreach (var ch in raw.Normalize(NormalizationForm.FormC))
        {
            if (char.IsWhiteSpace(ch) || ch is '-' or '\'' or '’') continue;
            if (!char.IsLetterOrDigit(ch)) return null;
            sb.Append(ch);
        }
        return sb.Length is >= 1 and <= 32 ? sb.ToString() : null;
    }

    public static readonly string[] Factions = ["alliance", "horde", "neutral"];

    /// <summary>"Alliance"/"Horde"/"Neutral" → gemener, annars null.</summary>
    public static string? Faction(string? raw) => raw?.ToLowerInvariant() is { } f && Factions.Contains(f) ? f : null;

    /// <summary>Talvärde ur en Lua/CBOR-tabell som positivt heltal inom gränsen, annars null.</summary>
    internal static long? PositiveInt(object? v, long max) => v switch
    {
        long l when l > 0 && l <= max => l,
        double d when d >= 1 && d <= max && !double.IsNaN(d) => (long)Math.Ceiling(d),
        string s when long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p > 0 && p <= max => p,
        _ => null,
    };

    /// <summary>Föremåls-id ur en nyckel som bara är siffror ("2589" eller 2589). Varianter ("g:", "gr:", "p:") → null.</summary>
    internal static int? ItemId(object key) => key switch
    {
        long l when l > 0 && l <= MaxItemId => (int)l,
        string s when s.Length is > 0 and <= 7 && s.All(char.IsAsciiDigit) && int.Parse(s, CultureInfo.InvariantCulture) is var i && i > 0 => i,
        _ => null,
    };

    /// <summary>
    /// Raderna som ska skickas: dagar från vattenmärket (samma dag igen – den kan ha fått fler skanningar) och högst
    /// <see cref="MaxAgeDays"/> bakåt, aldrig framtida dagar (högst i morgon, tidszoner). Sorterade, inga dubbletter.
    /// </summary>
    public static List<AuctionRow> Select(IEnumerable<AuctionRow> rows, DateOnly? watermark, DateOnly today)
    {
        var from = today.AddDays(-MaxAgeDays);
        if (watermark is { } w && w > from) from = w;
        var to = today.AddDays(1);
        var best = new Dictionary<(int, string, string, DateOnly), AuctionRow>();
        foreach (var r in rows)
        {
            if (r.Day < from || r.Day > to) continue;
            var k = (r.ItemId, r.Realm, r.Faction, r.Day);
            if (!best.TryGetValue(k, out var prev) || r.MinPrice < prev.MinPrice) best[k] = r;
        }
        return best.Values
            .OrderBy(r => r.Day).ThenBy(r => r.Realm, StringComparer.Ordinal).ThenBy(r => r.Faction, StringComparer.Ordinal).ThenBy(r => r.ItemId)
            .ToList();
    }

    /// <summary>
    /// Kroppen till POST /api/bidrag/auktioner: <c>{"v":1,"source":"auctionator","rows":[[id,"Realm","faction","2026-11-05",pris,antal|null],…]}</c>.
    /// Exakt de här fälten – det är allt som lämnar datorn.
    /// </summary>
    public static byte[] Payload(string source, IReadOnlyList<AuctionRow> rows)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("source", source);
            w.WriteStartArray("rows");
            foreach (var r in rows)
            {
                w.WriteStartArray();
                w.WriteNumberValue(r.ItemId);
                w.WriteStringValue(r.Realm);
                w.WriteStringValue(r.Faction);
                w.WriteStringValue(r.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                w.WriteNumberValue(r.MinPrice);
                if (r.Qty is int q) w.WriteNumberValue(q); else w.WriteNullValue();
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>I dag i spelarens tidszon.</summary>
    public static DateOnly Today(TimeZoneInfo tz) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));
}

/// <summary>
/// Auctionator (plusmouse, github.com/Auctionator/Auctionator; läst i v341). Filen
/// <c>WTF\Account\&lt;konto&gt;\SavedVariables\Auctionator.lua</c>, globalen <c>AUCTIONATOR_PRICE_DATABASE</c>:
/// <code>
/// AUCTIONATOR_PRICE_DATABASE = {
///   ["__dbversion"] = 8,
///   ["Realm Alliance"] = {                 -- Classic/LegacyAH: "&lt;realm&gt; &lt;fraktion&gt;"; ModernAH: realm (ingen fraktion)
///     ["2589"] = { m = 41, h = { ["2501"] = 45 }, l = { ["2501"] = 38 }, a = { ["2501"] = 812 } },
///     ["gr:15210:of the Bear"] = { … },    -- varianter (hoppas över – nyckeln "15210" har minimum för alla)
///     version = 2,
///   },
///   ["Annan realm"] = "&lt;CBOR&gt;",            -- realms serialiseras med C_EncodingUtil.SerializeCBOR vid utloggning
/// }
/// </code>
/// Dag = floor((time() − SCAN_DAY_0) / 86400), SCAN_DAY_0 = 2020-01-01 00:00 lokal tid. Dagens lägsta pris per styck
/// (koppar) = <c>l[dag]</c> om det finns, annars <c>h[dag]</c>; antal till salu = <c>a[dag]</c> (saknas i gamla databaser).
/// Allt annat i filen (inställningar, inköpslistor, säljhistorik) läses aldrig vidare.
/// </summary>
public sealed class AuctionatorReader : IAuctionReader
{
    public string Source => "auctionator";
    public string FileName => "Auctionator.lua";

    public static readonly DateOnly Day0 = new(2020, 1, 1);
    public const int MaxRealms = 32;
    public const int MaxItemsPerRealm = 200_000;

    public AuctionReadResult Read(byte[] bytes, DateTime lastWriteUtc, TimeZoneInfo tz)
    {
        Dictionary<string, object?> g;
        try { g = LuaParser.ParseGlobalsRaw(bytes); }
        catch (LuaParseException) { return new(false, [], "incomplete"); }
        if (!g.TryGetValue("AUCTIONATOR_PRICE_DATABASE", out var dbv) || dbv is not LuaTable db) return new(true, [], "no-price-database");
        var version = db.Int("__dbversion");
        if (version is not (6 or 7 or 8)) return new(true, [], "unknown-version");

        var rows = new List<AuctionRow>();
        int realms = 0;
        foreach (var (key, value) in db.Pairs)
        {
            if (key is not string realmKey || realmKey == "__dbversion") continue;
            if (++realms > MaxRealms) break;
            var (realm, faction) = SplitRealm(Utf8(realmKey));
            if (realm is null) continue;
            var table = value switch
            {
                LuaTable t => t,
                string s => DecodeCbor(s),
                _ => null,
            };
            if (table is null) continue;
            int items = 0;
            foreach (var (itemKey, entry) in table.Pairs)
            {
                if (AuctionData.ItemId(itemKey) is not int id) continue; // "version", "g:…", "gr:…", "p:…"
                if (++items > MaxItemsPerRealm) break;
                var e = entry switch { LuaTable t => t, string s => DecodeCbor(s), _ => null }; // äldre format: CBOR per föremål
                if (e is null) continue;
                AddEntry(rows, id, realm, faction, e);
            }
        }
        return new(true, rows);
    }

    /// <summary>"Firemaw Alliance" → ("Firemaw", "alliance"); "Firemaw" (ModernAH, ingen fraktion) → ("Firemaw", "all").</summary>
    public static (string? Realm, string Faction) SplitRealm(string key)
    {
        var sp = key.LastIndexOf(' ');
        if (sp > 0 && AuctionData.Faction(key[(sp + 1)..]) is { } f) return (AuctionData.CompactRealm(key[..sp]), f);
        return (AuctionData.CompactRealm(key), "all");
    }

    private static void AddEntry(List<AuctionRow> rows, int id, string realm, string faction, LuaTable e)
    {
        var h = Days(e.Table("h"));
        var l = Days(e.Table("l"));
        var a = Days(e.Table("a"));
        foreach (var day in h.Keys.Union(l.Keys))
        {
            var min = AuctionData.PositiveInt(l.GetValueOrDefault(day), AuctionData.MaxPrice)
                      ?? AuctionData.PositiveInt(h.GetValueOrDefault(day), AuctionData.MaxPrice);
            if (min is not long price) continue;
            int? qty = a.GetValueOrDefault(day) switch
            {
                long q when q >= 0 && q <= AuctionData.MaxQty => (int)q,
                double q when q >= 0 && q <= AuctionData.MaxQty => (int)q,
                _ => null,
            };
            rows.Add(new AuctionRow(id, realm, faction, Day0.AddDays(day), price, qty));
        }
    }

    /// <summary>Dagtabell (nycklar "2501" eller 2501) → dagnummer → värde.</summary>
    private static Dictionary<int, object> Days(LuaTable? t)
    {
        var d = new Dictionary<int, object>();
        if (t is null) return d;
        foreach (var (k, v) in t.Pairs)
        {
            int? day = k switch
            {
                long l when l is >= 0 and <= 30_000 => (int)l,
                string s when s.Length is > 0 and <= 5 && s.All(char.IsAsciiDigit) => int.Parse(s, CultureInfo.InvariantCulture),
                _ => null,
            };
            if (day is int n && n <= 30_000) d[n] = v;
        }
        return d;
    }

    /// <summary>Dagnummer för ett datum (för tester och vattenmärket).</summary>
    public static int DayNumber(DateOnly day) => day.DayNumber - Day0.DayNumber;

    private static LuaTable? DecodeCbor(string raw)
    {
        try { return Cbor.Decode(Encoding.Latin1.GetBytes(raw)) as LuaTable; }
        catch (CborException) { return null; }
        catch (ArgumentException) { return null; } // tecken över 0xFF – inte råa byte
    }

    /// <summary>Realmnamnet är UTF-8-byte i råläget (Latin-1-tecken) – avkoda.</summary>
    private static string Utf8(string raw)
    {
        foreach (var ch in raw) if (ch > 0x7F) return Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(raw));
        return raw;
    }
}

/// <summary>
/// Auctioneer (Norganna; Classic Era-versionen 4.4.x, modulen Auc-Stat-Simple). Filen
/// <c>WTF\Account\&lt;konto&gt;\SavedVariables\Auc-Stat-Simple.lua</c>, globalen <c>AucAdvancedStatSimpleData</c>:
/// <code>
/// AucAdvancedStatSimpleData = { Version = 3, RealmData = {
///   ["Firemaw_Alliance"] = {                -- kompakt realm + "_" + Alliance/Horde/Neutral
///     dailypush = 1762462800,               -- nästa "push" (= senaste push + 16 h)
///     daily = { ["2589"] = "0@totalt;sedda;lägsta;auktioner_-12@…" },
///     means = { … },                         -- glidande medelvärden (används inte)
/// } } }
/// </code>
/// <c>daily</c> samlar alla skanningar sedan senaste push (<c>dailypush − 57600</c>) och nollställs vid nästa
/// inloggning efter 16 h. Vi använder bara egenskapen "0" (föremålet oavsett suffix) och dess lägsta utköpspris per
/// styck. Dagen tas bara om fönstret (från push till filens ändringstid) ligger inom ett och samma lokala datum – annars
/// går det inte att säga vilken dag priset gäller och realmen hoppas över. Antal skickas inte: "sedda" räknar samma
/// auktion en gång per skanning. Auc-ScanData (rå skanning med säljarnamn) läses aldrig.
/// </summary>
public sealed class AuctioneerReader : IAuctionReader
{
    public string Source => "auctioneer";
    public string FileName => "Auc-Stat-Simple.lua";

    /// <summary>StatSimple.lua: PUSHTIME = 60 * 60 * 16.</summary>
    public const long PushTime = 57_600;

    public AuctionReadResult Read(byte[] bytes, DateTime lastWriteUtc, TimeZoneInfo tz)
    {
        Dictionary<string, object?> g;
        try { g = LuaParser.ParseGlobals(bytes); }
        catch (LuaParseException) { return new(false, [], "incomplete"); }
        if (!g.TryGetValue("AucAdvancedStatSimpleData", out var dv) || dv is not LuaTable data) return new(true, [], "no-price-database");
        if (data.Num("Version") != 3) return new(true, [], "unknown-version");
        if (data.Table("RealmData") is not { } realms) return new(true, [], "no-price-database");

        var rows = new List<AuctionRow>();
        string? problem = null;
        var end = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(lastWriteUtc, DateTimeKind.Utc), tz);
        int n = 0;
        foreach (var (key, value) in realms.Pairs)
        {
            if (key is not string serverKey || value is not LuaTable rd) continue;
            if (++n > AuctionatorReader.MaxRealms) break;
            var us = serverKey.LastIndexOf('_');
            if (us <= 0) continue;
            var realm = AuctionData.CompactRealm(serverKey[..us]);
            var faction = AuctionData.Faction(serverKey[(us + 1)..]);
            if (realm is null || faction is null) continue;
            if (rd.Num("dailypush") is not double push || rd.Table("daily") is not { } daily || daily.Count == 0) continue;
            var startUtc = DateTime.UnixEpoch.AddSeconds(push - PushTime);
            var start = TimeZoneInfo.ConvertTimeFromUtc(startUtc, tz);
            if (start > end.AddHours(1) || start.Date != end.Date) { problem = "auctioneer-window"; continue; }
            var day = DateOnly.FromDateTime(end);
            int items = 0;
            foreach (var (storeId, s) in daily.Pairs)
            {
                if (AuctionData.ItemId(storeId) is not int id || s is not string packed) continue; // "P…" = husdjur
                if (++items > AuctionatorReader.MaxItemsPerRealm) break;
                if (BaseMinBuyout(packed) is long price) rows.Add(new AuctionRow(id, realm, faction, day, price, null));
            }
        }
        return new(true, rows, rows.Count == 0 ? problem : null);
    }

    /// <summary>"0@totalt;sedda;lägsta[;auktioner]_…" → lägsta (egenskapen "0"), annars null.</summary>
    public static long? BaseMinBuyout(string packed)
    {
        foreach (var part in packed.Split('_'))
        {
            var at = part.IndexOf('@');
            if (at < 0 || part[..at] != "0") continue;
            var f = part[(at + 1)..].Split(';');
            if (f.Length < 3) return null;
            return double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? AuctionData.PositiveInt(v, AuctionData.MaxPrice)
                : null;
        }
        return null;
    }
}
