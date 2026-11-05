using System.Net;

namespace ForeverScout.Core;

/// <summary>Hur "Logga in med webbläsaren" slutade.</summary>
public enum DeviceLinkOutcome
{
    /// <summary>Godkänd på webben – nyckeln är hämtad (och sparad om <see cref="DeviceLink.OnApproved"/> är satt).</summary>
    Approved,
    /// <summary>Personen klickade Avbryt på webben.</summary>
    Denied,
    /// <summary>Koden gick ut (10 min) eller var okänd för servern.</summary>
    Expired,
    /// <summary>Kontot har redan max antal aktiva nycklar.</summary>
    TooManyKeys,
    /// <summary>Avbrutet i appen (CancellationToken).</summary>
    Cancelled,
    /// <summary>Servern skickade en sida utanför https://guide.wow4ever.se/ – den öppnades inte.</summary>
    BadUrl,
    /// <summary>Nät- eller serverfel, se <see cref="DeviceLinkResult.Error"/>.</summary>
    Failed,
}

/// <summary>Resultatet. <see cref="Contributor"/> bara vid Approved, <see cref="Error"/> bara vid Failed.</summary>
public sealed record DeviceLinkResult(DeviceLinkOutcome Outcome, Contributor? Contributor = null, Exception? Error = null);

/// <summary>Det appen visar medan den väntar: koden (XXXX-XXXX) och den kontrollerade sidan. Ingen deviceCode här.</summary>
public sealed record DeviceLinkTicket(string UserCode, Uri Url, TimeSpan ExpiresIn, TimeSpan Interval)
{
    public string DisplayCode => DeviceLink.FormatCode(UserCode);
}

/// <summary>
/// "Logga in med webbläsaren" (kontraktet 2b, enhetsflöde i stil med RFC 8628): hämtar en kod, låter appen visa den och
/// öppna sidan, frågar sedan var <c>interval</c>:e sekund tills personen godkänt eller nekat, koden gått ut
/// (<c>expiresIn</c>) eller appen avbryter. 429 med Retry-After = vänta längre och fortsätt.
/// <para>Sidan öppnas bara om den börjar exakt med https://guide.wow4ever.se/ (Debug: även appens loopback-basadress),
/// se <see cref="IsAllowedUrl"/>.</para>
/// </summary>
public sealed class DeviceLink(ApiClient api, string platform, string version)
{
    /// <summary>Den enda sida som får öppnas i Release-byggen.</summary>
    public const string SitePrefix = "https://guide.wow4ever.se/";
    /// <summary>Tecknen i userCode (kontraktet 2b): inga I, O, 0, 1.</summary>
    public const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxExpiry = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Körs när servern svarat "approved", innan RunAsync returnerar. Appen sparar nyckeln här
    /// (<see cref="ContributorStore.Save"/>). Kastar den blir resultatet Failed med undantaget som fel.
    /// </summary>
    public Action<Contributor>? OnApproved { get; init; }

    /// <summary>Väntan mellan förfrågningarna (tester byter ut den mot en låtsasklocka).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>"K7QM4TRX" → "K7QM-4TRX".</summary>
    public static string FormatCode(string userCode) =>
        userCode.Length == 8 ? userCode[..4] + "-" + userCode[4..] : userCode;

    /// <summary>
    /// Får appen öppna <paramref name="url"/>? Release: bara om den börjar exakt med https://guide.wow4ever.se/ (värden,
    /// porten och schemat kontrolleras dessutom efter tolkning, ingen användarinfo, inga blanktecken eller styrtecken).
    /// Debug: även appens egen loopback-basadress (<paramref name="baseUri"/>, t.ex. http://127.0.0.1:3000).
    /// </summary>
    public static bool IsAllowedUrl(string? url, Uri baseUri)
    {
        if (string.IsNullOrEmpty(url) || url.Length > 2048) return false;
        foreach (var ch in url)
            if (ch <= ' ' || ch == '\\' || char.IsControl(ch) || char.IsWhiteSpace(ch)) return false;
        if (SameOrigin(url, SitePrefix)) return true;
#if DEBUG
        if (baseUri.IsLoopback && (baseUri.Scheme == Uri.UriSchemeHttp || baseUri.Scheme == Uri.UriSchemeHttps))
            return SameOrigin(url, baseUri.GetLeftPart(UriPartial.Authority) + "/");
#endif
        return false;
    }

    /// <summary>Exakt prefix (skiftlägeskänsligt) och samma schema/värd/port efter tolkning, utan användarinfo.</summary>
    private static bool SameOrigin(string url, string prefix)
    {
        if (!url.StartsWith(prefix, StringComparison.Ordinal)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !Uri.TryCreate(prefix, UriKind.Absolute, out var p)) return false;
        return u.Scheme == p.Scheme && u.Host == p.Host && u.Port == p.Port && u.UserInfo.Length == 0
               && u.GetLeftPart(UriPartial.Authority) == p.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>
    /// Kör hela flödet. <paramref name="started"/> anropas en gång med koden och den kontrollerade sidan – där visar appen
    /// koden och öppnar webbläsaren. Kastar aldrig för nät-/serverfel eller avbrott; allt blir ett <see cref="DeviceLinkResult"/>.
    /// </summary>
    public async Task<DeviceLinkResult> RunAsync(Action<DeviceLinkTicket> started, CancellationToken ct = default)
    {
        try
        {
            var start = await api.DeviceStartAsync(platform, version, ct);
            if (!ValidCode(start.UserCode) || start.DeviceCode is not { Length: > 0 and <= 512 } deviceCode)
                return Fail(new ApiException(ErrorCodes.BadResponse));
            if (!IsAllowedUrl(start.Url, api.BaseUri)) return new DeviceLinkResult(DeviceLinkOutcome.BadUrl);

            var interval = start.Interval is int i and > 0 ? TimeSpan.FromSeconds(Math.Min(i, MaxWait.TotalSeconds)) : DefaultInterval;
            var expiresIn = start.ExpiresIn is int e and > 0 ? TimeSpan.FromSeconds(Math.Min(e, MaxExpiry.TotalSeconds)) : TimeSpan.FromMinutes(10);
            var deadline = Now() + expiresIn;
            started(new DeviceLinkTicket(start.UserCode!, new Uri(start.Url!), expiresIn, interval));

            var wait = interval;
            ApiException? lastError = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var left = deadline - Now();
                // Tiden är ute. Misslyckades senaste frågan (nätet borta) är det felet som visas, inte "gick ut".
                if (left <= TimeSpan.Zero) return lastError is null ? new DeviceLinkResult(DeviceLinkOutcome.Expired) : Fail(lastError);
                await Delay(wait < left ? wait : left, ct);

                DeviceStatusResult status;
                try { status = await api.DeviceStatusAsync(deviceCode, ct); }
                catch (ApiException ex) when (ex.Transient)
                {
                    // 429: vänta minst Retry-After (annars dubbla intervallet). Nätfel och 5xx: samma sak, fortsätt.
                    lastError = ex;
                    wait = Clamp(ex.RetryAfter ?? interval * 2, interval);
                    continue;
                }
                lastError = null;
                wait = interval;
                switch (status.Status)
                {
                    case "pending": continue;
                    case "denied": return new DeviceLinkResult(DeviceLinkOutcome.Denied);
                    case "expired": return new DeviceLinkResult(DeviceLinkOutcome.Expired);
                    case "too-many-keys": return new DeviceLinkResult(DeviceLinkOutcome.TooManyKeys);
                    case "approved":
                        if (status.ToContributor() is not { } who) return Fail(new ApiException(ErrorCodes.BadResponse, HttpStatusCode.OK));
                        // Kunde nyckeln inte sparas (DPAPI, nyckelringen, disken) är det inget godkännande för appen.
                        try { OnApproved?.Invoke(who); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail(ex); }
                        return new DeviceLinkResult(DeviceLinkOutcome.Approved, who);
                    default:
                        return Fail(new ApiException(ErrorCodes.BadResponse, HttpStatusCode.OK));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new DeviceLinkResult(DeviceLinkOutcome.Cancelled); }
        catch (ScoutException ex) { return Fail(ex); }
    }

    private static DeviceLinkResult Fail(Exception ex) => new(DeviceLinkOutcome.Failed, Error: ex);

    private static bool ValidCode(string? c) => c is { Length: 8 } && c.All(ch => CodeAlphabet.Contains(ch));

    private static TimeSpan Clamp(TimeSpan t, TimeSpan min) => t < min ? min : t > MaxWait ? MaxWait : t;
}

/// <summary>Sparar en koppling – samma väg för visningsnamn, kod och webbläsaren.</summary>
public static class ContributorStore
{
    /// <summary>
    /// Nyckeln via <paramref name="saveKey"/> (Windows: DPAPI-filen, macOS: nyckelringen), sedan i API-klienten, och id/namn
    /// i inställningarna. Byts kontot nollställs poäng, plats och senast bekräftat – de hörde till det gamla kontot.
    /// </summary>
    public static void Save(Contributor who, ApiClient api, Settings settings, Action<string> saveKey)
    {
        saveKey(who.Key);
        api.Key = who.Key;
        if (!string.Equals(settings.ContributorId, who.Id, StringComparison.Ordinal))
        {
            settings.Points = settings.Rank = null;
            settings.AcceptedTotal = settings.ConfirmedTotal = null;
            settings.LastConfirmed = new();
        }
        settings.ContributorId = who.Id;
        settings.ContributorName = who.Name;
        settings.Save();
    }
}
