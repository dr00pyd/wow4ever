// ORDAGRANN KOPIA av client/windows/src/ForeverScout.Core/ReleaseVerifier.cs vid git-taggen live-8 (klient 1.0.2,
// identisk med 1.0.0 och 1.0.1). Bara namnrymden är ändrad så att den kan ligga bredvid den nya. Används av
// VariantReleaseTests för att bevisa att gamla klienter fortfarande godtar en version.json med lite-fält och signature2.
// Ändra ALDRIG den här filen – återskapa med: git show live-8:client/windows/src/ForeverScout.Core/ReleaseVerifier.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using ForeverScout.Core;

namespace ForeverScout.Tests.Compat102;

public enum ReleaseKind { Client, Addon }

public sealed record ReleaseFile(ReleaseKind Kind, string Version, string FileName, string Sha256, string Signature);

/// <summary>Verifierad versionsinfo från /api/bidrag/version.</summary>
public sealed class SignedRelease
{
    public string? Client { get; init; }
    public string? Addon { get; init; }
    public ReleaseFile? ClientFile { get; init; }
    public ReleaseFile? AddonFile { get; init; }
    public string? Issued { get; init; }
    /// <summary>Spärrade addonversioner. Inte signerat (servern kan lägga till via miljövariabel) – används bara
    /// för att avgöra när en signerad addon ska hämtas, aldrig för att godta något.</summary>
    public IReadOnlyList<string> BlockedAddons { get; init; } = [];

    public ReleaseFile? File(ReleaseKind k) => k == ReleaseKind.Client ? ClientFile : AddonFile;
    public bool IsAddonBlocked(string? version) => version != null && BlockedAddons.Contains(version);
}

/// <summary>
/// Signerade släpp (ECDSA P-256 / SHA-256, signatur i IEEE P1363-format r||s, base64).
///
/// <para><b>Versionssignaturen</b> (<c>signature</c>) gäller den kanoniska JSON:en av exakt fälten i
/// <see cref="SignedFields"/> – nycklarna i bokstavsordning, utan blanksteg, saknat fält = <c>null</c>, värdena
/// serialiserade som i <c>JSON.stringify</c>. Övriga fält i svaret (clientUrl, addonUrl, blockedAddons …) ingår inte.</para>
///
/// <para><b>Filsignaturen</b> (<c>clientSig</c>/<c>addonSig</c>) gäller UTF-8-texten
/// <c>"ForeverScout-file/v1\n&lt;client|addon&gt;\n&lt;version&gt;\n&lt;sha256 hex gemener&gt;"</c>, så att en addon-zip
/// inte kan utges för en exe eller för en annan version. Samma format i <c>scripts/scout-sign.mjs</c>.</para>
/// </summary>
public static partial class ReleaseVerifier
{
    public static readonly string[] SignedFields =
        ["addon", "addonFile", "addonSha256", "addonSig", "client", "clientFile", "clientSig", "issued", "sha256", "v"];

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

    public static string Canonical(JsonElement root)
    {
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var name in SignedFields)
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
        Encoding.UTF8.GetBytes($"ForeverScout-file/v1\n{(kind == ReleaseKind.Client ? "client" : "addon")}\n{version}\n{sha256.ToLowerInvariant()}");

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
            if (!root.TryGetProperty("signature", out var sigEl) || sigEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(sigEl.GetString()))
                throw new ApiException(ErrorCodes.SignatureMissing);
            var canonical = Encoding.UTF8.GetBytes(Canonical(root));
            if (!VerifySig(key, canonical, sigEl.GetString())) throw new ApiException(ErrorCodes.SignatureInvalid);

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
