using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForeverScout.Core;

public sealed record Contributor(string Id, string Key, string Name);

public sealed class UploadResult
{
    [JsonPropertyName("accepted")] public Dictionary<string, int>? Accepted { get; set; }
    [JsonPropertyName("newConfirmed")] public List<ConfirmedItem>? NewConfirmed { get; set; }
    [JsonPropertyName("points")] public int? Points { get; set; }
    [JsonPropertyName("rank")] public int? Rank { get; set; }
    [JsonPropertyName("lastSeq")] public long? LastSeq { get; set; }
}

public sealed class HotfixResult
{
    [JsonPropertyName("stored")] public bool Stored { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
}

public sealed class MeResult
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("points")] public int? Points { get; set; }
    [JsonPropertyName("rank")] public int? Rank { get; set; }
    [JsonPropertyName("counts")] public JsonElement? Counts { get; set; }
    [JsonPropertyName("recent")] public List<JsonElement>? Recent { get; set; }
    /// <summary>Antal bekräftade poster (≥ 2 rapportörer). Finns bara om servern skickar fältet – annars null ("–").</summary>
    [JsonPropertyName("confirmed")] public int? Confirmed { get; set; }
    [JsonPropertyName("uploads")] public int? Uploads { get; set; }
    [JsonPropertyName("lastUpload")] public long? LastUpload { get; set; }

    /// <summary>Summan av "counts" för datatyperna (poster som servern godtagit, inte samma sak som bekräftade).</summary>
    public int? AcceptedTotal()
    {
        if (Counts is not { ValueKind: JsonValueKind.Object } c) return null;
        int sum = 0;
        foreach (var k in new[] { "npcs", "trainers", "vendors", "quests", "objects", "loot", "flights" })
            if (c.TryGetProperty(k, out var v) && v.TryGetInt32(out var n)) sum += n;
        return sum;
    }
}

/// <summary>API-/nätfel. <see cref="ScoutException.Code"/> är vår kod eller serverns "code"-fält.</summary>
public sealed class ApiException(string code, HttpStatusCode? status = null, string? detail = null, bool transient = false)
    : ScoutException(code, detail ?? (status is null ? null : ((int)status).ToString()))
{
    public HttpStatusCode? Status { get; } = status;
    /// <summary>Kom koden från servern (fältet "code" i felsvaret)?</summary>
    public bool FromServer { get; init; }
    /// <summary>
    /// Serverns förklaring (fältet "detail", t.ex. "Okänd eller saknad klientbuild"), rensad och kortad. Visas för
    /// användaren efter vår egen översatta text. Kommer från servern på svenska.
    /// </summary>
    public string? ServerDetail { get; init; }
    /// <summary>Servern avvisade själva förfrågan (4xx med kod) – ett nytt försök med samma fil hjälper inte förrän
    /// filen eller servern ändrats, men filen räknas aldrig som uppladdad.</summary>
    public bool Rejected => FromServer && Status is { } s && (int)s >= 400 && (int)s < 500 && !Transient;
    /// <summary>Värt att försöka igen senare (nätfel, 5xx, 429).</summary>
    public bool Transient => transient || (Status is { } s && ((int)s >= 500 || s == HttpStatusCode.TooManyRequests || s == HttpStatusCode.RequestTimeout));
}

/// <summary>Anropar /api/bidrag/* – bara HTTPS mot guide.wow4ever.se (bas-URL kan bytas med FOREVERSCOUT_BASEURL för test).</summary>
public sealed class ApiClient : IDisposable
{
    public const string DefaultBaseUrl = "https://guide.wow4ever.se";
    public const long MaxUploadBytes = 5 * 1024 * 1024;
    public const long MaxHotfixBytes = 30 * 1024 * 1024;

    private readonly HttpClient _http;
    public Uri BaseUri { get; }
    public string? Key { get; set; }

    /// <summary>Enda värden appen pratar med (i Release-byggen kan den inte bytas).</summary>
    public const string Host = "guide.wow4ever.se";

    /// <summary>"ForeverScout/&lt;version&gt;" – inget annat om datorn skickas.</summary>
    public static string UserAgent => "ForeverScout/" + (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0)).ToString(3);

    public ApiClient(string? baseUrl = null, HttpMessageHandler? handler = null)
    {
#if DEBUG
        BaseUri = ResolveBaseUri(baseUrl ?? Environment.GetEnvironmentVariable("FOREVERSCOUT_BASEURL"));
#else
        BaseUri = ResolveBaseUri(null); // Release: FOREVERSCOUT_BASEURL och baseUrl ignoreras
#endif
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, UseCookies = false, UseProxy = true })
            : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(120);
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    /// <summary>
    /// Release: alltid https://guide.wow4ever.se. Debug (test): HTTPS-adress eller http mot localhost från FOREVERSCOUT_BASEURL.
    /// </summary>
    public static Uri ResolveBaseUri(string? raw)
    {
#if !DEBUG
        return new Uri(DefaultBaseUrl);
#else
        if (string.IsNullOrWhiteSpace(raw)) return new Uri(DefaultBaseUrl);
        if (!Uri.TryCreate(raw.Trim().TrimEnd('/'), UriKind.Absolute, out var u)) return new Uri(DefaultBaseUrl);
        if (u.Scheme == Uri.UriSchemeHttps) return u;
        if (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback) return u;
        return new Uri(DefaultBaseUrl);
#endif
    }

    public Uri Site(string path) => new(BaseUri, path);

    private HttpRequestMessage Req(HttpMethod m, string path, bool auth)
    {
        var r = new HttpRequestMessage(m, new Uri(BaseUri, path));
        if (auth)
        {
            if (string.IsNullOrEmpty(Key)) throw new ApiException(ErrorCodes.NoKey);
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        }
        return r;
    }

    private async Task<T> Send<T>(HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, ct); }
        catch (HttpRequestException) { throw new ApiException(ErrorCodes.Network, transient: true); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new ApiException(ErrorCodes.Timeout, transient: true); }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode) throw await ErrorFrom(resp, ct);
            try { return await resp.Content.ReadFromJsonAsync<T>(ct) ?? throw new ApiException(ErrorCodes.EmptyResponse, resp.StatusCode); }
            catch (JsonException) { throw new ApiException(ErrorCodes.BadResponse, resp.StatusCode); }
        }
    }

    /// <summary>Felsvar → ApiException med serverns kod ("code") om den finns, annars "http".</summary>
    private static async Task<ApiException> ErrorFrom(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 and < 64 } code)
                return new ApiException(code, resp.StatusCode) { FromServer = true, ServerDetail = CleanDetail(doc.RootElement) };
        }
        catch { }
        return new ApiException(ErrorCodes.Http, resp.StatusCode);
    }

    /// <summary>"detail" som en enda rad utan styrtecken, högst 200 tecken.</summary>
    internal static string? CleanDetail(JsonElement root)
    {
        if (!root.TryGetProperty("detail", out var d) || d.ValueKind != JsonValueKind.String) return null;
        var t = new string((d.GetString() ?? "").Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray()).Trim();
        while (t.Contains("  ", StringComparison.Ordinal)) t = t.Replace("  ", " ", StringComparison.Ordinal);
        if (t.Length == 0) return null;
        return t.Length > 200 ? t[..200] + "…" : t;
    }

    private sealed class ContributorDto
    {
        [JsonPropertyName("id")] public JsonElement Id { get; set; }
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        public Contributor ToContributor() => new(Id.ToString(), Key ?? throw new ApiException(ErrorCodes.BadResponse), Name ?? "");
    }

    public async Task<Contributor> RegisterAsync(string name, CancellationToken ct = default)
    {
        var r = Req(HttpMethod.Post, "/api/bidrag/registrera", false);
        r.Content = JsonContent.Create(new { name });
        return (await Send<ContributorDto>(r, ct)).ToContributor();
    }

    public async Task<Contributor> LinkAsync(string code, CancellationToken ct = default)
    {
        var r = Req(HttpMethod.Post, "/api/bidrag/koppla", false);
        r.Content = JsonContent.Create(new { code = code.Trim() });
        return (await Send<ContributorDto>(r, ct)).ToContributor();
    }

    public async Task<UploadResult> UploadAsync(byte[] luaText, CancellationToken ct = default)
    {
        if (luaText.LongLength > MaxUploadBytes) throw new ApiException(ErrorCodes.UploadTooLarge);
        var r = Req(HttpMethod.Post, "/api/bidrag/ladda-upp", true);
        var content = new ByteArrayContent(Gzip(luaText));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
        content.Headers.ContentEncoding.Add("gzip");
        r.Content = content;
        return await Send<UploadResult>(r, ct);
    }

    public async Task<HotfixResult> UploadHotfixAsync(byte[] dbcache, CancellationToken ct = default)
    {
        if (dbcache.LongLength > MaxHotfixBytes) throw new ApiException(ErrorCodes.HotfixTooLarge);
        var r = Req(HttpMethod.Post, "/api/bidrag/hotfix", true);
        var content = new ByteArrayContent(dbcache);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        r.Content = content;
        return await Send<HotfixResult>(r, ct);
    }

    public Task<MeResult> MeAsync(CancellationToken ct = default) => Send<MeResult>(Req(HttpMethod.Get, "/api/bidrag/jag", true), ct);

    /// <summary>Hämtar versionsinfo och verifierar dess signatur. Saknad eller felaktig signatur = fel, ingen reserv.</summary>
    public async Task<SignedRelease> VersionAsync(string publicKeyPem, CancellationToken ct = default)
    {
        var json = await GetStringAsync("/api/bidrag/version", 256 * 1024, ct);
        return ReleaseVerifier.Verify(json, publicKeyPem);
    }

    /// <summary>
    /// macOS: samma versionsinfo, men verifierad med signature3 (<see cref="ReleaseVerifier.VerifyMac"/>) så att
    /// mac-fälten (macFile, macSha256, macSig) är signerade. Saknas signature3 blir det ett fel, ingen reserv.
    /// </summary>
    public async Task<SignedRelease> MacVersionAsync(string publicKeyPem, CancellationToken ct = default)
    {
        var json = await GetStringAsync("/api/bidrag/version", 256 * 1024, ct);
        return ReleaseVerifier.VerifyMac(json, publicKeyPem);
    }

    private async Task<string> GetStringAsync(string path, int maxBytes, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(Req(HttpMethod.Get, path, false), ct); }
        catch (HttpRequestException) { throw new ApiException(ErrorCodes.Network, transient: true); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new ApiException(ErrorCodes.Timeout, transient: true); }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode) throw await ErrorFrom(resp, ct);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > maxBytes) throw new ApiException(ErrorCodes.BadResponse);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>
    /// Laddar ner en släppt fil från /scout/&lt;fil&gt; på sajtens egen värd och kontrollerar den mot den signerade
    /// versionsinfon: SHA-256 mot det signerade värdet och filsignaturen över (typ, version, sha256).
    /// </summary>
    public async Task<byte[]> DownloadReleaseFileAsync(SignedRelease release, ReleaseKind kind, string publicKeyPem, long maxBytes, CancellationToken ct = default)
    {
        var f = release.File(kind) ?? throw new ApiException(ErrorCodes.NoRelease);
        if (!ReleaseVerifier.SafeFileName(f.FileName)) throw new ApiException(ErrorCodes.BadFileName);
        var uri = new Uri(BaseUri, "/scout/" + f.FileName);
        if (!string.Equals(uri.Host, BaseUri.Host, StringComparison.OrdinalIgnoreCase) || uri.Scheme != BaseUri.Scheme)
            throw new ApiException(ErrorCodes.OtherHost);
        HttpResponseMessage resp;
        try { resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException) { throw new ApiException(ErrorCodes.Network, transient: true); }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode) throw new ApiException(ErrorCodes.Http, resp.StatusCode);
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = await s.ReadAsync(buf, ct)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > maxBytes) throw new ApiException(ErrorCodes.DownloadTooLarge);
            }
            var data = ms.ToArray();
            ReleaseVerifier.VerifyFile(data, kind, f, publicKeyPem);
            return data;
        }
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Sha256Hex(string path) => Sha256Hex(SafeFs.ReadAllBytes(path));

    public static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(data);
        return ms.ToArray();
    }

    public void Dispose() => _http.Dispose();
}
