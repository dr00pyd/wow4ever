using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using ForeverScout.Core;

namespace ForeverScout.Tests;

/// <summary>
/// "Dela auktionspriser": CBOR, Auctionator-/Auctioneer-läsarna mot client/fixtures, urval med vattenmärke, exakt
/// JSON-kropp (inga namn), SafeFs-regeln (bara när delningen är på) och uppladdningen hela vägen.
/// </summary>
public sealed class AuctionTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 11, 6);
    private readonly DirectoryInfo _tmp = TestEnv.Temp("fs-auction-");

    public void Dispose()
    {
        SafeFs.AuctionFilesAllowed = false;
        SafeFs.SetGameDir(null);
        try { _tmp.Delete(true); } catch (IOException) { }
    }

    private static string Fixture(string name) =>
        ScoutStatsTests.FixtureFiles().Single(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ CBOR

    [Fact]
    public void CborDecodesTheTypesLuaUses()
    {
        // {"a": 1, "b": -2, "c": "x", "d": [true, 1.5], "e": 500000, "f": {}} med half/single/double
        byte[] data =
        [
            0xA7,
            0x61, (byte)'a', 0x01,
            0x61, (byte)'b', 0x21,
            0x61, (byte)'c', 0x61, (byte)'x',
            0x61, (byte)'d', 0x82, 0xF5, 0xF9, 0x3E, 0x00,
            0x61, (byte)'e', 0x1A, 0x00, 0x07, 0xA1, 0x20,
            0x61, (byte)'f', 0x80,
            0x61, (byte)'g', 0xFA, 0x42, 0x28, 0x00, 0x00,
        ];
        var t = Assert.IsType<LuaTable>(Cbor.Decode(data));
        Assert.Equal(1L, t["a"]);
        Assert.Equal(-2L, t["b"]);
        Assert.Equal("x", t["c"]);
        var d = t.Table("d")!;
        Assert.Equal(true, d[1L]);
        Assert.Equal(1.5, d[2L]);
        Assert.Equal(500_000L, t["e"]);
        Assert.Equal(0, t.Table("f")!.Count);
        Assert.Equal(42.0, t["g"]);
    }

    [Fact]
    public void CborIndefiniteLengthsAndDoubles()
    {
        // {_ "250": 42.0, "k": (_ "z", "!") }
        byte[] data = [0xBF, 0x63, (byte)'2', (byte)'5', (byte)'0', 0xFB, 0x40, 0x45, 0, 0, 0, 0, 0, 0, 0x61, (byte)'k', 0x7F, 0x61, (byte)'z', 0x61, (byte)'!', 0xFF, 0xFF];
        var t = Assert.IsType<LuaTable>(Cbor.Decode(data));
        Assert.Equal(42.0, t["250"]);
        Assert.Equal("z!", t["k"]);
    }

    [Theory]
    [InlineData(new byte[] { 0xA1, 0x61 })]                  // avklippt
    [InlineData(new byte[] { 0x1C })]                        // ogiltig additional info
    [InlineData(new byte[] { 0x01, 0x02 })]                  // skräp efter värdet
    [InlineData(new byte[] { 0x7B, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })] // jättesträng
    public void CborRejectsBrokenData(byte[] data) => Assert.Throws<CborException>(() => Cbor.Decode(data));

    [Fact]
    public void CborDepthIsCapped()
    {
        var data = Enumerable.Repeat((byte)0x81, Cbor.MaxDepth + 5).Append((byte)0x01).ToArray();
        Assert.Throws<CborException>(() => Cbor.Decode(data));
    }

    // ------------------------------------------------------------------ Auctionator

    private static List<AuctionRow> ReadAuctionator() =>
        new AuctionatorReader().Read(File.ReadAllBytes(Fixture("Auctionator.lua")), DateTime.UtcNow, TimeZoneInfo.Utc).Rows;

    [Fact]
    public void AuctionatorFixtureGivesDailyMinPerUnitAndQuantity()
    {
        var res = new AuctionatorReader().Read(File.ReadAllBytes(Fixture("Auctionator.lua")), DateTime.UtcNow, TimeZoneInfo.Utc);
        Assert.True(res.Complete);
        Assert.Null(res.Problem);
        var rows = res.Rows;
        AuctionRow R(int id, string faction, int day) => rows.Single(r => r.ItemId == id && r.Faction == faction && r.Day == AuctionatorReader.Day0.AddDays(day));

        Assert.All(rows, r => Assert.Equal("Forever", r.Realm));
        // l[dag] om den finns, annars h[dag]; a[dag] = antal
        Assert.Equal((38L, (int?)812), (R(2589, "alliance", 2497).MinPrice, R(2589, "alliance", 2497).Qty));
        Assert.Equal((41L, (int?)640), (R(2589, "alliance", 2498).MinPrice, R(2589, "alliance", 2498).Qty));
        Assert.Equal(35L, R(2589, "alliance", 2499).MinPrice);
        Assert.Equal(new DateOnly(2026, 11, 6), R(2589, "alliance", 2501).Day);
        Assert.Null(R(2770, "alliance", 2501).Qty);              // gammal databas utan "a"
        Assert.Equal(70L, R(2770, "alliance", 2501).MinPrice);
        Assert.Equal(19_500L, R(15210, "alliance", 2500).MinPrice); // grundnyckeln, inte "gr:15210:of the Bear"
        Assert.DoesNotContain(rows, r => r.MinPrice == 52_000);    // varianten hoppas över
        Assert.DoesNotContain(rows, r => r.ItemId == 39);           // husdjur "p:39"
        // Horde-realmen ligger som CBOR-sträng (C_EncodingUtil.SerializeCBOR vid utloggning)
        Assert.Equal((48L, (int?)280), (R(2589, "horde", 2501).MinPrice, R(2589, "horde", 2501).Qty));
        Assert.Equal(65L, R(2770, "horde", 2501).MinPrice);
        Assert.Equal(0x220A80L, R(4306, "horde", 2501).MinPrice); // byte 0x22, 0x0A, 0x80 i den råa strängen
        Assert.Equal(17, rows.Count);
    }

    [Fact]
    public void DayNumbersFollowScanDay0()
    {
        Assert.Equal(2501, AuctionatorReader.DayNumber(new DateOnly(2026, 11, 6)));
        Assert.Equal(0, AuctionatorReader.DayNumber(new DateOnly(2020, 1, 1)));
    }

    [Theory]
    [InlineData("Forever Alliance", "Forever", "alliance")]
    [InlineData("Mirage Raceway Horde", "MirageRaceway", "horde")]
    [InlineData("Firemaw", "Firemaw", "all")]          // ModernAH: ingen fraktion i nyckeln
    [InlineData("PvE", "PvE", "all")]                  // regionala regeluppsättningar
    [InlineData("Zul'jin Neutral", "Zuljin", "neutral")]
    public void RealmKeys(string key, string realm, string faction) =>
        Assert.Equal((realm, faction), AuctionatorReader.SplitRealm(key));

    [Theory]
    [InlineData("<script> Alliance")]
    [InlineData("http://evil.example Horde")]
    [InlineData("AVeryVeryVeryLongRealmNameThatIsSilly Alliance")]
    public void UnreasonableRealmNamesAreDropped(string key) => Assert.Null(AuctionatorReader.SplitRealm(key).Realm);

    [Fact]
    public void UnknownDbVersionAndHalfWrittenFiles()
    {
        var r = new AuctionatorReader();
        var v9 = r.Read(Encoding.UTF8.GetBytes("AUCTIONATOR_PRICE_DATABASE = { [\"__dbversion\"] = 9, [\"X Alliance\"] = { [\"2589\"] = { h = { [\"2501\"] = 5 } } } }"), DateTime.UtcNow, TimeZoneInfo.Utc);
        Assert.True(v9.Complete);
        Assert.Empty(v9.Rows);
        Assert.Equal("unknown-version", v9.Problem);
        var half = File.ReadAllBytes(Fixture("Auctionator.lua"));
        Assert.False(r.Read(half[..(half.Length / 2)], DateTime.UtcNow, TimeZoneInfo.Utc).Complete);
        Assert.Equal("no-price-database", r.Read(Encoding.UTF8.GetBytes("AUCTIONATOR_CONFIG = {}"), DateTime.UtcNow, TimeZoneInfo.Utc).Problem);
    }

    [Fact]
    public void AbsurdValuesAreDroppedOnTheClient()
    {
        var lua = "AUCTIONATOR_PRICE_DATABASE = { __dbversion = 8, [\"X Horde\"] = { [\"1\"] = { h = { [\"2501\"] = 0 } }, [\"2\"] = { h = { [\"2501\"] = 99999999999 } }, [\"3\"] = { h = { [\"2501\"] = 7 }, a = { [\"2501\"] = -4 } }, [\"99999999\"] = { h = { [\"2501\"] = 7 } } } }";
        var rows = new AuctionatorReader().Read(Encoding.UTF8.GetBytes(lua), DateTime.UtcNow, TimeZoneInfo.Utc).Rows;
        var only = Assert.Single(rows);
        Assert.Equal((3, 7L, (int?)null), (only.ItemId, only.MinPrice, only.Qty));
    }

    // ------------------------------------------------------------------ Auctioneer

    [Fact]
    public void AuctioneerUsesTodaysWindowOnly()
    {
        var mtime = new DateTime(2026, 11, 6, 21, 30, 0, DateTimeKind.Utc); // push 17:00, sparad 21:30 samma dag
        var res = new AuctioneerReader().Read(File.ReadAllBytes(Fixture("Auc-Stat-Simple.lua")), mtime, TimeZoneInfo.Utc);
        Assert.True(res.Complete);
        Assert.All(res.Rows, r =>
        {
            Assert.Equal(("Forever", "alliance", Today), (r.Realm, r.Faction, r.Day));
            Assert.Null(r.Qty); // "sedda" räknar samma auktion en gång per skanning
        });
        var byItem = res.Rows.ToDictionary(r => r.ItemId, r => r.MinPrice);
        Assert.Equal(new Dictionary<int, long> { [2589] = 38, [2770] = 80, [15210] = 19_000 }, byItem); // egenskapen "0"; husdjuret "P39" hoppas över
        Assert.DoesNotContain(res.Rows, r => r.ItemId == 2592); // lägsta 0 = inget utköpspris
        Assert.DoesNotContain(res.Rows, r => r.Faction == "horde"); // fönstret spänner flera dagar

        // Sparad dagen efter pushen: dagen går inte att avgöra → inga rader.
        var later = new AuctioneerReader().Read(File.ReadAllBytes(Fixture("Auc-Stat-Simple.lua")), mtime.AddDays(1), TimeZoneInfo.Utc);
        Assert.Empty(later.Rows);
        Assert.Equal("auctioneer-window", later.Problem);
    }

    [Theory]
    [InlineData("0@1640;40;38;2", 38L)]
    [InlineData("0@41000;2;19000;2_-12@24000;1;24000", 19_000L)]
    [InlineData("-12@24000;1;24000_0@1;1;7", 7L)]
    [InlineData("0@0;0;0", null)]
    [InlineData("0@1;2", null)]
    [InlineData("garbage", null)]
    public void AuctioneerPackedStrings(string packed, long? min) => Assert.Equal(min, AuctioneerReader.BaseMinBuyout(packed));

    // ------------------------------------------------------------------ urval och kropp

    [Fact]
    public void SelectKeepsWatermarkDayAndDropsOldAndFutureDays()
    {
        var rows = ReadAuctionator();
        var all = AuctionData.Select(rows, null, Today);
        Assert.DoesNotContain(all, r => r.Day < Today.AddDays(-30)); // 2026-10-06 (Wool Cloth) är för gammal
        Assert.Contains(all, r => r.ItemId == 2592 && r.Day == Today);
        var fromWatermark = AuctionData.Select(rows, Today.AddDays(-1), Today);
        Assert.All(fromWatermark, r => Assert.True(r.Day >= Today.AddDays(-1)));
        Assert.Contains(fromWatermark, r => r.Day == Today.AddDays(-1)); // vattenmärkets dag skickas igen
        var future = AuctionData.Select([new AuctionRow(1, "X", "all", Today.AddDays(3), 5, null)], null, Today);
        Assert.Empty(future);
        // Dubbletter (samma föremål/realm/fraktion/dag) → lägsta priset
        var dup = AuctionData.Select([new AuctionRow(1, "X", "all", Today, 9, 1), new AuctionRow(1, "X", "all", Today, 5, 2)], null, Today);
        Assert.Equal(5L, Assert.Single(dup).MinPrice);
    }

    [Fact]
    public void PayloadContainsOnlyTheSixFields()
    {
        var json = Encoding.UTF8.GetString(AuctionData.Payload("auctionator", [new AuctionRow(2589, "Forever", "alliance", Today, 42, 655), new AuctionRow(2770, "Forever", "horde", Today, 65, null)]));
        Assert.Equal("""{"v":1,"source":"auctionator","rows":[[2589,"Forever","alliance","2026-11-06",42,655],[2770,"Forever","horde","2026-11-06",65,null]]}""", json);
        // Hela fixturens kropp: inga inköpslistor, inställningar, säljhistorik eller sökningar
        var all = Encoding.UTF8.GetString(AuctionData.Payload("auctionator", AuctionData.Select(ReadAuctionator(), null, Today)));
        foreach (var bad in new[] { "Tygfarm", "Linen", "Copper Ore", "price_history", "quantity", "time" }) Assert.DoesNotContain(bad, all);
    }

    // ------------------------------------------------------------------ SafeFs och uppladdning

    private (GameDir Dir, string Auctionator, string Auctioneer) Game()
    {
        var game = Path.Combine(_tmp.FullName, "World of Warcraft", "_forever_");
        var sv = Path.Combine(game, "WTF", "Account", "ACCOUNTNAME", "SavedVariables");
        Directory.CreateDirectory(sv);
        Directory.CreateDirectory(Path.Combine(game, "Interface"));
        File.Copy(Fixture("Auctionator.lua"), Path.Combine(sv, "Auctionator.lua"));
        File.Copy(Fixture("Auc-Stat-Simple.lua"), Path.Combine(sv, "Auc-Stat-Simple.lua"));
        File.SetLastWriteTimeUtc(Path.Combine(sv, "Auc-Stat-Simple.lua"), new DateTime(2026, 11, 6, 21, 30, 0, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(sv, "Auctioneer.lua"), "AucAdvancedData = { secret = 1 }");
        File.WriteAllText(Path.Combine(sv, "Auc-ScanData.lua"), "AucScanData = { seller = \"Someone\" }");
        var real = SafeFs.SetGameDir(game)!;
        var svReal = Path.Combine(real, "WTF", "Account", "ACCOUNTNAME", "SavedVariables");
        return (new GameDir(real, "_forever_"), Path.Combine(svReal, "Auctionator.lua"), Path.Combine(svReal, "Auc-Stat-Simple.lua"));
    }

    [Fact]
    public void AuctionFilesAreReadableOnlyWhenSharingIsOn()
    {
        var (dir, auctionator, auctioneer) = Game();
        SafeFs.AuctionFilesAllowed = false;
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(auctionator));
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(auctioneer));
        Assert.Empty(WowLocator.AuctionFiles(dir));

        SafeFs.AuctionFilesAllowed = true;
        Assert.NotEmpty(SafeFs.ReadAllBytes(auctionator));
        Assert.NotEmpty(SafeFs.ReadAllBytes(auctioneer));
        Assert.Equal(2, WowLocator.AuctionFiles(dir).Count);
        // Auctioneers övriga filer (Auc-ScanData med säljarnamn, AucAdvancedData) nekas alltid.
        var sv = Path.GetDirectoryName(auctionator)!;
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(Path.Combine(sv, "Auc-ScanData.lua")));
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(Path.Combine(sv, "Auctioneer.lua")));
        Assert.Throws<SafeFsException>(() => SafeFs.WriteAllText(auctionator, "x"));
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public List<(string Path, byte[] Body)> Calls { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content is null ? [] : await req.Content.ReadAsByteArrayAsync(ct);
            var path = req.RequestUri!.AbsolutePath;
            lock (Calls) Calls.Add((path, body));
            var json = path.EndsWith("auktioner") ? """{"accepted":3,"skipped":0}""" : """{"accepted":{},"newConfirmed":[],"points":1,"rank":1,"lastSeq":1}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static string Gunzip(byte[] gz)
    {
        using var s = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static (Uploader Up, FakeServer Srv, Settings Set) NewUploader(bool share)
    {
        var srv = new FakeServer();
        var api = new ApiClient("https://guide.wow4ever.se", srv) { Key = "k" };
        var set = Settings.Load();
        set.UploadHotfix = false;
        set.ShareAuctionPrices = share;
        set.AuctionSha.Clear(); set.AuctionDay.Clear(); set.UploadedSha.Clear(); set.UploadedSeq.Clear();
        set.RetryAttempts = 0; set.NextRetryUtc = null;
        SafeFs.AuctionFilesAllowed = share;
        return (new Uploader(api, set) { AuctionTimeZone = TimeZoneInfo.Utc, AuctionToday = () => Today }, srv, set);
    }

    [Fact]
    public void SharingIsOffByDefault() => Assert.False(new Settings().ShareAuctionPrices);

    [Fact]
    public async Task NothingIsReadOrSentWhenSharingIsOff()
    {
        var (dir, _, _) = Game();
        var (up, srv, _) = NewUploader(share: false);
        var report = await up.SyncAsync(dir, force: false);
        Assert.Null(report.Error);
        Assert.DoesNotContain(srv.Calls, c => c.Path.EndsWith("auktioner"));
    }

    [Fact]
    public async Task SharingSendsOnlyRowsAndThenOnlyNewDays()
    {
        var (dir, auctionator, _) = Game();
        var (up, srv, set) = NewUploader(share: true);
        var report = await up.SyncAsync(dir, force: false);
        Assert.Null(report.Error);
        var calls = srv.Calls.Where(c => c.Path.EndsWith("auktioner")).ToList();
        Assert.Equal(2, calls.Count); // Auctionator + Auctioneer
        Assert.Equal(6, report.AuctionRows);
        var bodies = calls.Select(c => Gunzip(c.Body)).ToList();
        var auctionatorBody = bodies.Single(b => b.Contains("\"auctionator\""));
        using (var doc = JsonDocument.Parse(auctionatorBody))
        {
            Assert.Equal(new[] { "rows", "source", "v" }, doc.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.All(doc.RootElement.GetProperty("rows").EnumerateArray(), r => Assert.Equal(6, r.GetArrayLength()));
            Assert.Equal(16, doc.RootElement.GetProperty("rows").GetArrayLength()); // 17 rader minus Wool Cloth 2026-10-06
        }
        Assert.DoesNotContain("ACCOUNTNAME", string.Join("", bodies));
        Assert.Equal("2026-11-06", set.AuctionDay[Privacy.FileKey(auctionator)]);

        // Oförändrad fil → inget skickas
        srv.Calls.Clear();
        await up.SyncAsync(dir, force: false);
        Assert.DoesNotContain(srv.Calls, c => c.Path.EndsWith("auktioner"));

        // Filen ändras (spelet sparar igen) → bara dagar från vattenmärket (2026-11-06) skickas
        var text = File.ReadAllText(auctionator, Encoding.Latin1).Replace("[\"m\"] = 42,", "[\"m\"] = 41,");
        File.WriteAllText(auctionator, text, Encoding.Latin1);
        srv.Calls.Clear();
        await up.SyncAsync(dir, force: false);
        var again = srv.Calls.Single(c => c.Path.EndsWith("auktioner"));
        using var d2 = JsonDocument.Parse(Gunzip(again.Body));
        Assert.All(d2.RootElement.GetProperty("rows").EnumerateArray(), r => Assert.Equal("2026-11-06", r[3].GetString()));
    }
}
