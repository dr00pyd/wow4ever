using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForeverScout.Core;
using Old = ForeverScout.Tests.Compat102;

namespace ForeverScout.Tests;

/// <summary>
/// Säkerhetsfixarna i 1.0.7 (docs/review/SAKERHET-AUDIT-2026-11-05.md): L10 (signature2 krävs, "issued" får inte gå
/// bakåt, ingen nedgradering av addonen via osignerade fält), L11 (automatiskt hittad spelkatalog bekräftas) och L12
/// (omdirigeringar följs aldrig). Plattformsoberoende – länkas även in i macOS-testerna.
/// </summary>
public sealed class Release107Tests : IDisposable
{
    private readonly TestSigner _signer = new();
    private readonly DirectoryInfo _tmp = TestEnv.Temp("fs-107-");

    public void Dispose()
    {
        _signer.Dispose();
        try { _tmp.Delete(true); } catch (IOException) { }
        // Settings.Load(dir) pekar om SafeFs appdata – återställ till testernas gemensamma katalog.
        SafeFs.UseAppDataDir(TestEnv.AppData);
    }

    private static string Code(Action a) => Assert.IsAssignableFrom<ScoutException>(Record.Exception(a)).Code;

    // ---------------------------------------------------------------- L10 b: signature2 krävs

    [Fact]
    public void L10_NewClientRequiresSignature2_OldClientsStillAcceptTheSameFile()
    {
        var n = _signer.Release("2026-11-05T10:00:00.000Z");
        // Fullständig fil (som scout-sign.mjs skriver): ny klient OK, gamla 1.0.0–1.0.2 OK.
        Assert.True(ReleaseVerifier.Verify(n.ToJsonString(), _signer.Pem).HasSignature2);
        Assert.Equal("1.0.7", Old.ReleaseVerifier.Verify(n.ToJsonString(), _signer.Pem).Client);

        // Utan signature2 (bara den gamla signaturen): 1.0.7 vägrar – ingen reserv till v1.
        n.Remove("signature2");
        Assert.Equal(ErrorCodes.SignatureMissing, Code(() => ReleaseVerifier.Verify(n.ToJsonString(), _signer.Pem)));
        Assert.Equal("1.0.7", Old.ReleaseVerifier.Verify(n.ToJsonString(), _signer.Pem).Client); // gamla klienter opåverkade
    }

    [Fact]
    public void L10_SignedFieldListsAreUnchanged()
    {
        // Kanoniska fältlistorna får inte ändras – gamla klienter (1.0.2–1.0.6) verifierar mot exakt dessa.
        Assert.Equal(["addon", "addonFile", "addonSha256", "addonSig", "client", "clientFile", "clientSig", "issued", "sha256", "v"], ReleaseVerifier.SignedFields);
        Assert.Equal(Old.ReleaseVerifier.SignedFields, ReleaseVerifier.SignedFields);
        Assert.Equal(18, ReleaseVerifier.SignedFields2.Length);
        Assert.Equal(22, ReleaseVerifier.SignedFields3.Length);
    }

    // ---------------------------------------------------------------- L10 a: "issued" får inte gå bakåt

    [Theory]
    [InlineData("2026-11-05T01:08:54.179Z", true)]   // så skriver scout-sign.mjs (toISOString)
    [InlineData("2026-11-05T01:08:54Z", true)]
    [InlineData("2026-11-05T01:08:54.1+02:00", true)]
    [InlineData("x\"y\\z", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("5 nov 2026", false)]
    public void L10_IssuedFormat(string? issued, bool ok) => Assert.Equal(ok, ReleaseVerifier.TryParseIssued(issued, out _));

    [Fact]
    public void L10_AcceptIssuedIsMonotonic()
    {
        const string a = "2026-11-05T01:00:00.000Z", b = "2026-11-05T02:00:00.000Z";
        Assert.Equal(a, ReleaseVerifier.AcceptIssued(a, null));          // första gången
        Assert.Equal(b, ReleaseVerifier.AcceptIssued(b, a));             // nyare → nytt högsta värde
        Assert.Equal(b, ReleaseVerifier.AcceptIssued(b, b));             // samma fil igen → OK
        Assert.Equal(ErrorCodes.ReleaseStale, Code(() => ReleaseVerifier.AcceptIssued(a, b))); // äldre → avvisas
        Assert.Equal(a, ReleaseVerifier.AcceptIssued(a, "skräp"));       // trasigt sparat värde ignoreras
        Assert.Equal(ErrorCodes.BadResponse, Code(() => ReleaseVerifier.AcceptIssued(null, a)));
        // Samma ögonblick i annan tidszon räknas som samma.
        Assert.Equal(b, ReleaseVerifier.AcceptIssued("2026-11-05T03:00:00.000+01:00", b));
    }

    [Fact]
    public void L10_ReplayedOlderSignedReleaseIsRejectedAndTheFloorSurvivesRestart()
    {
        var dir = Path.Combine(_tmp.FullName, "appdata");
        var older = ReleaseVerifier.Verify(_signer.Release("2026-11-05T01:00:00.000Z").ToJsonString(), _signer.Pem);
        var newer = ReleaseVerifier.Verify(_signer.Release("2026-11-05T02:00:00.000Z").ToJsonString(), _signer.Pem);

        var s = Settings.Load(dir);
        Assert.Same(newer, s.AcceptRelease(newer));
        Assert.Equal("2026-11-05T02:00:00.000Z", s.ReleaseIssued);
        Assert.Equal(ErrorCodes.ReleaseStale, Code(() => s.AcceptRelease(older)));

        // Golvet ligger i settings.json (via SafeFs) och gäller efter omstart.
        var again = Settings.Load(dir);
        Assert.Equal("2026-11-05T02:00:00.000Z", again.ReleaseIssued);
        Assert.Equal(ErrorCodes.ReleaseStale, Code(() => again.AcceptRelease(older)));
        again.AcceptRelease(newer); // samma fil igen är OK
    }

    // ---------------------------------------------------------------- L10 c: ingen nedgradering via osignerade fält

    private static SignedRelease Rel(string? addon, params string[] blocked) => new()
    {
        Addon = addon,
        AddonFile = addon is null ? null : new ReleaseFile(ReleaseKind.Addon, addon, "ForeverScout-addon.zip", new string('a', 64), "sig"),
        BlockedAddons = blocked,
        HasSignature2 = true,
    };

    [Theory]
    [InlineData(null, "1.0.3", "", "", AddonInstaller.UpdateAction.Install)]                    // ingen addon → installera
    [InlineData("1.0.2", "1.0.3", "", "", AddonInstaller.UpdateAction.Install)]                 // signerad nyare → installera
    [InlineData("1.0.3", "1.0.3", "", "", AddonInstaller.UpdateAction.None)]
    [InlineData("1.0.4", "1.0.3", "", "", AddonInstaller.UpdateAction.None)]                    // installerad nyare → rör inte
    [InlineData("1.0.4", "1.0.3", "1.0.4", "", AddonInstaller.UpdateAction.BlockedWaiting)]     // spärrlistan: ALDRIG ner till 1.0.3
    [InlineData("1.0.4", "1.0.3", "", "1.0.4", AddonInstaller.UpdateAction.BlockedWaiting)]     // 422 addon-blocked: samma
    [InlineData("1.0.3", "1.0.3", "1.0.3", "", AddonInstaller.UpdateAction.BlockedWaiting)]     // spärrad och inget nyare
    [InlineData("1.0.2", "1.0.3", "1.0.2", "", AddonInstaller.UpdateAction.Install)]            // spärrad men nyare finns → installera
    [InlineData("1.0.2", "1.0.5", "1.0.5", "", AddonInstaller.UpdateAction.None)]               // släppet självt spärrat
    [InlineData("1.0.4", null, "1.0.4", "", AddonInstaller.UpdateAction.BlockedWaiting)]        // ingen addon i versionsinfon
    public void L10_AddonDecisionNeverDowngrades(string? installed, string? release, string blockedList, string learned, AddonInstaller.UpdateAction expected)
    {
        var r = Rel(release, blockedList.Length > 0 ? [blockedList] : []);
        Assert.Equal(expected, AddonInstaller.Decide(installed, r, learned.Length > 0 ? [learned] : null));
    }

    [Fact]
    public void L10_UnsignedBlockListCannotForceTheSignedOlderAddon()
    {
        // En angripare med kontroll över servern lägger den installerade versionen i blockedAddons (osignerat) och
        // serverar en korrekt signerad, äldre addon. Signaturen godtas – men den äldre addonen installeras inte.
        var n = _signer.Release("2026-11-05T10:00:00.000Z", addon: "1.0.1");
        n["blockedAddons"] = new JsonArray("1.0.3");
        var r = ReleaseVerifier.Verify(n.ToJsonString(), _signer.Pem);
        Assert.True(r.IsAddonBlocked("1.0.3"));
        Assert.Equal(AddonInstaller.UpdateAction.BlockedWaiting, AddonInstaller.Decide("1.0.3", r));
    }

    // ---------------------------------------------------------------- L11 a: automatiskt hittad katalog bekräftas

    private static readonly GameDir Forever = new(@"C:\Games\World of Warcraft\_forever_", "_forever_");
    private static readonly GameDir Other = new(@"D:\WoW\_forever_", "_forever_");

    [Fact]
    public void L11_AutoDetectedFolderMustBeConfirmedOnce()
    {
        var s = new Settings();
        Assert.Equal((WowLocator.StartStep.Confirm, Forever.Path), WowLocator.NextStep(s, _ => true, () => [Forever]));
        s.GameDir = Forever.Path;
        Assert.False(s.IsGameDirConfirmed());
        s.ConfirmGameDir();
        Assert.True(s.IsGameDirConfirmed());
        Assert.Equal((WowLocator.StartStep.Ready, Forever.Path), WowLocator.NextStep(s, _ => true, () => throw new InvalidOperationException("ingen sökning behövs")));
        // Skiftläge spelar ingen roll, en annan katalog kräver ny bekräftelse.
        s.GameDir = Forever.Path.ToUpperInvariant();
        Assert.True(s.IsGameDirConfirmed());
        s.GameDir = Other.Path;
        Assert.False(s.IsGameDirConfirmed());
    }

    [Fact]
    public void L11_FolderSavedByOlderClientIsConfirmedOnce()
    {
        // settings.json från ≤ 1.0.6: GameDir finns, GameDirConfirmed saknas → frågan ställs en gång.
        var s = new Settings { GameDir = Forever.Path };
        Assert.Equal((WowLocator.StartStep.Confirm, Forever.Path), WowLocator.NextStep(s, _ => true, () => []));
    }

    [Fact]
    public void L11_DeclinedFolderIsNotSuggestedAgain()
    {
        var s = new Settings { GameDirDeclined = Forever.Path };
        Assert.Equal((WowLocator.StartStep.Pick, (string?)null), WowLocator.NextStep(s, _ => true, () => [Forever]));
        // … men väljer användaren den själv i Inställningar gäller den.
        s.GameDir = Forever.Path;
        s.ConfirmGameDir();
        Assert.Null(s.GameDirDeclined);
        Assert.Equal(WowLocator.StartStep.Ready, WowLocator.NextStep(s, _ => true, () => []).Step);
    }

    [Fact]
    public void L11_SeveralOrNoCandidatesOpenThePicker()
    {
        Assert.Equal(WowLocator.StartStep.Pick, WowLocator.NextStep(new Settings(), _ => true, () => [Forever, Other]).Step);
        Assert.Equal(WowLocator.StartStep.Pick, WowLocator.NextStep(new Settings(), _ => true, () => []).Step);
        // Sparad katalog som inte finns just nu (t.ex. urkopplad disk): inget att fråga, felet visas i status.
        var gone = new Settings { GameDir = Other.Path, GameDirConfirmed = Other.Path };
        Assert.Equal(WowLocator.StartStep.Nothing, WowLocator.NextStep(gone, _ => false, () => []).Step);
    }

    [Fact]
    public void L11_ConfirmationAndReleaseFloorArePersisted()
    {
        var dir = Path.Combine(_tmp.FullName, "appdata2");
        var s = Settings.Load(dir);
        s.GameDir = Forever.Path;
        s.ConfirmGameDir();
        s.GameDirDeclined = Other.Path;
        s.Save();
        var again = Settings.Load(dir);
        Assert.True(again.IsGameDirConfirmed());
        Assert.Equal(Other.Path, again.GameDirDeclined);
        // settings.json från 1.0.6 (utan de nya fälten) läses som obekräftad.
        // SafeFs.AppDataDir (inte dir): på macOS är /var/folders/… egentligen /private/var/folders/….
        SafeFs.WriteAllText(Path.Combine(SafeFs.AppDataDir, "settings.json"),"{\"GameDir\":\"C:\\\\Games\\\\World of Warcraft\\\\_forever_\"}");
        var legacy = Settings.Load(dir);
        Assert.Equal(Forever.Path, legacy.GameDir);
        Assert.False(legacy.IsGameDirConfirmed());
        Assert.Null(legacy.ReleaseIssued);
    }

    // ---------------------------------------------------------------- L12: omdirigeringar följs aldrig

    [DebugFact]
    public async Task L12_UploadIsNeverReSentToTheRedirectTarget()
    {
        using var srv = new RedirectServer(307);
        using var api = new ApiClient(srv.BaseUrl) { Key = "test-key" };
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.UploadAsync(Encoding.UTF8.GetBytes("ForeverScoutDB = {}")));
        Assert.Equal(ErrorCodes.Redirect, ex.Code);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, ex.Status);
        Assert.False(ex.Transient);
        Assert.False(ex.Rejected);
        Assert.Equal(1, srv.Hits("/api/bidrag/ladda-upp"));
        Assert.Equal(0, srv.Hits("/elsewhere")); // POST-kroppen skickades aldrig vidare
    }

    [DebugTheory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(308)]
    public async Task L12_VersionAndDownloadRefuseRedirects(int status)
    {
        using var srv = new RedirectServer(status);
        using var api = new ApiClient(srv.BaseUrl);
        Assert.Equal(ErrorCodes.Redirect, (await Assert.ThrowsAsync<ApiException>(() => api.VersionAsync(_signer.Pem))).Code);
        var rel = ReleaseVerifier.Verify(_signer.Release("2026-11-05T10:00:00.000Z").ToJsonString(), _signer.Pem);
        Assert.Equal(ErrorCodes.Redirect, (await Assert.ThrowsAsync<ApiException>(() => api.DownloadReleaseFileAsync(rel, ReleaseKind.Addon, _signer.Pem, 1024))).Code);
        Assert.Equal(ErrorCodes.Redirect, (await Assert.ThrowsAsync<ApiException>(() => api.RegisterAsync("Scout"))).Code);
        Assert.Equal(0, srv.Hits("/elsewhere"));
    }

    [Fact]
    public async Task L12_RedirectFromACustomHandlerIsAnErrorToo()
    {
        using var api = new ApiClient("http://127.0.0.1:9", new StaticHandler(HttpStatusCode.PermanentRedirect)) { Key = "k" };
        Assert.Equal(ErrorCodes.Redirect, (await Assert.ThrowsAsync<ApiException>(() => api.MeAsync())).Code);
    }

    [Fact]
    public void L12_RedirectAndStaleHaveTexts()
    {
        foreach (var code in new[] { ErrorCodes.Redirect, ErrorCodes.ReleaseStale })
        {
            Assert.True(Strings.All.TryGetValue("err." + code, out var t));
            Assert.False(string.IsNullOrWhiteSpace(t.Sv));
            Assert.False(string.IsNullOrWhiteSpace(t.En));
        }
        foreach (var key in new[] { "gamedir.confirm", "gamedir.confirm.title", "mac.gamedir.confirm", "addon.blocked.wait" })
            Assert.True(Strings.Has(key), key);
    }

    private sealed class StaticHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var r = new HttpResponseMessage(status);
            r.Headers.Location = new Uri("https://evil.example/x");
            return Task.FromResult(r);
        }
    }

    /// <summary>
    /// Minimal HTTP/1.1-server på 127.0.0.1 som svarar med en omdirigering till /elsewhere på allt utom /elsewhere.
    /// Riktiga sockets, så att ApiClient:s egen SocketsHttpHandler (AllowAutoRedirect = false) provas.
    /// </summary>
    private sealed class RedirectServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Dictionary<string, int> _hits = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly int _status;

        public RedirectServer(int status)
        {
            _status = status;
            _listener.Start();
            _ = Task.Run(Loop);
        }

        public string BaseUrl => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Hits(string path) { lock (_hits) return _hits.TryGetValue(path, out var n) ? n : 0; }

        private async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(_stop.Token); } catch { return; }
                _ = Task.Run(() => Serve(c));
            }
        }

        private async Task Serve(TcpClient c)
        {
            using (c)
            {
                try
                {
                    var s = c.GetStream();
                    var head = new StringBuilder();
                    var one = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await s.ReadAsync(one) == 1) head.Append((char)one[0]);
                    var lines = head.ToString().Split("\r\n");
                    var path = lines[0].Split(' ')[1];
                    int len = lines.Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Select(l => int.Parse(l[15..].Trim())).FirstOrDefault();
                    var body = new byte[len];
                    for (int got = 0; got < len;) { int n = await s.ReadAsync(body.AsMemory(got)); if (n == 0) break; got += n; }
                    lock (_hits) _hits[path] = (_hits.TryGetValue(path, out var h) ? h : 0) + 1;
                    var resp = path == "/elsewhere"
                        ? "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"
                        : $"HTTP/1.1 {_status} Moved\r\nLocation: {BaseUrl}/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await s.WriteAsync(Encoding.ASCII.GetBytes(resp));
                }
                catch (IOException) { }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}

/// <summary>Engångsnyckel som signerar versionsinfo i exakt scout-sign.mjs-formatet (signature + signature2).</summary>
internal sealed class TestSigner : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string Pem => _key.ExportSubjectPublicKeyInfoPem();

    public string Sign(byte[] data) =>
        Convert.ToBase64String(_key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public JsonObject Release(string issued, string addon = "1.0.5")
    {
        var exeSha = ApiClient.Sha256Hex(Encoding.ASCII.GetBytes("MZ exe"));
        var zipSha = ApiClient.Sha256Hex(Encoding.ASCII.GetBytes("PK addon " + addon));
        var n = new JsonObject
        {
            ["v"] = 1, ["client"] = "1.0.7", ["clientFile"] = "ForeverScout.exe", ["sha256"] = exeSha,
            ["clientSig"] = Sign(ReleaseVerifier.FileMessage(ReleaseKind.Client, "1.0.7", exeSha)),
            ["addon"] = addon, ["addonFile"] = "ForeverScout-addon.zip", ["addonSha256"] = zipSha,
            ["addonSig"] = Sign(ReleaseVerifier.FileMessage(ReleaseKind.Addon, addon, zipSha)),
            ["issued"] = issued,
        };
        using var doc = JsonDocument.Parse(n.ToJsonString());
        n["blockedAddons"] = new JsonArray();
        n["signature"] = Sign(Encoding.UTF8.GetBytes(ReleaseVerifier.Canonical(doc.RootElement, ReleaseVerifier.SignedFields)));
        n["signature2"] = Sign(Encoding.UTF8.GetBytes(ReleaseVerifier.Canonical(doc.RootElement, ReleaseVerifier.SignedFields2)));
        return n;
    }

    public void Dispose() => _key.Dispose();
}
