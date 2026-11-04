using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ForeverScout.Core;

namespace ForeverScout.Tests;

/// <summary>
/// Testvektorn är skapad med <c>node scripts/scout-sign.mjs</c> (engångsnyckel, inte släppnyckeln) –
/// den bevisar att Node-signaturen och .NET-verifieringen använder samma format.
/// </summary>
public class ReleaseSignatureTests
{
    private const string NodePublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAET9PFTp329iBE7Q3eu8I7M+xFWaVE
        y5uqkasv6+CZNqVHFTSqVtm9Xio0tSPsiOeh/q80QK7zd6rX8d4ZQO65kw==
        -----END PUBLIC KEY-----
        """;

    private const string NodeVersionJson = """
        {
          "v": 1,
          "client": "1.0.1",
          "clientFile": "ForeverScout.exe",
          "sha256": "cdfd0c3b048a08996c0a0e3e96b215b76b6a8e49d0b89200dab39535b65a05d6",
          "clientSig": "dRqpSrsLV8zmtIjWjygioM3qUL1sWtgKSoOi0z0LEahmaG5zAyWertLtXzfzwe4b3J8DtonZaLGNZje9+WxvmQ==",
          "addon": "1.0.1",
          "addonFile": "ForeverScout-addon.zip",
          "addonSha256": "c98fb06628705723d128f24e1473aa787379fb489b4cbd8df9409e802808cd4d",
          "addonSig": "yQ7hVLxtlA1QQW5bFcTbDTlyvi48n5iFY7tiFcl2skR9hBcaBK+IOlLgZUlTHPVbZQPMl8wXwcezzm/c4FJPkw==",
          "issued": "2026-11-04T21:26:36.898Z",
          "blockedAddons": [ "0.9.0" ],
          "signature": "I4/u3aJbrM88BV5WzDRpDzfC8syKRe3/y4m3PqC+tYIogK8urN0zFrz7iA4GCCfMBLZSdT2l4R7L0JSBTfcFMA=="
        }
        """;

    private static readonly byte[] NodeExe = Encoding.ASCII.GetBytes("MZ test exe");

    private static string Code(Action a) => Assert.IsAssignableFrom<ScoutException>(Record.Exception(a)).Code;

    [Fact]
    public void NodeSignedVersionVerifies()
    {
        var r = ReleaseVerifier.Verify(NodeVersionJson, NodePublicKey);
        Assert.Equal("1.0.1", r.Client);
        Assert.Equal("ForeverScout.exe", r.ClientFile!.FileName);
        Assert.Equal("1.0.1", r.AddonFile!.Version);
        Assert.True(r.IsAddonBlocked("0.9.0"));
        ReleaseVerifier.VerifyFile(NodeExe, ReleaseKind.Client, r.ClientFile, NodePublicKey);
    }

    [Fact]
    public void UnsignedServerFieldsMayDiffer()
    {
        var n = JsonNode.Parse(NodeVersionJson)!.AsObject();
        n["clientUrl"] = "https://guide.wow4ever.se/scout/ForeverScout.exe";
        n["blockedAddons"] = new JsonArray("0.9.0", "1.0.0");
        var r = ReleaseVerifier.Verify(n.ToJsonString(), NodePublicKey);
        Assert.True(r.IsAddonBlocked("1.0.0"));
    }

    [Theory]
    [InlineData("client", "9.9.9")]
    [InlineData("sha256", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("addonFile", "Other.zip")]
    [InlineData("issued", "2030-01-01T00:00:00.000Z")]
    public void TamperedSignedFieldIsRejected(string field, string value)
    {
        var n = JsonNode.Parse(NodeVersionJson)!.AsObject();
        n[field] = value;
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(n.ToJsonString(), NodePublicKey)));
    }

    [Fact]
    public void RemovedSignedFieldIsRejected()
    {
        var n = JsonNode.Parse(NodeVersionJson)!.AsObject();
        n.Remove("clientSig");
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(n.ToJsonString(), NodePublicKey)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    public void MissingOrGarbageSignatureIsRejected(string? sig)
    {
        var n = JsonNode.Parse(NodeVersionJson)!.AsObject();
        if (sig is null) n.Remove("signature"); else n["signature"] = sig;
        var code = Code(() => ReleaseVerifier.Verify(n.ToJsonString(), NodePublicKey));
        Assert.Contains(code, new[] { ErrorCodes.SignatureMissing, ErrorCodes.SignatureInvalid });
    }

    [Fact]
    public void OtherKeyIsRejected()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(NodeVersionJson, other.ExportSubjectPublicKeyInfoPem())));
    }

    [Fact]
    public void PlaceholderKeyRefusesEverything()
    {
        Assert.False(ReleaseVerifier.HasKey("__SCOUT_PUBLIC_KEY_PLACEHOLDER__"));
        Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.Verify(NodeVersionJson, "__SCOUT_PUBLIC_KEY_PLACEHOLDER__")));
    }

    [Fact]
    public void FileWithWrongContentIsRejected()
    {
        var r = ReleaseVerifier.Verify(NodeVersionJson, NodePublicKey);
        Assert.Equal(ErrorCodes.ShaMismatch, Code(() => ReleaseVerifier.VerifyFile(Encoding.ASCII.GetBytes("MZ evil"), ReleaseKind.Client, r.ClientFile!, NodePublicKey)));
    }

    [Fact]
    public void AddonSignatureCannotPassAsClient()
    {
        var r = ReleaseVerifier.Verify(NodeVersionJson, NodePublicKey);
        // Klientfilen kan inte installeras som addon …
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyFile(NodeExe, ReleaseKind.Addon, r.ClientFile!, NodePublicKey)));
        // … och addonens signatur duger inte för en klientfil med samma hash (typen ingår i det signerade).
        var forged = r.ClientFile! with { Sha256 = ApiClient.Sha256Hex(NodeExe), Signature = r.AddonFile!.Signature };
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyFile(NodeExe, ReleaseKind.Client, forged, NodePublicKey)));
    }

    [Fact]
    public void DotNetRoundTripWithCanonicalJson()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var exe = Encoding.ASCII.GetBytes("MZ round trip");
        var sha = ApiClient.Sha256Hex(exe);
        string Sign(byte[] d) => Convert.ToBase64String(key.SignData(d, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var n = new JsonObject
        {
            ["v"] = 1, ["client"] = "2.0.0", ["clientFile"] = "ForeverScout.exe", ["sha256"] = sha,
            ["clientSig"] = Sign(ReleaseVerifier.FileMessage(ReleaseKind.Client, "2.0.0", sha)), ["issued"] = "x\"y\\z",
        };
        using var doc = System.Text.Json.JsonDocument.Parse(n.ToJsonString());
        n["signature"] = Sign(Encoding.UTF8.GetBytes(ReleaseVerifier.Canonical(doc.RootElement)));
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var r = ReleaseVerifier.Verify(n.ToJsonString(), pem);
        Assert.Null(r.AddonFile);
        ReleaseVerifier.VerifyFile(exe, ReleaseKind.Client, r.ClientFile!, pem);
    }

    [Fact]
    public void CanonicalMatchesJsonStringify()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"v":1,"client":"1.0","issued":"a\u0001\n\"b","zzz":"ignored"}""");
        Assert.Equal("""{"addon":null,"addonFile":null,"addonSha256":null,"addonSig":null,"client":"1.0","clientFile":null,"clientSig":null,"issued":"a\u0001\n\"b","sha256":null,"v":1}""",
            ReleaseVerifier.Canonical(doc.RootElement));
    }
}
