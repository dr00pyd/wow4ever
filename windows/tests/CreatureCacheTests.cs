using System.IO.Compression;
using System.Net;
using System.Text;
using ForeverScout.Core;

namespace ForeverScout.Tests;

/// <summary>
/// 1.0.14: spelets varelsecache (Cache\WDB\&lt;locale&gt;\creaturecache.wdb) – hittas och läses som hotfix-cachen men inga
/// andra filer i Cache\WDB, skickas bara när rutan är på (standard) och bara när filen ändrats (sha256), gzip till
/// /api/bidrag/varelser, och syns i "Visa vad som skickas".
/// </summary>
public sealed class CreatureCacheTests : IDisposable
{
    private readonly DirectoryInfo _tmp = TestEnv.Temp("fs-wdb-");

    public void Dispose()
    {
        SafeFs.SetGameDir(null);
        try { _tmp.Delete(true); } catch (IOException) { }
    }

    /// <summary>En liten fil i spelets format: huvud "BOMW" + en post + slutmärke (innehållet tolkas bara av servern).</summary>
    private static byte[] Wdb(byte variant = 1)
    {
        var ms = new MemoryStream();
        ms.Write("BOMW"u8);
        ms.Write(BitConverter.GetBytes(70938u));
        ms.Write("SUne"u8);
        ms.Write(BitConverter.GetBytes(688u));
        ms.Write(BitConverter.GetBytes(4u));
        ms.Write(BitConverter.GetBytes(0u));
        byte[] rec = [0, 0, 0, 0, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. "Eagle Owl"u8, 0, variant];
        ms.Write(BitConverter.GetBytes(7556u));
        ms.Write(BitConverter.GetBytes((uint)rec.Length));
        ms.Write(rec);
        ms.Write(new byte[8]);
        return ms.ToArray();
    }

    /// <summary>_forever_ med varelsecache (enUS + deDE), en annan WDB-fil och hotfix-cachen.</summary>
    private (GameDir Dir, string EnUs) Game()
    {
        var game = Path.Combine(_tmp.FullName, "World of Warcraft", "_forever_");
        Directory.CreateDirectory(Path.Combine(game, "Interface", "AddOns"));
        foreach (var loc in new[] { "enUS", "deDE" })
        {
            var d = Path.Combine(game, "Cache", "WDB", loc);
            Directory.CreateDirectory(d);
            File.WriteAllBytes(Path.Combine(d, "creaturecache.wdb"), Wdb());
            File.WriteAllBytes(Path.Combine(d, "itemcache.wdb"), [1, 2, 3]);
        }
        File.WriteAllBytes(Path.Combine(game, "Cache", "WDB", "creaturecache.wdb"), [1]); // fel nivå – läses aldrig
        Directory.CreateDirectory(Path.Combine(game, "Cache", "WDB", "frFR")); // tom locale-katalog
        var real = SafeFs.SetGameDir(game)!;
        return (new GameDir(real, "_forever_"), Path.Combine(real, "Cache", "WDB", "enUS", "creaturecache.wdb"));
    }

    [Fact]
    public void FindsOnlyCreatureCacheFilesUnderCacheWdb()
    {
        var (dir, enUs) = Game();
        var files = WowLocator.CreatureCacheFiles(dir);
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.Equal("creaturecache.wdb", Path.GetFileName(f)));
        Assert.Contains(enUs, files, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Wdb(), SafeFs.ReadAllBytes(enUs));
        // Övriga cachefiler, filer på fel nivå och skrivning nekas.
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(Path.Combine(dir.WdbDir, "enUS", "itemcache.wdb")));
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(Path.Combine(dir.WdbDir, "creaturecache.wdb")));
        Assert.Throws<SafeFsException>(() => SafeFs.WriteAllBytes(enUs, [0]));
        Assert.Throws<SafeFsException>(() => SafeFs.ReadAllBytes(Path.Combine(dir.Path, "Cache", "Other", "enUS", "creaturecache.wdb")));
    }

    [Fact]
    public void NoWdbFolderGivesEmptyList()
    {
        var game = Path.Combine(_tmp.FullName, "World of Warcraft", "_forever_");
        Directory.CreateDirectory(Path.Combine(game, "Interface"));
        var real = SafeFs.SetGameDir(game)!;
        Assert.Empty(WowLocator.CreatureCacheFiles(new GameDir(real, "_forever_")));
    }

    [Fact]
    public void OnByDefault()
    {
        var s = new Settings();
        Assert.True(s.UploadCreatureCache);
        Assert.Empty(s.CreatureCacheSha);
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public List<(string Path, byte[] Body, string? Encoding)> Calls { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content is null ? [] : await req.Content.ReadAsByteArrayAsync(ct);
            var path = req.RequestUri!.AbsolutePath;
            lock (Calls) Calls.Add((path, body, req.Content?.Headers.ContentEncoding.FirstOrDefault()));
            if (path.EndsWith("varelser") && Status != HttpStatusCode.OK)
                return new HttpResponseMessage(Status) { Content = new StringContent("""{"error":"x","code":"not-creaturecache"}""", Encoding.UTF8, "application/json") };
            var json = path.EndsWith("varelser") ? """{"stored":true,"creatures":1}""" : """{"stored":true}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
        public List<(string Path, byte[] Body, string? Encoding)> Wdb() => Calls.Where(c => c.Path == "/api/bidrag/varelser").ToList();
    }

    private static (Uploader Up, FakeServer Srv, Settings Set, ApiClient Api) NewUploader(bool on)
    {
        var srv = new FakeServer();
        var api = new ApiClient("https://guide.wow4ever.se", srv) { Key = "k" };
        var set = Settings.Load();
        set.UploadHotfix = false;
        set.ShareAuctionPrices = false;
        set.UploadCreatureCache = on;
        set.CreatureCacheSha.Clear(); set.UploadedSha.Clear(); set.UploadedSeq.Clear();
        set.RetryAttempts = 0; set.NextRetryUtc = null;
        return (new Uploader(api, set), srv, set, api);
    }

    private static byte[] Gunzip(byte[] gz)
    {
        using var s = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task SentOnceGzippedThenOnlyWhenChanged()
    {
        var (dir, enUs) = Game();
        var (up, srv, set, _) = NewUploader(on: true);
        var report = await up.SyncAsync(dir, force: false);
        Assert.Null(report.Error);
        var calls = srv.Wdb();
        Assert.Equal(2, calls.Count); // enUS + deDE
        Assert.All(calls, c => Assert.Equal("gzip", c.Encoding));
        Assert.All(calls, c => Assert.Equal(Wdb(), Gunzip(c.Body)));
        Assert.Equal(2, report.CreatureCacheStored);
        Assert.Equal(2, report.CreatureCount);
        Assert.False(report.NothingToDo);
        Assert.Equal(ApiClient.Sha256Hex(Wdb()), set.CreatureCacheSha[Privacy.FileKey(enUs)]);
        Assert.DoesNotContain(set.CreatureCacheSha.Keys, k => k.Contains("WDB", StringComparison.OrdinalIgnoreCase) && k.Contains('\\'));

        // Oförändrad → inget skickas (sha-dedup).
        srv.Calls.Clear();
        report = await up.SyncAsync(dir, force: false);
        Assert.Empty(srv.Wdb());
        Assert.Equal(0, report.CreatureCacheStored);

        // Spelet skriver om enUS-filen → bara den skickas.
        File.WriteAllBytes(enUs, Wdb(variant: 2));
        srv.Calls.Clear();
        await up.SyncAsync(dir, force: false);
        var again = Assert.Single(srv.Wdb());
        Assert.Equal(Wdb(variant: 2), Gunzip(again.Body));
    }

    [Fact]
    public async Task NothingIsSentWhenTurnedOff()
    {
        var (dir, _) = Game();
        var (up, srv, set, api) = NewUploader(on: false);
        var report = await up.SyncAsync(dir, force: false);
        Assert.Null(report.Error);
        Assert.Empty(srv.Wdb());
        Assert.Empty(set.CreatureCacheSha);

        var preview = UploadPreview.Build(api, dir, set);
        Assert.Contains("creaturecache.wdb", preview);
        Assert.DoesNotContain("/api/bidrag/varelser", preview);
    }

    [Fact]
    public void PreviewShowsTheCreatureCacheWhenOn()
    {
        var (dir, _) = Game();
        var (_, _, set, api) = NewUploader(on: true);
        var preview = UploadPreview.Build(api, dir, set);
        Assert.Contains("/api/bidrag/varelser", preview);
        Assert.Contains(ApiClient.Sha256Hex(Wdb()), preview);
        Assert.DoesNotContain("itemcache", preview);
    }

    [Fact]
    public async Task RejectedFileIsNotMarkedAndIsRetriedLater()
    {
        var (dir, enUs) = Game();
        var (up, srv, set, _) = NewUploader(on: true);
        srv.Status = HttpStatusCode.BadRequest;
        var report = await up.SyncAsync(dir, force: false);
        Assert.NotNull(report.Error);
        Assert.False(report.WillRetry);
        Assert.Empty(set.CreatureCacheSha);

        srv.Status = HttpStatusCode.OK;
        srv.Calls.Clear();
        report = await up.SyncAsync(dir, force: false);
        Assert.Equal(2, srv.Wdb().Count);
        Assert.True(set.CreatureCacheSha.ContainsKey(Privacy.FileKey(enUs)));
    }

    [Fact]
    public async Task TooLargeIsRefusedBeforeSending()
    {
        var srv = new FakeServer();
        var api = new ApiClient("https://guide.wow4ever.se", srv) { Key = "k" };
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.UploadCreatureCacheAsync(new byte[ApiClient.MaxCreatureCacheBytes + 1]));
        Assert.Equal(ErrorCodes.CreatureCacheTooLarge, ex.Code);
        Assert.Empty(srv.Calls);
        Assert.True(Strings.Has("err." + ErrorCodes.CreatureCacheTooLarge));
    }

    [Fact]
    public void SettingsTextsExistInBothLanguages()
    {
        foreach (var k in new[] { "set.creaturecache", "set.creaturecache.hint", "journal.creaturecache", "srv.not-creaturecache" })
        {
            Assert.True(Strings.All.TryGetValue(k, out var t), k);
            Assert.False(string.IsNullOrWhiteSpace(t.Sv));
            Assert.False(string.IsNullOrWhiteSpace(t.En));
        }
    }
}
