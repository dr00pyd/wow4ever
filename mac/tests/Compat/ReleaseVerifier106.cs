// ORDAGRANN KOPIA av client/windows/src/ForeverScout.Core/ReleaseVerifier.cs i klient 1.0.6 (commit 18ca1b9, "Forever
// Scout 1.0.6"), den version som Windows-användarna kör när macOS-stödet läggs till. Bara namnrymden är ändrad så att
// den kan ligga bredvid den nya. Används av MacReleaseTests för att bevisa att Windows-klienterna 1.0.3–1.0.6 godtar en
// version.json med mac-fält och signature3 och fortfarande får samma filer. Ändra ALDRIG den här filen – återskapa med:
// git show 18ca1b9:client/windows/src/ForeverScout.Core/ReleaseVerifier.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using ForeverScout.Core;

namespace ForeverScout.Mac.Tests.Compat106;

/// <summary>Client = fristående (full) exe, Lite = ramverksberoende exe (kräver .NET 10 Desktop Runtime), Addon = addon-zip.</summary>
public enum ReleaseKind { Client, Addon, Lite }

public sealed record ReleaseFile(ReleaseKind Kind, string Version, string FileName, string Sha256, string Signature);

/// <summary>Verifierad versionsinfo från /api/bidrag/version.</summary>
public sealed class SignedRelease
{
    public string? Client { get; init; }
    public string? Addon { get; init; }
    public ReleaseFile? ClientFile { get; init; }
    public ReleaseFile? AddonFile { get; init; }
    /// <summary>Lite-varianten (bara från signature2-formatet; null annars).</summary>
    public string? Lite { get; init; }
    public ReleaseFile? LiteFile { get; init; }
    public string? Issued { get; init; }
    /// <summary>Verifierades svaret med signature2 (utökat fältset) eller bara den gamla signaturen?</summary>
    public bool HasSignature2 { get; init; }
    /// <summary>Spärrade addonversioner. Inte signerat (servern kan lägga till via miljövariabel) – används bara
    /// för att avgöra när en signerad addon ska hämtas, aldrig för att godta något.</summary>
    public IReadOnlyList<string> BlockedAddons { get; init; } = [];

    public ReleaseFile? File(ReleaseKind k) => k switch { ReleaseKind.Client => ClientFile, ReleaseKind.Lite => LiteFile, _ => AddonFile };
    public string? VersionOf(ReleaseKind k) => k switch { ReleaseKind.Client => Client, ReleaseKind.Lite => Lite, _ => Addon };
    public bool IsAddonBlocked(string? version) => version != null && BlockedAddons.Contains(version);
}

/// <summary>
/// Signerade släpp (ECDSA P-256 / SHA-256, signatur i IEEE P1363-format r||s, base64).
///
/// <para><b>Versionssignaturen</b> (<c>signature</c>) gäller den kanoniska JSON:en av exakt fälten i
/// <see cref="SignedFields"/> – nycklarna i bokstavsordning, utan blanksteg, saknat fält = <c>null</c>, värdena
/// serialiserade som i <c>JSON.stringify</c>. Övriga fält i svaret (clientUrl, addonUrl, blockedAddons …) ingår inte.</para>
///
/// <para><b>signature2</b> (från 1.0.3) gäller samma kanoniska form men över <see cref="SignedFields2"/> = de gamla fälten
/// plus <c>lite, liteFile, liteSha256, liteSig</c> och <c>setup, setupFile, setupSha256, setupSig</c>. Den gamla <c>signature</c> lämnas orörd så att klienter 1.0.0–1.0.2
/// (som bara känner till <see cref="SignedFields"/>) fortsätter att verifiera och uppdatera till full-exe:n.
/// Nya klienter: finns signature2 MÅSTE den stämma (ingen reserv till den gamla); saknas den används den gamla
/// signaturen och lite-fälten ignoreras.</para>
///
/// <para><b>Filsignaturen</b> (<c>clientSig</c>/<c>liteSig</c>/<c>addonSig</c>) gäller UTF-8-texten
/// <c>"ForeverScout-file/v1\n&lt;client|lite|addon&gt;\n&lt;version&gt;\n&lt;sha256 hex gemener&gt;"</c>, så att en fil
/// inte kan utges för en annan typ eller version. Samma format i <c>scripts/scout-sign.mjs</c>.</para>
/// </summary>
public static partial class ReleaseVerifier
{
    public static readonly string[] SignedFields =
        ["addon", "addonFile", "addonSha256", "addonSig", "client", "clientFile", "clientSig", "issued", "sha256", "v"];

    /// <summary>
    /// Fälten för signature2: de gamla plus lite- och setup-fälten, i bokstavsordning. Setup (installationsprogrammet)
    /// används inte av klienten, men ingår i signaturen så att nedladdningssidans uppgifter är signerade.
    /// </summary>
    public static readonly string[] SignedFields2 =
        ["addon", "addonFile", "addonSha256", "addonSig", "client", "clientFile", "clientSig", "issued", "lite", "liteFile", "liteSha256", "liteSig",
         "setup", "setupFile", "setupSha256", "setupSig", "sha256", "v"];

    public const int FormatVersion = 1;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,80}$")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"^\d{1,4}(\.\d{1,6}){0,3}([-+][0-9A-Za-z.]{1,20})?$")]
    private static partial Regex VersionRx();

    [GeneratedRegex(@"^[0-9a-f]{64}$")]
    private static partial Regex Sha256Rx();

    public static bool SafeFileName(string? n) => n != null && SafeName().IsMatch(n);

    /// <summary>Finns en riktig publik nyckel inbyggd (inte platshållaren)?</summary>
    public static bool HasKey(string? pem) => pem != null && pem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal);

    public static string Canonical(JsonElement root) => Canonical(root, SignedFields);

    public static string Canonical(JsonElement root, IReadOnlyList<string> fields)
    {
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var name in fields)
        {
            if (!first) sb.Append(',');
            first = false;
            AppendString(sb, name);
            sb.Append(':');
            if (!root.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) { sb.Append("null"); continue; }
            switch (v.ValueKind)
            {
                case JsonValueKind.String: AppendString(sb, v.GetString()!); break;
                case JsonValueKind.Number when v.TryGetInt64(out var l): sb.Append(l.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
                default: throw new ApiException(ErrorCodes.SignatureInvalid, detail: name);
            }
        }
        return sb.Append('}').ToString();
    }

    /// <summary>Som JSON.stringify för strängar.</summary>
    private static void AppendString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || char.IsSurrogate(c)) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    public static byte[] FileMessage(ReleaseKind kind, string version, string sha256) =>
        Encoding.UTF8.GetBytes($"ForeverScout-file/v1\n{KindName(kind)}\n{version}\n{sha256.ToLowerInvariant()}");

    public static string KindName(ReleaseKind kind) => kind switch { ReleaseKind.Client => "client", ReleaseKind.Lite => "lite", _ => "addon" };

    private static ECDsa Key(string publicKeyPem)
    {
        if (!HasKey(publicKeyPem)) throw new ApiException(ErrorCodes.NoPublicKey);
        var k = ECDsa.Create();
        k.ImportFromPem(publicKeyPem);
        if (k.KeySize != 256) throw new ApiException(ErrorCodes.NoPublicKey);
        return k;
    }

    private static bool VerifySig(ECDsa key, byte[] data, string? b64)
    {
        if (string.IsNullOrWhiteSpace(b64)) return false;
        byte[] sig;
        try { sig = Convert.FromBase64String(b64.Trim()); } catch (FormatException) { return false; }
        if (sig.Length != 64) return false;
        return key.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Kontrollerar versionsinfons signatur och returnerar dess innehåll. Kastar vid saknad/ogiltig signatur.</summary>
    public static SignedRelease Verify(string json, string publicKeyPem)
    {
        using var key = Key(publicKeyPem);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException) { throw new ApiException(ErrorCodes.BadResponse); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ApiException(ErrorCodes.BadResponse);
            static string? SigOf(JsonElement r, string n) =>
                r.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()) ? e.GetString() : null;
            bool v2 = SigOf(root, "signature2") is not null;
            if (v2)
            {
                // Nytt format: signature2 över det utökade fältsetet måste stämma. Ingen reserv till den gamla.
                if (!VerifySig(key, Encoding.UTF8.GetBytes(Canonical(root, SignedFields2)), SigOf(root, "signature2")))
                    throw new ApiException(ErrorCodes.SignatureInvalid);
            }
            else
            {
                var old = SigOf(root, "signature") ?? throw new ApiException(ErrorCodes.SignatureMissing);
                if (!VerifySig(key, Encoding.UTF8.GetBytes(Canonical(root, SignedFields)), old)) throw new ApiException(ErrorCodes.SignatureInvalid);
            }

            string? Str(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (!(root.TryGetProperty("v", out var ver) && ver.ValueKind == JsonValueKind.Number && ver.TryGetInt32(out var fv) && fv == FormatVersion))
                throw new ApiException(ErrorCodes.BadResponse, detail: "v");

            ReleaseFile? FileOf(ReleaseKind k, string verField, string fileField, string shaField, string sigField)
            {
                var version = Str(verField);
                var file = Str(fileField);
                if (version is null || file is null) return null;
                var sha = Str(shaField)?.ToLowerInvariant();
                var fsig = Str(sigField);
                if (!VersionRx().IsMatch(version) || !SafeFileName(file)) throw new ApiException(ErrorCodes.BadResponse, detail: fileField);
                if (sha is null || !Sha256Rx().IsMatch(sha)) throw new ApiException(ErrorCodes.ShaMissing);
                if (string.IsNullOrWhiteSpace(fsig)) throw new ApiException(ErrorCodes.SignatureMissing);
                return new ReleaseFile(k, version, file, sha, fsig!);
            }

            var blocked = new List<string>();
            if (root.TryGetProperty("blockedAddons", out var b) && b.ValueKind == JsonValueKind.Array)
                foreach (var x in b.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String && x.GetString() is { } bs && VersionRx().IsMatch(bs)) blocked.Add(bs);

            return new SignedRelease
            {
                Client = Str("client"),
                Addon = Str("addon"),
                ClientFile = FileOf(ReleaseKind.Client, "client", "clientFile", "sha256", "clientSig"),
                AddonFile = FileOf(ReleaseKind.Addon, "addon", "addonFile", "addonSha256", "addonSig"),
                // Lite-fälten är bara signerade av signature2 – utan den ignoreras de.
                Lite = v2 ? Str("lite") : null,
                LiteFile = v2 ? FileOf(ReleaseKind.Lite, "lite", "liteFile", "liteSha256", "liteSig") : null,
                HasSignature2 = v2,
                Issued = Str("issued"),
                BlockedAddons = blocked,
            };
        }
    }

    /// <summary>Först SHA-256 mot det signerade värdet, sedan filsignaturen. Kastar vid avvikelse.</summary>
    public static void VerifyFile(byte[] data, ReleaseKind kind, ReleaseFile file, string publicKeyPem)
    {
        if (file.Kind != kind) throw new ApiException(ErrorCodes.SignatureInvalid);
        using var key = Key(publicKeyPem);
        var sha = ApiClient.Sha256Hex(data);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sha), Encoding.ASCII.GetBytes(file.Sha256)))
            throw new ApiException(ErrorCodes.ShaMismatch);
        if (!VerifySig(key, FileMessage(kind, file.Version, sha), file.Signature))
            throw new ApiException(ErrorCodes.SignatureInvalid);
    }
}
