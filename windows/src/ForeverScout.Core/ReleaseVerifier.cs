using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForeverScout.Core;

/// <summary>Client = fristående (full) exe, Lite = ramverksberoende exe (kräver .NET 10 Desktop Runtime), Addon = addon-zip,
/// Mac = zip med det signerade och notariserade .app-paketet (bara i signature3-formatet).</summary>
public enum ReleaseKind { Client, Addon, Lite, Mac, Linux }

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
    /// <summary>macOS-appen (bara när svaret verifierats med signature3, se <see cref="ReleaseVerifier.VerifyMac"/>).</summary>
    public string? Mac { get; init; }
    public ReleaseFile? MacFile { get; init; }
    /// <summary>Linux-appen (bara när svaret verifierats med signature5, se <see cref="ReleaseVerifier.VerifyLinux"/>).</summary>
    public string? Linux { get; init; }
    public ReleaseFile? LinuxFile { get; init; }
    public string? Issued { get; init; }
    /// <summary>Verifierades svaret med signature2 (utökat fältset)? Alltid sant från 1.0.7, som kräver signature2 (L10).</summary>
    public bool HasSignature2 { get; init; }
    /// <summary>Verifierades svaret med signature3 (alla fält inklusive mac-fälten)?</summary>
    public bool HasSignature3 { get; init; }
    /// <summary>Spärrade addonversioner. Inte signerat (servern kan lägga till via miljövariabel) – används bara
    /// för att stoppa uppladdningen och tala om det för användaren, aldrig för att godta något eller för att
    /// installera en äldre addon (L10, se <see cref="AddonInstaller.Decide"/>).</summary>
    public IReadOnlyList<string> BlockedAddons { get; init; } = [];

    public ReleaseFile? File(ReleaseKind k) => k switch { ReleaseKind.Client => ClientFile, ReleaseKind.Lite => LiteFile, ReleaseKind.Mac => MacFile, ReleaseKind.Linux => LinuxFile, _ => AddonFile };
    public string? VersionOf(ReleaseKind k) => k switch { ReleaseKind.Client => Client, ReleaseKind.Lite => Lite, ReleaseKind.Mac => Mac, ReleaseKind.Linux => Linux, _ => Addon };
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
/// Från 1.0.7 (L10) KRÄVS signature2: saknas den är svaret osignerat för den här klienten (ingen reserv till den gamla).</para>
///
/// <para><b>issued</b> (signerat, ISO 8601 UTC från scout-sign.mjs) får aldrig gå bakåt: <see cref="AcceptIssued"/> avvisar
/// en versionsinfo som är äldre än den senaste klienten sett, så att en gammal men korrekt signerad fil inte kan
/// spelas upp igen (L10). Det högsta värdet sparas i settings.json (<see cref="Settings.ReleaseIssued"/>).</para>
///
/// <para><b>Filsignaturen</b> (<c>clientSig</c>/<c>liteSig</c>/<c>addonSig</c>) gäller UTF-8-texten
/// <c>"ForeverScout-file/v1\n&lt;client|lite|addon&gt;\n&lt;version&gt;\n&lt;sha256 hex gemener&gt;"</c>, så att en fil
/// inte kan utges för en annan typ eller version. Samma format i <c>scripts/scout-sign.mjs</c>.</para>
///
/// <para><b>signature3</b> (macOS-klienten) gäller samma kanoniska form över <see cref="SignedFields3"/> = alla fält i
/// signature2 plus <c>mac, macFile, macSha256, macSig</c>. Den används BARA av <see cref="VerifyMac"/>, som kräver den.
/// <see cref="Verify(string, string)"/> (Windows) bryr sig inte om den, så Windows-klienterna beter sig exakt som förut;
/// signature och signature2 lämnas orörda av scout-sign.mjs. Mac-filens filsignatur har typen <c>mac</c>.</para>
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

    /// <summary>Fälten för signature3 (macOS): alla fält i signature2 plus mac-fälten, i bokstavsordning.</summary>
    public static readonly string[] SignedFields3 =
        ["addon", "addonFile", "addonSha256", "addonSig", "client", "clientFile", "clientSig", "issued", "lite", "liteFile", "liteSha256", "liteSig",
         "mac", "macFile", "macSha256", "macSig", "setup", "setupFile", "setupSha256", "setupSig", "sha256", "v"];

    /// <summary>
    /// Fälten för signature5 (Linux): alla fält i signature3 plus dmg-fälten (signature4, nedladdningssidan) och
    /// linux-fälten, i bokstavsordning. Används BARA av <see cref="VerifyLinux"/>; Windows och macOS läser den inte.
    /// </summary>
    public static readonly string[] SignedFields5 =
        ["addon", "addonFile", "addonSha256", "addonSig", "client", "clientFile", "clientSig", "dmg", "dmgFile", "dmgSha256", "dmgSig", "issued",
         "linux", "linuxFile", "linuxSha256", "linuxSig", "lite", "liteFile", "liteSha256", "liteSig",
         "mac", "macFile", "macSha256", "macSig", "setup", "setupFile", "setupSha256", "setupSig", "sha256", "v"];

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

    public static string KindName(ReleaseKind kind) => kind switch { ReleaseKind.Client => "client", ReleaseKind.Lite => "lite", ReleaseKind.Mac => "mac", ReleaseKind.Linux => "linux", _ => "addon" };

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
    public static SignedRelease Verify(string json, string publicKeyPem) => Verify(json, publicKeyPem, mac: false, linux: false);

    /// <summary>
    /// Linux: kräver <c>signature5</c> (över <see cref="SignedFields5"/>, ingen reserv) och returnerar även linux-fälten.
    /// Kastar vid saknad/ogiltig signatur.
    /// </summary>
    public static SignedRelease VerifyLinux(string json, string publicKeyPem) => Verify(json, publicKeyPem, mac: false, linux: true);

    /// <summary>
    /// macOS: kräver <c>signature3</c> (över <see cref="SignedFields3"/>, ingen reserv till signature2/signature) och
    /// returnerar även mac-fälten. Kastar vid saknad/ogiltig signatur.
    /// </summary>
    public static SignedRelease VerifyMac(string json, string publicKeyPem) => Verify(json, publicKeyPem, mac: true, linux: false);

    private static SignedRelease Verify(string json, string publicKeyPem, bool mac, bool linux)
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
            if (linux)
            {
                // Linux: signature5 över alla fält måste finnas och stämma. Ingen reserv.
                var s5 = SigOf(root, "signature5") ?? throw new ApiException(ErrorCodes.SignatureMissing);
                if (!VerifySig(key, Encoding.UTF8.GetBytes(Canonical(root, SignedFields5)), s5))
                    throw new ApiException(ErrorCodes.SignatureInvalid);
            }
            else if (mac)
            {
                // macOS: signature3 över alla fält måste finnas och stämma. Ingen reserv.
                var s3 = SigOf(root, "signature3") ?? throw new ApiException(ErrorCodes.SignatureMissing);
                if (!VerifySig(key, Encoding.UTF8.GetBytes(Canonical(root, SignedFields3)), s3))
                    throw new ApiException(ErrorCodes.SignatureInvalid);
            }
            else
            {
                // signature2 över det utökade fältsetet måste finnas och stämma. 1.0.7 (L10): ingen reserv till den gamla
                // signaturen – den finns kvar i filen bara för klienter 1.0.0–1.0.2.
                var s2 = SigOf(root, "signature2") ?? throw new ApiException(ErrorCodes.SignatureMissing);
                if (!VerifySig(key, Encoding.UTF8.GetBytes(Canonical(root, SignedFields2)), s2))
                    throw new ApiException(ErrorCodes.SignatureInvalid);
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
                // Lite-fälten är signerade av signature2 (och signature3, som täcker alla fält i signature2).
                Lite = Str("lite"),
                LiteFile = FileOf(ReleaseKind.Lite, "lite", "liteFile", "liteSha256", "liteSig"),
                HasSignature2 = true,
                // Mac-fälten är bara signerade av signature3 – utan den (Windows) ignoreras de.
                Mac = mac ? Str("mac") : null,
                MacFile = mac ? FileOf(ReleaseKind.Mac, "mac", "macFile", "macSha256", "macSig") : null,
                HasSignature3 = mac,
                // Linux-fälten är bara signerade av signature5 – utan den ignoreras de.
                Linux = linux ? Str("linux") : null,
                LinuxFile = linux ? FileOf(ReleaseKind.Linux, "linux", "linuxFile", "linuxSha256", "linuxSig") : null,
                Issued = Str("issued"),
                BlockedAddons = blocked,
            };
        }
    }

    /// <summary>"issued" som scout-sign.mjs skriver den (<c>new Date().toISOString()</c>, t.ex. 2026-11-05T01:08:54.179Z).</summary>
    public static bool TryParseIssued(string? issued, out DateTimeOffset at)
    {
        at = default;
        return issued is { Length: > 0 and < 40 }
            && DateTimeOffset.TryParseExact(issued, ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"],
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out at);
    }

    /// <summary>
    /// L10: släpper bara igenom en (redan signaturkontrollerad) versionsinfo vars "issued" inte är äldre än
    /// <paramref name="highestSeen"/> – det högsta värde klienten tidigare godtagit. Samma värde godtas (samma fil
    /// hämtas om och om igen). Returnerar det nya högsta värdet att spara. Saknas eller är "issued" ogiltig är svaret
    /// felaktigt (scout-sign.mjs skriver alltid fältet). Ett trasigt sparat värde ignoreras.
    /// </summary>
    public static string AcceptIssued(string? issued, string? highestSeen)
    {
        if (!TryParseIssued(issued, out var at)) throw new ApiException(ErrorCodes.BadResponse, detail: "issued");
        if (TryParseIssued(highestSeen, out var seen))
        {
            if (at < seen) throw new ApiException(ErrorCodes.ReleaseStale);
            if (at == seen) return highestSeen!;
        }
        return issued!;
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
