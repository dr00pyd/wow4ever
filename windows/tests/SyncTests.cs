using System.IO.Compression;
using System.Net;
using System.Text;
using ForeverScout.Core;

namespace ForeverScout.Tests;

/// <summary>Uppladdning vid filändring: debounce, hash-skip, dirty-omkörning, halvskriven fil, inga parallella körningar.</summary>
public sealed class SyncTests : IDisposable
{
    private readonly DirectoryInfo _tmp = TestEnv.Temp("fs-sync-");
    private readonly GameDir _dir;
    private readonly string _sv;

    public SyncTests()
    {
        var game = Path.Combine(_tmp.FullName, "World of Warcraft", "_forever_");
        var svDir = Path.Combine(game, "WTF", "Account", "ACCOUNTNAME", "SavedVariables");
        Directory.CreateDirectory(svDir);
        Directory.CreateDirectory(Path.Combine(game, "Interface"));
        _sv = Path.Combine(svDir, "ForeverScout.lua");
        File.WriteAllText(_sv, LuaParserTests.Sample);
        var real = SafeFs.SetGameDir(game)!;
        _dir = new GameDir(real, "_forever_");
        _sv = Path.Combine(real, "WTF", "Account", "ACCOUNTNAME", "SavedVariables", "ForeverScout.lua");
    }

    public void Dispose()
    {
        SafeFs.SetGameDir(null);
        try { _tmp.Delete(true); } catch (IOException) { }
    }

    /// <summary>Låtsasserver: räknar anrop, mäter samtidighet, sparar rubriker och kropp.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public int Posts, Concurrent, MaxConcurrent;
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<byte[]> Bodies { get; } = new();
        public TimeSpan Delay = TimeSpan.Zero;
        /// <summary>Om satt: svar för ladda-upp nr n (1-baserat); null = vanligt OK-svar.</summary>
        public Func<int, HttpResponseMessage?>? Respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref Concurrent);
            lock (this) MaxConcurrent = Math.Max(MaxConcurrent, now);
            try
            {
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
                lock (this)
                {
                    Requests.Add(req);
                    if (req.Content != null) Bodies.Add(req.Content.ReadAsByteArrayAsync(ct).Result);
                    if (req.RequestUri!.AbsolutePath.EndsWith("ladda-upp")) Posts++;
                    if (req.RequestUri!.AbsolutePath.EndsWith("ladda-upp") && Respond?.Invoke(Posts) is { } custom) return custom;
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"accepted":{},"newConfirmed":[],"points":1,"rank":1,"lastSeq":417}""", Encoding.UTF8, "application/json"),
                };
            }
            finally { Interlocked.Decrement(ref Concurrent); }
        }
    }

    private (Uploader Up, FakeServer Srv, Settings Set) NewUploader()
    {
        var srv = new FakeServer();
        var api = new ApiClient("https://guide.wow4ever.se", srv) { Key = "k" };
        // Riktig inställningsfil i testernas appdata (Save måste fungera), men utan tillstånd från tidigare tester.
        var set = Settings.Load();
        set.UploadHotfix = false;
        set.UploadedSha.Clear(); set.UploadedSeq.Clear(); set.HotfixSha.Clear(); set.BlockedAddons.Clear();
        set.RetryAttempts = 0; set.NextRetryUtc = null;
        return (new Uploader(api, set), srv, set);
    }

    [Fact]
    public async Task UnchangedFileIsNotSentTwice()
    {
        var (up, srv, _) = NewUploader();
        Assert.Equal(1, (await up.SyncAsync(_dir, force: false)).FilesUploaded);
        var second = await up.SyncAsync(_dir, force: false);
        Assert.Equal(0, second.FilesUploaded);
        Assert.Equal(1, second.FilesUnchanged);
        File.AppendAllText(_sv, "\n-- ny post\n");
        Assert.Equal(1, (await up.SyncAsync(_dir, force: false)).FilesUploaded);
        Assert.Equal(2, srv.Posts);
    }

    private static HttpResponseMessage BadFile() => new(HttpStatusCode.BadRequest)
    {
        Content = new StringContent("""{"error":"Filen kunde inte läsas som ForeverScout.lua.","code":"bad-file","detail":"Okänd eller saknad klientbuild"}""", Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task RejectedFileIsNotMarkedUploadedAndIsRetried()
    {
        var (up, srv, set) = NewUploader();
        srv.Respond = n => n == 1 ? BadFile() : null; // servern avvisar första gången, godtar sedan (t.ex. efter en serverfix)
        var first = await up.SyncAsync(_dir, force: false);
        Assert.Equal(1, first.FilesRejected);
        Assert.Equal(0, first.FilesUploaded);
        Assert.False(first.WillRetry);                    // inget nätfel → ingen backoff-kö
        Assert.Empty(set.UploadedSha);                    // INTE markerad som uppladdad
        Assert.Empty(set.UploadedSeq);
        var ex = Assert.IsType<ApiException>(first.Error);
        Assert.Equal("bad-file", ex.Code);
        Assert.Equal("Okänd eller saknad klientbuild", ex.ServerDetail);
        // Samma, oförändrade fil skickas igen vid nästa försök ("Ladda upp", nästa start, efter versionskontroll).
        var second = await up.SyncAsync(_dir, force: false);
        Assert.Equal(1, second.FilesUploaded);
        Assert.Equal(2, srv.Posts);
        Assert.NotEmpty(set.UploadedSha);
        // … och därefter inte igen (sha256 oförändrad).
        Assert.Equal(0, (await up.SyncAsync(_dir, force: false)).FilesUploaded);
        Assert.Equal(2, srv.Posts);
    }

    [Fact]
    public async Task RejectedFileDoesNotStopOtherAccounts()
    {
        var other = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(_sv)!)!, "..", "SECOND", "SavedVariables");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "ForeverScout.lua"), LuaParserTests.Sample.Replace("a1b2c3d4", "e5f6a7b8"));
        var (up, srv, set) = NewUploader();
        srv.Respond = n => n == 1 ? BadFile() : null;
        var r = await up.SyncAsync(_dir, force: false);
        Assert.Equal(1, r.FilesRejected);
        Assert.Equal(1, r.FilesUploaded);
        Assert.Single(set.UploadedSha);
    }

    [Fact]
    public void ServerDetailIsShownInBothLanguages()
    {
        var ex = new ApiException("bad-file", HttpStatusCode.BadRequest) { FromServer = true, ServerDetail = "Okänd eller saknad klientbuild" };
        try
        {
            Strings.Apply("sv");
            Assert.Equal("Servern avvisade filen: Okänd eller saknad klientbuild", Strings.Error(ex));
            Strings.Apply("en");
            Assert.Equal("The server rejected the file: Okänd eller saknad klientbuild", Strings.Error(ex));
            var other = new ApiException("invalid", HttpStatusCode.BadRequest) { FromServer = true, ServerDetail = "x" };
            Assert.Equal("Invalid request. (x)", Strings.Error(other));
            Assert.Equal("The file could not be read as ForeverScout.lua.", Strings.Error(new ApiException("bad-file", HttpStatusCode.BadRequest) { FromServer = true }));
        }
        finally { Strings.Apply("sv"); }
    }

    [Fact]
    public async Task ServerDetailIsParsedAndSanitized()
    {
        var srv = new FakeServer
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"code\":\"bad-file\",\"detail\":\"rad1\\nrad2\\t" + new string('x', 300) + "\"}", Encoding.UTF8, "application/json"),
            },
        };
        using var api = new ApiClient("https://guide.wow4ever.se", srv) { Key = "k" };
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.UploadAsync(Encoding.UTF8.GetBytes("ForeverScoutDB = {}")));
        Assert.True(ex.Rejected);
        Assert.StartsWith("rad1 rad2 xxx", ex.ServerDetail);
        Assert.True(ex.ServerDetail!.Length <= 201);
        Assert.DoesNotContain('\n', ex.ServerDetail);
    }

    [Fact]
    public async Task HalfWrittenFileIsNotSent()
    {
        var (up, srv, set) = NewUploader();
        File.WriteAllText(_sv, LuaParserTests.Sample[..(LuaParserTests.Sample.Length / 2)]);
        var r = await up.SyncAsync(_dir, force: false);
        Assert.Equal(1, r.FilesIncomplete);
        Assert.Equal(0, srv.Posts);
        Assert.Empty(set.UploadedSha);
        File.WriteAllText(_sv, LuaParserTests.Sample);
        Assert.Equal(1, (await up.SyncAsync(_dir, force: false)).FilesUploaded);
    }

    [Theory]
    [InlineData("ForeverScoutDB = {}\nOtherAddonDB = { secret = 1 }")]
    [InlineData("Something = {}")]
    [InlineData("ForeverScoutDB = 5")]
    public async Task FilesWithAnythingButForeverScoutDBAreRefused(string content)
    {
        var (up, srv, _) = NewUploader();
        File.WriteAllText(_sv, content);
        var r = await up.SyncAsync(_dir, force: false);
        Assert.Equal(0, srv.Posts);
        Assert.Equal(0, r.FilesUploaded);
    }

    [Fact]
    public async Task OnlyFileBytesAndMinimalHeadersAreSent()
    {
        var (up, srv, _) = NewUploader();
        await up.SyncAsync(_dir, force: false);
        var req = Assert.Single(srv.Requests);
        var headers = req.Headers.Select(h => h.Key).Concat(req.Content!.Headers.Select(h => h.Key)).Select(h => h.ToLowerInvariant()).OrderBy(x => x).ToArray();
        Assert.Equal(["authorization", "content-encoding", "content-length", "content-type", "user-agent"], headers.Where(h => h != "content-length").Append("content-length").OrderBy(x => x).ToArray());
        Assert.Matches(@"^ForeverScout/\d+\.\d+\.\d+$", string.Join(" ", req.Headers.GetValues("User-Agent")));
        using var gz = new GZipStream(new MemoryStream(srv.Bodies[0]), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        Assert.Equal(File.ReadAllBytes(_sv), ms.ToArray());
        var all = Encoding.UTF8.GetString(ms.ToArray()) + req.RequestUri + string.Join(";", req.Headers.SelectMany(h => h.Value));
        Assert.DoesNotContain("ACCOUNTNAME", all);
        Assert.DoesNotContain(Environment.MachineName, all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadsNeverRunConcurrently()
    {
        var (up, srv, _) = NewUploader();
        srv.Delay = TimeSpan.FromMilliseconds(150);
        var a = up.SyncAsync(_dir, force: true);
        var b = up.SyncAsync(_dir, force: true);
        var c = up.SyncAsync(_dir, force: true);
        await Task.WhenAll(a, b, c);
        Assert.Equal(1, srv.MaxConcurrent);
    }

    [Fact]
    public async Task UploadLogRecordsWithoutPaths()
    {
        var (up, _, _) = NewUploader();
        await up.SyncAsync(_dir, force: false);
        var log = File.ReadAllText(UploadLog.PathFor);
        Assert.Contains("ladda-upp", log);
        Assert.Contains(ApiClient.Sha256Hex(File.ReadAllBytes(_sv)), log);
        Assert.DoesNotContain("ACCOUNTNAME", log);
    }

    // ---------------------------------------------------------------- SyncCoordinator

    [Fact]
    public async Task DebounceWaitsUntilFileIsStable()
    {
        int runs = 0;
        DateTime lastWrite = DateTime.MinValue, ranAt = DateTime.MinValue;
        using var sync = new SyncCoordinator(() => [_sv], _ => { runs++; ranAt = DateTime.UtcNow; return Task.CompletedTask; },
            quiet: TimeSpan.FromMilliseconds(400), poll: TimeSpan.FromHours(1));
        sync.Notify();
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(120);
            File.AppendAllText(_sv, $"\n-- {i}\n");
            lastWrite = DateTime.UtcNow;
            sync.Notify(); // som FSW-händelser under skrivningen
        }
        await WaitIdle(sync);
        Assert.Equal(1, runs);
        Assert.True(ranAt - lastWrite >= TimeSpan.FromMilliseconds(350), $"körde för tidigt: {(ranAt - lastWrite).TotalMilliseconds} ms efter sista skrivningen");
    }

    [Fact]
    public async Task ChangeDuringRunTriggersExactlyOneMoreRun()
    {
        var gate = new TaskCompletionSource();
        int runs = 0, concurrent = 0, maxConcurrent = 0;
        using var sync = new SyncCoordinator(() => [_sv], async _ =>
        {
            var c = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, c);
            if (Interlocked.Increment(ref runs) == 1) await gate.Task;
            Interlocked.Decrement(ref concurrent);
        }, quiet: TimeSpan.FromMilliseconds(100), poll: TimeSpan.FromHours(1));

        sync.Notify();
        while (runs == 0) await Task.Delay(20);
        for (int i = 0; i < 5; i++) sync.Notify(); // flera ändringar under pågående körning → "dirty"
        gate.SetResult();
        await WaitIdle(sync);
        Assert.Equal(2, runs);
        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task PollFallbackNoticesChangesWithoutEvents()
    {
        int runs = 0;
        using var sync = new SyncCoordinator(() => [_sv], _ => { runs++; return Task.CompletedTask; },
            quiet: TimeSpan.FromMilliseconds(100), poll: TimeSpan.FromMilliseconds(150));
        File.AppendAllText(_sv, "\n-- poll\n");
        for (int i = 0; i < 40 && runs == 0; i++) await Task.Delay(50);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task CoordinatorPlusUploaderSendsOnceForBurstOfSaves()
    {
        var (up, srv, _) = NewUploader();
        using var sync = new SyncCoordinator(() => WowLocator.SavedVariableFiles(_dir),
            async ct => await up.SyncAsync(_dir, force: false, ct), quiet: TimeSpan.FromMilliseconds(200), poll: TimeSpan.FromHours(1));
        sync.Notify();
        await WaitIdle(sync);
        sync.Notify(); // t.ex. processavslut direkt efter – samma innehåll
        await WaitIdle(sync);
        Assert.Equal(1, srv.Posts);
    }

    private static async Task WaitIdle(SyncCoordinator sync)
    {
        for (int i = 0; i < 100; i++)
        {
            await sync.WhenIdleAsync();
            await Task.Delay(50);
            var t = sync.WhenIdleAsync();
            if (t.IsCompleted) return;
        }
    }
}
