using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ForeverScout.Core;

namespace ForeverScout.Tests;

/// <summary>
/// "Logga in med webbläsaren" (kontraktet 2b, klient 1.0.8 / Mac 1.0.9): DeviceLink-flödet mot en låtsasserver och en
/// låtsasklocka. Ingen test når nätet – ApiClient får alltid en egen HttpMessageHandler, och tester som behöver en
/// loopback-basadress är [DebugFact] (i Release ignorerar ApiClient baseUrl).
/// </summary>
public sealed class DeviceLinkTests
{
    private const string Site = "https://guide.wow4ever.se";
    private const string GoodUrl = "https://guide.wow4ever.se/bidra/koppla?kod=K7QM-4TRX";
    private const string DeviceCode = "dc_0123456789abcdefghijklmnopqrstuvwxyzABCDE";

    private static string StartJson(string url = GoodUrl, string userCode = "K7QM4TRX", int expiresIn = 600, int interval = 3, string? deviceCode = DeviceCode) =>
        JsonSerializer.Serialize(new { userCode, deviceCode, url, expiresIn, interval });

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(string status) => Json(HttpStatusCode.OK, $$"""{"status":"{{status}}"}""");

    private static HttpResponseMessage Approved(string key = "k-new") =>
        Json(HttpStatusCode.OK, $$"""{"status":"approved","id":42,"key":"{{key}}","name":"Jonsson"}""");

    private static HttpResponseMessage RateLimited(int? retryAfter)
    {
        var r = Json(HttpStatusCode.TooManyRequests, """{"code":"rate-limited"}""");
        if (retryAfter is int s) r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
        return r;
    }

    /// <summary>Låtsasserver för /api/bidrag/enhet och /enhet/status. Allt annat är ett testfel.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public List<(Uri Uri, string Body, bool Auth)> Requests { get; } = new();
        public Func<HttpResponseMessage> Start = () => Json(HttpStatusCode.OK, StartJson());
        /// <summary>Svar på statusfrågorna i tur och ordning; när kön är tom: pending.</summary>
        public Queue<Func<HttpResponseMessage>> Statuses { get; } = new();
        public Func<HttpResponseMessage> WhenEmpty = () => Status("pending");
        /// <summary>Om satt: körs vid statusfråga nr n (1-baserat) innan svaret, t.ex. för att avbryta mitt i anropet.</summary>
        public Func<int, CancellationToken, Task>? BeforeStatus;

        public int StatusCalls => Requests.Count(r => r.Uri.AbsolutePath == "/api/bidrag/enhet/status");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            Requests.Add((req.RequestUri!, body, req.Headers.Authorization != null));
            Assert.Equal(HttpMethod.Post, req.Method);
            switch (req.RequestUri!.AbsolutePath)
            {
                case "/api/bidrag/enhet":
                    return Start();
                case "/api/bidrag/enhet/status":
                    if (BeforeStatus != null) await BeforeStatus(StatusCalls, ct);
                    return Statuses.Count > 0 ? Statuses.Dequeue()() : WhenEmpty();
                default:
                    throw new InvalidOperationException("oväntad sökväg " + req.RequestUri);
            }
        }
    }

    /// <summary>Låtsasklocka: Delay flyttar bara tiden framåt och minns väntetiderna.</summary>
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        public List<double> Waits { get; } = new();
        public Action<int>? OnWait;

        public Task Delay(TimeSpan t, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Waits.Add(t.TotalSeconds);
            Now += t;
            OnWait?.Invoke(Waits.Count);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private static DeviceLink Flow(ApiClient api, Clock clock, Action<Contributor>? onApproved = null) =>
        new(api, "windows", "1.0.8") { Delay = clock.Delay, Now = () => clock.Now, OnApproved = onApproved };

    private static Settings FreshSettings()
    {
        var s = Settings.Load();
        s.ContributorId = "7";
        s.ContributorName = "Gammal";
        s.Points = 120;
        s.Rank = 3;
        s.LastConfirmed = [new ConfirmedItem { Kind = "npc", Id = "198", Name = "Khelden Bremen" }];
        s.Save();
        return s;
    }

    // ------------------------------------------------------------------ statusar

    [Fact]
    public async Task ApprovedAfterPending_StoresKeyLikeTheCodeLink()
    {
        var srv = new FakeServer();
        srv.Statuses.Enqueue(() => Status("pending"));
        srv.Statuses.Enqueue(() => Status("pending"));
        srv.Statuses.Enqueue(() => Approved("k-new"));
        using var api = new ApiClient(Site, srv) { Key = "old-key" };
        var settings = FreshSettings();
        string? savedKey = null;
        var clock = new Clock();
        var tickets = new List<DeviceLinkTicket>();

        var r = await Flow(api, clock, who => ContributorStore.Save(who, api, settings, k => savedKey = k)).RunAsync(tickets.Add);

        Assert.Equal(DeviceLinkOutcome.Approved, r.Outcome);
        Assert.Equal(new Contributor("42", "k-new", "Jonsson"), r.Contributor);
        // Sparad precis som efter koppla med kod: nyckeln, API-klienten, id och namn i inställningarna.
        Assert.Equal("k-new", savedKey);
        Assert.Equal("k-new", api.Key);
        Assert.Equal("42", settings.ContributorId);
        Assert.Equal("Jonsson", settings.ContributorName);
        // Nytt konto: det gamla kontots poäng och fynd visas inte längre.
        Assert.Null(settings.Points);
        Assert.Null(settings.Rank);
        Assert.Empty(settings.LastConfirmed);
        var reloaded = Settings.Load();
        Assert.Equal("Jonsson", reloaded.ContributorName);
        Assert.Equal("42", reloaded.ContributorId);

        var t = Assert.Single(tickets);
        Assert.Equal("K7QM-4TRX", t.DisplayCode);
        Assert.Equal(new Uri(GoodUrl), t.Url);
        Assert.Equal(new[] { 3.0, 3.0, 3.0 }, clock.Waits);

        // Start: plattform och version, ingen nyckel (inte ens om klienten redan har en). Status: bara deviceCode.
        Assert.Equal(4, srv.Requests.Count);
        Assert.All(srv.Requests, q => Assert.False(q.Auth));
        using (var start = JsonDocument.Parse(srv.Requests[0].Body))
        {
            Assert.Equal("windows", start.RootElement.GetProperty("platform").GetString());
            Assert.Equal("1.0.8", start.RootElement.GetProperty("version").GetString());
        }
        foreach (var q in srv.Requests.Skip(1))
        {
            using var b = JsonDocument.Parse(q.Body);
            Assert.Equal(DeviceCode, b.RootElement.GetProperty("deviceCode").GetString());
            Assert.Equal("https://guide.wow4ever.se/api/bidrag/enhet/status", q.Uri.ToString());
        }
    }

    [Fact]
    public async Task SameAccount_KeepsPoints()
    {
        var srv = new FakeServer();
        srv.Statuses.Enqueue(() => Approved());
        using var api = new ApiClient(Site, srv);
        var settings = FreshSettings();
        settings.ContributorId = "42";
        var r = await Flow(api, new Clock(), who => ContributorStore.Save(who, api, settings, _ => { })).RunAsync(_ => { });
        Assert.Equal(DeviceLinkOutcome.Approved, r.Outcome);
        Assert.Equal(120, settings.Points);
        Assert.Single(settings.LastConfirmed);
    }

    [Theory]
    [InlineData("denied", DeviceLinkOutcome.Denied)]
    [InlineData("expired", DeviceLinkOutcome.Expired)]
    [InlineData("too-many-keys", DeviceLinkOutcome.TooManyKeys)]
    public async Task FinalStatuses_StopWithoutStoring(string status, DeviceLinkOutcome expected)
    {
        var srv = new FakeServer();
        srv.Statuses.Enqueue(() => Status("pending"));
        srv.Statuses.Enqueue(() => Status(status));
        using var api = new ApiClient(Site, srv) { Key = "old-key" };
        bool stored = false;
        var r = await Flow(api, new Clock(), _ => stored = true).RunAsync(_ => { });
        Assert.Equal(expected, r.Outcome);
        Assert.Null(r.Contributor);
        Assert.False(stored);
        Assert.Equal("old-key", api.Key);
        Assert.Equal(2, srv.StatusCalls);
    }

    [Theory]
    [InlineData("""{"status":"approved","id":1,"name":"X"}""")]           // ingen nyckel
    [InlineData("""{"status":"approved","id":1,"key":"","name":"X"}""")]  // tom nyckel
    [InlineData("""{"status":"maybe"}""")]                                // okänd status
    [InlineData("""{}""")]
    public async Task OddStatus_FailsWithoutStoring(string json)
    {
        var srv = new FakeServer();
        srv.Statuses.Enqueue(() => Json(HttpStatusCode.OK, json));
        using var api = new ApiClient(Site, srv);
        bool stored = false;
        var r = await Flow(api, new Clock(), _ => stored = true).RunAsync(_ => { });
        Assert.Equal(DeviceLinkOutcome.Failed, r.Outcome);
        Assert.Equal(ErrorCodes.BadResponse, Assert.IsType<ApiException>(r.Error).Code);
        Assert.False(stored);
        Assert.Null(api.Key);
    }

    [Fact]
    public async Task StoreFails_IsAFailureNotApproved()
    {
        var srv = new FakeServer();
        srv.Statuses.Enqueue(() => Approved());
        using var api = new ApiClient(Site, srv);
        var r = await Flow(api, new Clock(), _ => throw new IOException("disk")).RunAsync(_ => { });
        Assert.Equal(DeviceLinkOutcome.Failed, r.Outcome);
        Assert.IsType<IOException>(r.Error);
        Assert.Equal(Strings.S("err.io"), Strings.Error(r.Error!));
    }

    // ------------------------------------------------------------------ 429 och fel

    [Theory]
    [InlineData(7, 7.0)]       // Retry-After vinner
    [InlineData(null, 6.0)]    // utan Retry-After: dubbla intervallet
    [InlineData(1, 3.0)]       // aldrig kortare än intervallet
    [InlineData(3600, 60.0)]   // och aldrig längre än en minut åt gången
    public async Task RateLimited_BacksOffAndContinues(int? retryAfter, double expectedWait)
    {
        var srv = new FakeServer();
        srv.Statuses.Enqueue(() => RateLimited(retryAfter));
        srv.Statuses.Enqueue(() => Status("pending"));
        srv.Statuses.Enqueue(() => Approved());
        using var api = new ApiClient(Site, srv);
        var clock = new Clock();
        var r = await Flow(api, clock).RunAsync(_ => { });
        Assert.Equal(DeviceLinkOutcome.Approved, r.Outcome);
        Assert.Equal(new[] { 3.0, expectedWait, 3.0 }, clock.Waits); // efter backoff: tillbaka till intervallet
    }

    [Fact]
    public async Task RetryAfter_IsParsedOnApiException()
    {
        var srv = new FakeServer { Start = () => RateLimited(30) };
        using var api = new ApiClient(Site, srv);
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.DeviceStartAsync("windows", "1.0.8"));
        Assert.Equal("rate-limited", ex.Code);
        Assert.True(ex.FromServer);
        Assert.True(ex.Transient);
        Assert.Equal(TimeSpan.FromSeconds(30), ex.RetryAfter);
    }

    [Fact]
    public async Task StartRejected_FailsBeforeAnythingIsShown()
    {
        var srv = new FakeServer { Start = () => RateLimited(30) };
        using var api = new ApiClient(Site, srv);
        bool started = false;
        var r = await Flow(api, new Clock()).RunAsync(_ => started = true);
        Assert.Equal(DeviceLinkOutcome.Failed, r.Outcome);
        Assert.Equal("rate-limited", Assert.IsType<ApiException>(r.Error).Code);
        Assert.False(started);
        Assert.Equal(0, srv.StatusCalls);
    }

    [Fact]
    public async Task ClientError_StopsAtOnce()
    {
        var srv = new FakeServer();
        srv.Statuses.Enqueue(() => Json(HttpStatusCode.BadRequest, """{"code":"invalid"}"""));
        using var api = new ApiClient(Site, srv);
        var r = await Flow(api, new Clock()).RunAsync(_ => { });
        Assert.Equal(DeviceLinkOutcome.Failed, r.Outcome);
        Assert.Equal("invalid", Assert.IsType<ApiException>(r.Error).Code);
        Assert.Equal(1, srv.StatusCalls);
    }

    [Fact]
    public async Task ServerTrouble_IsRetriedThenApproved()
    {
        var srv = new FakeServer();
        srv.Statuses.Enqueue(() => Json(HttpStatusCode.ServiceUnavailable, """{"code":"unavailable"}"""));
        srv.Statuses.Enqueue(() => Approved());
        using var api = new ApiClient(Site, srv);
        var clock = new Clock();
        var r = await Flow(api, clock).RunAsync(_ => { });
        Assert.Equal(DeviceLinkOutcome.Approved, r.Outcome);
        Assert.Equal(new[] { 3.0, 6.0 }, clock.Waits);
    }

    // ------------------------------------------------------------------ tiden

    [Fact]
    public async Task Expiry_StopsWhenTimeIsUp()
    {
        var srv = new FakeServer { Start = () => Json(HttpStatusCode.OK, StartJson(expiresIn: 10, interval: 3)) };
        using var api = new ApiClient(Site, srv);
        var clock = new Clock();
        var t0 = clock.Now;
        var r = await Flow(api, clock).RunAsync(_ => { });
        Assert.Equal(DeviceLinkOutcome.Expired, r.Outcome);
        Assert.Equal(new[] { 3.0, 3.0, 3.0, 1.0 }, clock.Waits); // sista väntan kortas till tidsgränsen, sedan en sista fråga
        Assert.Equal(TimeSpan.FromSeconds(10), clock.Now - t0);
        Assert.Equal(4, srv.StatusCalls);
    }

    [Fact]
    public async Task Expiry_WhileServerIsDown_ReportsTheError()
    {
        var srv = new FakeServer
        {
            Start = () => Json(HttpStatusCode.OK, StartJson(expiresIn: 20, interval: 3)),
            WhenEmpty = () => Json(HttpStatusCode.ServiceUnavailable, """{"code":"unavailable"}"""),
        };
        using var api = new ApiClient(Site, srv);
        var r = await Flow(api, new Clock()).RunAsync(_ => { });
        Assert.Equal(DeviceLinkOutcome.Failed, r.Outcome);
        Assert.Equal("unavailable", Assert.IsType<ApiException>(r.Error).Code);
        Assert.True(srv.StatusCalls > 1);
    }

    [Theory]
    [InlineData(0, 5.0)]      // saknas → 5 s (RFC 8628)
    [InlineData(1000, 60.0)]  // orimligt → högst en minut
    public async Task Interval_IsSane(int interval, double expected)
    {
        var srv = new FakeServer { Start = () => Json(HttpStatusCode.OK, StartJson(interval: interval)) };
        srv.Statuses.Enqueue(() => Approved());
        using var api = new ApiClient(Site, srv);
        var clock = new Clock();
        var tickets = new List<DeviceLinkTicket>();
        await Flow(api, clock).RunAsync(tickets.Add);
        Assert.Equal(new[] { expected }, clock.Waits);
        Assert.Equal(TimeSpan.FromSeconds(expected), Assert.Single(tickets).Interval);
    }

    // ------------------------------------------------------------------ avbryt

    [Fact]
    public async Task Cancel_RightAfterStart_NoPolling()
    {
        var srv = new FakeServer();
        using var api = new ApiClient(Site, srv);
        using var cts = new CancellationTokenSource();
        var r = await Flow(api, new Clock()).RunAsync(_ => cts.Cancel(), cts.Token);
        Assert.Equal(DeviceLinkOutcome.Cancelled, r.Outcome);
        Assert.Equal(0, srv.StatusCalls);
    }

    [Fact]
    public async Task Cancel_WhileWaiting()
    {
        var srv = new FakeServer();
        using var api = new ApiClient(Site, srv);
        using var cts = new CancellationTokenSource();
        var clock = new Clock { OnWait = n => { if (n == 3) cts.Cancel(); } };
        bool stored = false;
        var r = await Flow(api, clock, _ => stored = true).RunAsync(_ => { }, cts.Token);
        Assert.Equal(DeviceLinkOutcome.Cancelled, r.Outcome);
        Assert.Equal(2, srv.StatusCalls);
        Assert.False(stored);
    }

    [Fact]
    public async Task Cancel_DuringRequest()
    {
        using var cts = new CancellationTokenSource();
        var srv = new FakeServer
        {
            BeforeStatus = async (n, ct) => { if (n == 2) { cts.Cancel(); await Task.Delay(Timeout.Infinite, ct); } },
        };
        using var api = new ApiClient(Site, srv);
        var r = await Flow(api, new Clock()).RunAsync(_ => { }, cts.Token);
        Assert.Equal(DeviceLinkOutcome.Cancelled, r.Outcome);
        Assert.Equal(2, srv.StatusCalls);
    }

    [Fact]
    public async Task Cancel_BeforeStart()
    {
        var srv = new FakeServer();
        using var api = new ApiClient(Site, srv);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await Flow(api, new Clock()).RunAsync(_ => { }, cts.Token);
        Assert.Equal(DeviceLinkOutcome.Cancelled, r.Outcome);
    }

    // ------------------------------------------------------------------ svaret från start

    [Theory]
    [InlineData("https://guide.wow4ever.se.evil.com/bidra/koppla")]
    [InlineData("http://guide.wow4ever.se/bidra/koppla")]
    [InlineData("https://evil.com/")]
    [InlineData("https://guide.wow4ever.se@evil.com/")]
    public async Task BadUrl_IsNeverShownOrPolled(string url)
    {
        var srv = new FakeServer { Start = () => Json(HttpStatusCode.OK, StartJson(url: url)) };
        using var api = new ApiClient(Site, srv);
        bool started = false;
        var r = await Flow(api, new Clock()).RunAsync(_ => started = true);
        Assert.Equal(DeviceLinkOutcome.BadUrl, r.Outcome);
        Assert.False(started);
        Assert.Equal(0, srv.StatusCalls);
    }

    [Theory]
    [InlineData("K7QM4TR", DeviceCode)]   // 7 tecken
    [InlineData("K7QM4TR0", DeviceCode)]  // 0 finns inte i alfabetet
    [InlineData("k7qm4trx", DeviceCode)]  // gemener
    [InlineData("K7QM4TRX", null)]        // ingen deviceCode
    [InlineData("K7QM4TRX", "")]
    public async Task BadStartFields_Fail(string userCode, string? deviceCode)
    {
        var srv = new FakeServer { Start = () => Json(HttpStatusCode.OK, StartJson(userCode: userCode, deviceCode: deviceCode)) };
        using var api = new ApiClient(Site, srv);
        bool started = false;
        var r = await Flow(api, new Clock()).RunAsync(_ => started = true);
        Assert.Equal(DeviceLinkOutcome.Failed, r.Outcome);
        Assert.False(started);
    }

    [Fact]
    public void FormatCode_GroupsByFour()
    {
        Assert.Equal("K7QM-4TRX", DeviceLink.FormatCode("K7QM4TRX"));
        Assert.Equal("ABC", DeviceLink.FormatCode("ABC"));
    }

    // ------------------------------------------------------------------ url-regeln

    [Theory]
    [InlineData("https://guide.wow4ever.se/bidra/koppla?kod=K7QM-4TRX")]
    [InlineData("https://guide.wow4ever.se/en/contribute/link?kod=K7QM-4TRX")]
    [InlineData("https://guide.wow4ever.se/")]
    public void Url_Allowed(string url) => Assert.True(DeviceLink.IsAllowedUrl(url, new Uri(Site)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://guide.wow4ever.se")]                          // inget snedstreck efter värden
    [InlineData("http://guide.wow4ever.se/bidra/koppla")]              // http
    [InlineData("HTTPS://guide.wow4ever.se/bidra/koppla")]             // inte exakt
    [InlineData("https://GUIDE.wow4ever.se/bidra/koppla")]
    [InlineData("https://guide.wow4ever.se.evil.com/")]                // liknande värd
    [InlineData("https://guide.wow4ever.se.evil.com/bidra/koppla")]
    [InlineData("https://guide.wow4ever.se./bidra")]
    [InlineData("https://guide-wow4ever.se/")]
    [InlineData("https://wow4ever.se/")]
    [InlineData("https://evil.com/")]                                  // fel värd
    [InlineData("https://evil.com/?r=https://guide.wow4ever.se/")]
    [InlineData("https://guide.wow4ever.se@evil.com/")]                // användarinfo
    [InlineData("https://guide.wow4ever.se:443@evil.com/")]
    [InlineData("https://user:pw@guide.wow4ever.se/")]
    [InlineData("https://evil.com\\@guide.wow4ever.se/")]
    [InlineData("https://guide.wow4ever.se:8443/")]                    // annan port
    [InlineData("https://guide.wow4ever.se/\\evil.com")]               // bakstreck
    [InlineData("https://guide.wow4ever.se/ bidra")]                   // blanktecken
    [InlineData("https://guide.wow4ever.se/\r\nLocation: x")]          // styrtecken
    [InlineData("https://guide.wow4ever.se/\u00A0x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("//guide.wow4ever.se/bidra")]
    [InlineData(" https://guide.wow4ever.se/")]
    public void Url_Rejected(string? url) => Assert.False(DeviceLink.IsAllowedUrl(url, new Uri(Site)));

    [Fact]
    public void Url_TooLong_Rejected() =>
        Assert.False(DeviceLink.IsAllowedUrl("https://guide.wow4ever.se/" + new string('a', 3000), new Uri(Site)));

    /// <summary>Loopback-basadressen godtas bara i Debug; i Release är bara guide.wow4ever.se tillåten.</summary>
    [Fact]
    public void Url_Loopback_OnlyInDebug() =>
        Assert.Equal(TestEnv.IsDebug, DeviceLink.IsAllowedUrl("http://127.0.0.1:5123/bidra/koppla?kod=K7QM-4TRX", new Uri("http://127.0.0.1:5123")));

    [Fact]
    public void Url_NonLoopbackBase_NeverAllowed() =>
        Assert.False(DeviceLink.IsAllowedUrl("https://staging.example.com/bidra/koppla", new Uri("https://staging.example.com")));

    [DebugTheory]
    [InlineData("http://127.0.0.1:5123/bidra/koppla?kod=K7QM-4TRX", true)]
    [InlineData("http://127.0.0.1:5123/", true)]
    [InlineData("https://guide.wow4ever.se/bidra/koppla", true)]      // produktionen gäller alltid
    [InlineData("http://127.0.0.1:5124/bidra/koppla", false)]         // annan port
    [InlineData("http://localhost:5123/bidra/koppla", false)]         // annan värdsträng än basadressen
    [InlineData("https://127.0.0.1:5123/bidra/koppla", false)]        // annat schema
    [InlineData("http://127.0.0.1:5123@evil.com/", false)]
    [InlineData("http://127.0.0.1:51234/", false)]
    [InlineData("http://127.0.0.1:5123", false)]
    public void Url_DebugLoopbackBase(string url, bool allowed) =>
        Assert.Equal(allowed, DeviceLink.IsAllowedUrl(url, new Uri("http://127.0.0.1:5123")));

    [DebugFact]
    public async Task DebugLoopback_FullFlow()
    {
        var srv = new FakeServer { Start = () => Json(HttpStatusCode.OK, StartJson(url: "http://127.0.0.1:5123/bidra/koppla?kod=K7QM-4TRX")) };
        srv.Statuses.Enqueue(() => Approved());
        using var api = new ApiClient("http://127.0.0.1:5123", srv);
        var r = await Flow(api, new Clock()).RunAsync(_ => { });
        Assert.Equal(DeviceLinkOutcome.Approved, r.Outcome);
        Assert.All(srv.Requests, q => Assert.Equal("127.0.0.1", q.Uri.Host));
    }

    [Fact]
    public void Strings_ForEveryOutcome()
    {
        foreach (var key in new[] { "link.browser", "link.explain", "link.waiting", "link.cancel", "link.denied", "link.expired", "link.toomany", "link.badurl", "link.ok", "menu.login" })
            Assert.True(Strings.Has(key), key);
        try
        {
            Strings.Apply("sv");
            Assert.Equal("Logga in med webbläsaren", Strings.S("link.browser"));
            Assert.Equal("Kopplad som Jonsson", Strings.F("link.ok", "Jonsson"));
            Assert.Contains("K7QM-4TRX", Strings.F("link.waiting", DeviceLink.FormatCode("K7QM4TRX")));
            Assert.DoesNotContain("kommer snart", Strings.S("menu.login"));
            Strings.Apply("en");
            Assert.Equal("Log in with your browser", Strings.S("link.browser"));
        }
        finally { Strings.Apply("sv"); }
    }
}
