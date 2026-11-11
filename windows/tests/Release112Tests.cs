using System.Net;
using System.Text;
using ForeverScout.Core;

namespace ForeverScout.Tests;

/// <summary>
/// 1.0.12 (granskningen 2026-11-11, docs/review/CLIENT-WINDOWS-2026-11-11.md): inga tysta stopp i uppladdningen.
/// M1 omförsök utan broms, M2 en dålig fil stoppar resten, M3 tolkens fel som ingen fångade, M4 hängd nedladdning.
/// </summary>
public sealed class Release112Tests : IDisposable
{
    private readonly DirectoryInfo _tmp = TestEnv.Temp("fs-112-");
    private readonly GameDir _dir;
    private readonly string _sv;
    private readonly string _second;

    public Release112Tests()
    {
        var game = Path.Combine(_tmp.FullName, "World of Warcraft", "_forever_");
        Directory.CreateDirectory(Path.Combine(game, "Interface"));
        foreach (var acc in new[] { "AAA", "BBB" })
        {
            var d = Path.Combine(game, "WTF", "Account", acc, "SavedVariables");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "ForeverScout.lua"), LuaParserTests.Sample.Replace("a1b2c3d4", acc == "AAA" ? "a1b2c3d4" : "e5f6a7b8"));
        }
        var real = SafeFs.SetGameDir(game)!;
        _dir = new GameDir(real, "_forever_");
        _sv = Path.Combine(real, "WTF", "Account", "AAA", "SavedVariables", "ForeverScout.lua");
        _second = Path.Combine(real, "WTF", "Account", "BBB", "SavedVariables", "ForeverScout.lua");
    }

    public void Dispose()
    {
        SafeFs.SetGameDir(null);
        try { _tmp.Delete(true); } catch (IOException) { }
    }

    /// <summary>Låtsasserver: svaret per ladda-upp-anrop (1-baserat) kan styras.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public int Posts;
        public Func<int, HttpResponseMessage?>? Respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("ladda-upp"))
            {
                var n = Interlocked.Increment(ref Posts);
                if (Respond?.Invoke(n) is { } custom) return Task.FromResult(custom);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"accepted":{},"newConfirmed":[],"points":1,"rank":1,"lastSeq":417}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    private (Uploader Up, Server Srv, Settings Set) NewUploader()
    {
        var srv = new Server();
        var api = new ApiClient("https://guide.wow4ever.se", srv) { Key = "k" };
        var set = Settings.Load();
        set.UploadHotfix = false;
        set.UploadedSha.Clear(); set.UploadedSeq.Clear(); set.HotfixSha.Clear(); set.BlockedAddons.Clear();
        set.RetryAttempts = 0; set.NextRetryUtc = null;
        return (new Uploader(api, set), srv, set);
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    [Fact]
    public async Task NonTransientFailureAfterANetworkErrorStopsTheRetryLoop()
    {
        var (up, srv, set) = NewUploader();
        // Först ett tillfälligt fel (503): ett nytt försök schemaläggs.
        srv.Respond = n => n == 1 ? Status(HttpStatusCode.ServiceUnavailable) : null;
        var r1 = await up.SyncAsync(_dir, force: false);
        Assert.True(r1.WillRetry);
        Assert.NotNull(set.NextRetryUtc);

        // Sedan ett bestående fel (proxy-sida med 403, ingen serverkod): förr låg NextRetryUtc kvar i det förflutna och
        // appen försökte igen var 15:e sekund. Nu nollställs kön – nästa försök sker när filen ändras eller appen startar.
        set.NextRetryUtc = DateTime.UtcNow.AddSeconds(-1);
        Assert.True(up.RetryDue);
        srv.Respond = _ => Status(HttpStatusCode.Forbidden, "<html>nej</html>");
        var r2 = await up.SyncAsync(_dir, force: false);
        Assert.NotNull(r2.Error);
        Assert.False(r2.WillRetry);
        Assert.Null(set.NextRetryUtc);
        Assert.Equal(0, set.RetryAttempts);
        Assert.False(up.RetryDue);
    }

    [Fact]
    public async Task ABadFileDoesNotStopTheOtherAccounts()
    {
        var (up, srv, set) = NewUploader();
        // Ena kontots fil får ett bestående fel som inte är en serveravvisning (proxyns 413-sida). Förr avbröt det hela
        // körningen, så det andra kontot (och hotfix, auktionspriser) skickades aldrig.
        srv.Respond = n => n == 1 ? Status(HttpStatusCode.RequestEntityTooLarge, "<html>för stor</html>") : null;
        var r = await up.SyncAsync(_dir, force: false);
        Assert.Equal(1, r.FilesUploaded);
        Assert.Equal(1, r.FilesRejected);
        Assert.NotNull(r.Error);
        Assert.False(r.WillRetry);
        Assert.Single(set.UploadedSha);
        Assert.Equal(2, srv.Posts);
    }

    [Fact]
    public async Task AFileOverTheSizeLimitIsSkippedNotFatal()
    {
        var (up, srv, set) = NewUploader();
        // Över 5 MB men en giltig fil: ApiClient vägrar (upload-too-large) utan att skicka. Det andra kontot går ändå.
        var big = new StringBuilder("ForeverScoutDB = {\n  [\"addon\"] = \"1.0.8\",\n  [\"pad\"] = {\n");
        while (big.Length < ApiClient.MaxUploadBytes + 1000) big.Append("    \"").Append('x', 200).Append("\",\n");
        big.Append("  },\n}\n");
        File.WriteAllText(_sv, big.ToString());
        var r = await up.SyncAsync(_dir, force: false);
        Assert.Equal(1, r.FilesUploaded);
        Assert.Equal(1, r.FilesRejected);
        Assert.Equal(ErrorCodes.UploadTooLarge, ((ScoutException)r.Error!).Code);
        Assert.Equal(1, srv.Posts);
        Assert.Single(set.UploadedSha);
    }

    [Fact]
    public async Task ACorruptTokenInTheAddonFileIsSkippedNotFatal()
    {
        var (up, srv, _) = NewUploader();
        // Förr: OverflowException ur long.Parse, som ingen fångade – bakgrundsuppladdningen stannade utan besked.
        File.WriteAllText(_sv, "ForeverScoutDB = { [\"seq\"] = 0x11111111111111111 }\n");
        var r = await up.SyncAsync(_dir, force: false);
        Assert.Equal(1, r.FilesIncomplete);
        Assert.Equal(1, r.FilesUploaded);
        Assert.Equal(1, srv.Posts);
    }

    [Theory]
    [InlineData("x = 0x11111111111111111")]
    [InlineData("x = \"\\u{}\"")]
    [InlineData("x = \"\\u{zz}\"")]
    [InlineData("x = \"\\u{D800}\"")]
    [InlineData("x = \"\\u{110000}\"")]
    [InlineData("x = \"\\u{123456789}\"")]
    public void HostileTokensGiveAParseErrorNothingElse(string src)
    {
        Assert.Throws<LuaParseException>(() => LuaParser.ParseGlobals(Encoding.UTF8.GetBytes(src)));
        Assert.Throws<LuaParseException>(() => LuaParser.ParseGlobals(src));
    }

    [Fact]
    public void UnicodeEscapeIsCorrectInByteMode()
    {
        // Förr blev \u{E9} ett ensamt Latin-1-tecken som UTF-8-avkodningen gjorde till U+FFFD.
        Assert.Equal("é", LuaParser.ParseGlobals(Encoding.UTF8.GetBytes("x = \"\\u{E9}\""))["x"]);
        Assert.Equal("é", LuaParser.ParseGlobals("x = \"\\u{E9}\"")["x"]);
        Assert.Equal("😀", LuaParser.ParseGlobals(Encoding.UTF8.GetBytes("x = \"\\u{1F600}\""))["x"]);
    }

    [Fact]
    public void AnAuctioneerTimeOutOfRangeIsAProblemNotACrash()
    {
        var src = "AucAdvancedStatSimpleData = { [\"Version\"] = 3, [\"RealmData\"] = { [\"PvE_Alliance\"] = { [\"dailypush\"] = 1e30, [\"daily\"] = { [\"2589\"] = \"1;2;3\" } } } }";
        var r = new AuctioneerReader().Read(Encoding.UTF8.GetBytes(src), DateTime.UtcNow, TimeZoneInfo.Utc);
        Assert.Empty(r.Rows);
        Assert.Equal("auctioneer-window", r.Problem);
    }

    /// <summary>Svarar med huvuden och sedan en ström som aldrig levererar mer data.</summary>
    private sealed class StallingServer : HttpMessageHandler
    {
        private sealed class Stall : Stream
        {
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set { } }
            public override void Flush() { }
            public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
            public override async ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) { await Task.Delay(Timeout.Infinite, ct); return 0; }
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Stall()) });
    }

    [Fact]
    public async Task AStalledDownloadTimesOutInsteadOfHangingForever()
    {
        var api = new ApiClient("https://guide.wow4ever.se", new StallingServer()) { DownloadStall = TimeSpan.FromMilliseconds(300) };
        var release = new SignedRelease { Addon = "1.0.8", AddonFile = new ReleaseFile(ReleaseKind.Addon, "1.0.8", "ForeverScout-addon.zip", new string('a', 64), "sig") };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.DownloadReleaseFileAsync(release, ReleaseKind.Addon, "x", 1024));
        Assert.Equal(ErrorCodes.Timeout, ex.Code);
        Assert.True(ex.Transient);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }
}
