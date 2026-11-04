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
        var set = new Settings { UploadHotfix = false };
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
