using System.Net;

namespace ForeverScout.Core;

/// <summary>
/// Felkoder. Core visar aldrig text för användaren: allt som kan nå gränssnittet bär en kod som
/// <see cref="Strings"/> översätter (nyckel "err.&lt;kod&gt;", sv + en). Testerna kräver att varje kod har en text.
/// </summary>
public static class ErrorCodes
{
    public const string Network = "network";
    public const string Timeout = "timeout";
    public const string Http = "http";
    public const string NoKey = "no-key";
    public const string EmptyResponse = "empty-response";
    public const string BadResponse = "bad-response";
    public const string UploadTooLarge = "upload-too-large";
    public const string HotfixTooLarge = "hotfix-too-large";
    public const string DownloadTooLarge = "download-too-large";
    public const string OtherHost = "other-host";
    public const string BadFileName = "bad-file-name";
    public const string ShaMissing = "sha-missing";
    public const string ShaMismatch = "sha-mismatch";
    public const string SignatureMissing = "signature-missing";
    public const string SignatureInvalid = "signature-invalid";
    public const string NoPublicKey = "no-public-key";
    public const string NotNewer = "not-newer";
    public const string NoRelease = "no-release";
    public const string AddonMissing = "addon-missing";
    public const string AddonTooLarge = "addon-too-large";
    public const string AddonInvalid = "addon-invalid";
    public const string AddonBlocked = "addon-blocked";
    public const string LuaParse = "lua-parse";
    public const string FileTooLarge = "file-too-large";
    public const string Io = "io";
    public const string ExePath = "exe-path";
    public const string AccessDenied = "access-denied";
    public const string NotOurFile = "not-our-file";
    public const string UpdateFailed = "update-failed";

    /// <summary>Alla koder (används av testet som kräver sv- och en-text för varje).</summary>
    public static IEnumerable<string> All =>
        typeof(ErrorCodes).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!);

    /// <summary>Felkoder som servern skickar i fältet "code" (lib/scout/http.ts) och som vi översätter.</summary>
    public static readonly string[] Server =
    [
        "origin", "rate-limited", "too-large", "invalid", "name-length", "name-chars", "name-reserved", "name-taken",
        "unauthorized", "bad-code", "bad-file", "addon-blocked", "not-dbcache", "unavailable", "server",
    ];
}

/// <summary>Fel med kod (och ev. en språkneutral detalj, t.ex. radnummer eller HTTP-status).</summary>
public class ScoutException(string code, string? detail = null, Exception? inner = null)
    : Exception(detail is null ? code : $"{code}: {detail}", inner)
{
    public string Code { get; } = code;
    public string? Detail { get; } = detail;
}
