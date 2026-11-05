using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ForeverScout.Core;
using Old = ForeverScout.Tests.Compat102;

namespace ForeverScout.Tests;

/// <summary>
/// Testvektorn är skapad med <c>node scripts/scout-sign.mjs</c> (engångsnyckel, inte släppnyckeln) –
/// den bevisar att Node-signaturen och .NET-verifieringen använder samma format.
/// <para>Vektorn har bara den gamla <c>signature</c> (före 1.0.3). Från 1.0.7 kräver klienten signature2 (L10), så
/// v1-formatet provas här med den gamla verifieraren (Compat/ReleaseVerifier102.cs = klient 1.0.0–1.0.2), och den nya
/// med <see cref="V2"/> (scout-sign.mjs-vektorn med signature2, samma engångsnyckel).</para>
/// </summary>
public class ReleaseSignatureTests
{
    /// <summary>scout-sign.mjs-vektor med signature + signature2 (samma som VariantReleaseTests.V2Json).</summary>
    private const string V2 = """
        {
          "v": 1,
          "client": "1.0.3",
          "clientFile": "ForeverScout.exe",
          "sha256": "157b275d8f6283166686ed4104cd5ca4ca4559cab7ab0dbdd7d0b085b5639c23",
          "clientSig": "+UrxbVLKkYw41XJhFvPNdtKvwoSX6yIVKuTXXf593/cX+ngUGoizHH4EPr7jP6XtI8dYKZj0jnZr0zu1luhUsg==",
          "lite": "1.0.3",
          "liteFile": "ForeverScout-lite.exe",
          "liteSha256": "fa0db2eb42559145408ae94bf42ea29693fd0d3283f4dc8ff2b31cd188ee7049",
          "liteSig": "kJBOSqxrk21yzCRXR2OcGXKGqgT0wuLUxmbaNS/96YJ+r/1yE0sK+yyyeouBW0qbx+JUQLVL0cduRxpnGj3biw==",
          "setup": "1.0.3",
          "setupFile": "ForeverScout-Setup.exe",
          "setupSha256": "43e906b81f0cd3f454d4877651329fcae43eb34a5e957461c15c9d023e1cc339",
          "setupSig": "ywhouSglCnVgGU9ZSZfqOlAWU0mQEFR0w3E6TeIhHpnBRQ++AxXQFFroyP0Z/EN9Pr35e84llsNF0S7rgjvcsw==",
          "addon": "1.0.1",
          "addonFile": "ForeverScout-addon.zip",
          "addonSha256": "1548a0221112924ccf1cd6868fa214f1dc847d4cc902ff07826e7f2151debd07",
          "addonSig": "dONGxPKpfJo4ED/qdg3IwZlUrce13PBVse1eXeiw3MXIB5kAYHwPJNFH2G2WLIC+An6D+RiOG2aiDfEXhks71A==",
          "issued": "2026-11-04T23:17:30.067Z",
          "blockedAddons": [
            "0.9.0"
          ],
          "signature": "P8y4I2ASPF6w+fvhONDmHpEb/qONajCo+WAMtx7ESUo1JR1Xbe53W/CynNM+ZSUnsYVIGHTqIGvPw1NimIAZPw==",
          "signature2": "TlUmKy28NdRM4NNdD5O2s7aIU4ex+sZ/u92/8ygbb68g5hbZqehrnimbdC+AvC5KPfT39b4imdf3O+movSNHHw=="
        }
        """;

    /// <summary>Den gamla klientens filpost som den nya ReleaseFile (VerifyFile är oberoende av versionssignaturen).</summary>
    private static ReleaseFile New(Old.ReleaseFile f) =>
        new(f.Kind == Old.ReleaseKind.Client ? ReleaseKind.Client : ReleaseKind.Addon, f.Version, f.FileName, f.Sha256, f.Signature);

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
        // v1-vektorn (bara "signature"): gamla klienter godtar den, 1.0.7 kräver signature2 (L10).
        var r = Old.ReleaseVerifier.Verify(NodeVersionJson, NodePublicKey);
        Assert.Equal("1.0.1", r.Client);
        Assert.Equal("ForeverScout.exe", r.ClientFile!.FileName);
        Assert.Equal("1.0.1", r.AddonFile!.Version);
        Assert.True(r.IsAddonBlocked("0.9.0"));
        ReleaseVerifier.VerifyFile(NodeExe, ReleaseKind.Client, New(r.ClientFile), NodePublicKey);
        Assert.Equal(ErrorCodes.SignatureMissing, Code(() => ReleaseVerifier.Verify(NodeVersionJson, NodePublicKey)));

        var v2 = ReleaseVerifier.Verify(V2, NodePublicKey);
        Assert.Equal("1.0.3", v2.Client);
        Assert.Equal("1.0.1", v2.AddonFile!.Version);
        Assert.True(v2.IsAddonBlocked("0.9.0"));
    }

    [Fact]
    public void UnsignedServerFieldsMayDiffer()
    {
        var n = JsonNode.Parse(V2)!.AsObject();
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
    [InlineData("liteFile", "Evil.exe")]
    public void TamperedSignedFieldIsRejected(string field, string value)
    {
        var n = JsonNode.Parse(V2)!.AsObject();
        n[field] = value;
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(n.ToJsonString(), NodePublicKey)));
        if (field == "liteFile") return; // inte i det gamla fältsetet
        var old = JsonNode.Parse(NodeVersionJson)!.AsObject();
        old[field] = value;
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => Old.ReleaseVerifier.Verify(old.ToJsonString(), NodePublicKey)));
    }

    [Fact]
    public void RemovedSignedFieldIsRejected()
    {
        var n = JsonNode.Parse(V2)!.AsObject();
        n.Remove("clientSig");
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(n.ToJsonString(), NodePublicKey)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    public void MissingOrGarbageSignatureIsRejected(string? sig)
    {
        var n = JsonNode.Parse(V2)!.AsObject();
        if (sig is null) n.Remove("signature2"); else n["signature2"] = sig;
        var code = Code(() => ReleaseVerifier.Verify(n.ToJsonString(), NodePublicKey));
        Assert.Contains(code, new[] { ErrorCodes.SignatureMissing, ErrorCodes.SignatureInvalid });
    }

    [Fact]
    public void OtherKeyIsRejected()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(V2, other.ExportSubjectPublicKeyInfoPem())));
    }

    [Fact]
    public void PlaceholderKeyRefusesEverything()
    {
        Assert.False(ReleaseVerifier.HasKey("__SCOUT_PUBLIC_KEY_PLACEHOLDER__"));
        Assert.Equal(ErrorCodes.NoPublicKey, Code(() => ReleaseVerifier.Verify(V2, "__SCOUT_PUBLIC_KEY_PLACEHOLDER__")));
    }

    [Fact]
    public void FileWithWrongContentIsRejected()
    {
        var r = ReleaseVerifier.Verify(V2, NodePublicKey);
        Assert.Equal(ErrorCodes.ShaMismatch, Code(() => ReleaseVerifier.VerifyFile(Encoding.ASCII.GetBytes("MZ evil"), ReleaseKind.Client, r.ClientFile!, NodePublicKey)));
    }

    [Fact]
    public void AddonSignatureCannotPassAsClient()
    {
        var old = Old.ReleaseVerifier.Verify(NodeVersionJson, NodePublicKey);
        var client = New(old.ClientFile!);
        // Klientfilen kan inte installeras som addon …
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyFile(NodeExe, ReleaseKind.Addon, client, NodePublicKey)));
        // … och addonens signatur duger inte för en klientfil med samma hash (typen ingår i det signerade).
        var forged = client with { Sha256 = ApiClient.Sha256Hex(NodeExe), Signature = old.AddonFile!.Signature };
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
        Assert.Equal(ErrorCodes.SignatureMissing, Code(() => ReleaseVerifier.Verify(n.ToJsonString(), pem))); // bara v1 räcker inte
        n["signature2"] = Sign(Encoding.UTF8.GetBytes(ReleaseVerifier.Canonical(doc.RootElement, ReleaseVerifier.SignedFields2)));
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
