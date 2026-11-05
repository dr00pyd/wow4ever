using System.Text;
using System.Text.Json.Nodes;
using ForeverScout.Core;
using Old = ForeverScout.Tests.Compat102;

namespace ForeverScout.Tests;

/// <summary>
/// 1.0.3: två varianter (full/lite) i samma version.json. Testvektorn är skapad med <c>scripts/scout-sign.mjs
/// --exe … --exe-lite … --addon …</c> och en engångsnyckel (samma som i <see cref="ReleaseSignatureTests"/>).
/// Gamla klienter (1.0.0–1.0.2, koden i Compat/ReleaseVerifier102.cs = git live-8) måste fortfarande godta den
/// och få full-exe:n; nya klienter verifierar signature2 och väljer fil efter variant.
/// </summary>
public class VariantReleaseTests
{
    private const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAET9PFTp329iBE7Q3eu8I7M+xFWaVE
        y5uqkasv6+CZNqVHFTSqVtm9Xio0tSPsiOeh/q80QK7zd6rX8d4ZQO65kw==
        -----END PUBLIC KEY-----
        """;

    internal const string V2Json = """
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

    private static readonly byte[] FullExe = Encoding.ASCII.GetBytes("MZ full 1.0.3");
    private static readonly byte[] LiteExe = Encoding.ASCII.GetBytes("MZ lite 1.0.3");

    private static string Code(Action a) => Assert.IsAssignableFrom<ScoutException>(Record.Exception(a)).Code;

    // ---------------------------------------------------------------- gamla klienter (1.0.0–1.0.2)

    [Fact]
    public void OldClientAcceptsNewVersionJsonAndGetsFullExe()
    {
        var r = Old.ReleaseVerifier.Verify(V2Json, PublicKey);
        Assert.Equal("1.0.3", r.Client);
        Assert.Equal("ForeverScout.exe", r.ClientFile!.FileName);
        // Den gamla koden laddar ner clientFile och kontrollerar den – full-exe:n godkänns …
        Old.ReleaseVerifier.VerifyFile(FullExe, Old.ReleaseKind.Client, r.ClientFile, PublicKey);
        // … och lite-exe:n kan aldrig smita in under clientFile.
        Assert.Equal(ErrorCodes.ShaMismatch, Code(() => Old.ReleaseVerifier.VerifyFile(LiteExe, Old.ReleaseKind.Client, r.ClientFile, PublicKey)));
    }

    [Fact]
    public void OldClientIgnoresLiteFieldsEvenIfTamperedButNewClientRejects()
    {
        var n = JsonNode.Parse(V2Json)!.AsObject();
        n["liteFile"] = "Evil.exe";
        var json = n.ToJsonString();
        Assert.Equal("1.0.3", Old.ReleaseVerifier.Verify(json, PublicKey).Client); // gamla fältet orört → OK
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(json, PublicKey)));
    }

    [Fact]
    public void OldSignatureIsUnchangedSoOldClientRejectsTamperedOldFields()
    {
        var n = JsonNode.Parse(V2Json)!.AsObject();
        n["client"] = "9.9.9";
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => Old.ReleaseVerifier.Verify(n.ToJsonString(), PublicKey)));
    }

    // ---------------------------------------------------------------- nya klienter

    [Fact]
    public void NewClientVerifiesSignature2AndPicksFileByVariant()
    {
        var r = ReleaseVerifier.Verify(V2Json, PublicKey);
        Assert.True(r.HasSignature2);
        Assert.Equal("1.0.3", r.VersionOf(ReleaseKind.Lite));
        Assert.Equal("ForeverScout-lite.exe", r.File(ReleaseKind.Lite)!.FileName);
        Assert.Equal("ForeverScout.exe", r.File(ReleaseKind.Client)!.FileName);
        ReleaseVerifier.VerifyFile(LiteExe, ReleaseKind.Lite, r.LiteFile!, PublicKey);
        ReleaseVerifier.VerifyFile(FullExe, ReleaseKind.Client, r.ClientFile!, PublicKey);
    }

    [Fact]
    public void VariantsCannotBeSwapped()
    {
        var r = ReleaseVerifier.Verify(V2Json, PublicKey);
        // lite-filen med fullens metadata (eller tvärtom) godtas inte – typen ingår i filsignaturen
        Assert.ThrowsAny<ScoutException>(() => ReleaseVerifier.VerifyFile(LiteExe, ReleaseKind.Client, r.ClientFile!, PublicKey));
        Assert.ThrowsAny<ScoutException>(() => ReleaseVerifier.VerifyFile(FullExe, ReleaseKind.Lite, r.LiteFile!, PublicKey));
        var forged = r.LiteFile! with { Kind = ReleaseKind.Client };
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyFile(LiteExe, ReleaseKind.Client, forged, PublicKey)));
    }

    [Fact]
    public void SetupFieldsAreCoveredBySignature2()
    {
        var n = JsonNode.Parse(V2Json)!.AsObject();
        n["setupSha256"] = new string('0', 64);
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(n.ToJsonString(), PublicKey)));
        Assert.Equal("1.0.3", Old.ReleaseVerifier.Verify(n.ToJsonString(), PublicKey).Client); // gamla klienter påverkas inte
    }

    [Fact]
    public void BadSignature2HasNoFallbackToOldSignature()
    {
        var n = JsonNode.Parse(V2Json)!.AsObject();
        n["signature2"] = n["signature"]!.GetValue<string>(); // giltig gammal signatur, men fel för signature2
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(n.ToJsonString(), PublicKey)));
    }

    [Fact]
    public void WithoutSignature2NewClientRefusesButOldClientsStillVerify()
    {
        // 1.0.7 (L10): ingen reserv till den gamla signaturen – en fil utan signature2 godtas inte.
        var n = JsonNode.Parse(V2Json)!.AsObject();
        n.Remove("signature2");
        Assert.Equal(ErrorCodes.SignatureMissing, Code(() => ReleaseVerifier.Verify(n.ToJsonString(), PublicKey)));
        Assert.Equal("1.0.3", Old.ReleaseVerifier.Verify(n.ToJsonString(), PublicKey).Client);
    }

    [Fact]
    public void Canonical2CoversLiteAndSetupFields()
    {
        Assert.Equal(ReleaseVerifier.SignedFields.Concat(["lite", "liteFile", "liteSha256", "liteSig", "setup", "setupFile", "setupSha256", "setupSig"]).OrderBy(x => x, StringComparer.Ordinal), ReleaseVerifier.SignedFields2);
        Assert.Equal(ReleaseVerifier.SignedFields, Old.ReleaseVerifier.SignedFields); // gamla fältlistan oförändrad
    }

    [Fact]
    public void TestBuildIsFullVariant()
    {
        Assert.Equal("full", BuildInfo.Variant);
        Assert.Equal(ReleaseKind.Client, BuildInfo.UpdateKind);
    }
}

public class TakeoverTests
{
    private static readonly Version V102 = new(1, 0, 2), V103 = new(1, 0, 3);
    private const string A = @"C:\Users\x\Downloads\ForeverScout.exe", B = @"C:\Tools\Scout\ForeverScout.exe";

    [Theory]
    [InlineData("1.0.4", A, TakeoverDecision.Exit)]  // nyare, samma plats
    [InlineData("1.0.4", B, TakeoverDecision.Exit)]  // nyare, annan plats
    [InlineData("1.0.3", B, TakeoverDecision.Exit)]  // samma version, annan exe (t.ex. lite ↔ full)
    [InlineData("1.0.3", A, TakeoverDecision.Show)]  // samma exe igen → visa fönstret
    [InlineData("1.0.2", A, TakeoverDecision.Show)]  // äldre, samma plats
    [InlineData("1.0.2", B, TakeoverDecision.Show)]  // äldre kopia någon annanstans → ingen nedgradering
    [InlineData("skräp", B, TakeoverDecision.Show)]
    public void DecisionRule(string theirVersion, string theirExe, TakeoverDecision expected) =>
        Assert.Equal(expected, Takeover.Decide(V103, A, new TakeoverRequest(theirVersion, theirExe)));

    [Fact]
    public void PathComparisonIsCaseInsensitive() =>
        Assert.Equal(TakeoverDecision.Show, Takeover.Decide(V103, A, new TakeoverRequest("1.0.3", A.ToUpperInvariant())));

    [Fact]
    public void NewerInstanceRunningKeepsOldNewcomerOut() =>
        Assert.Equal(TakeoverDecision.Show, Takeover.Decide(new Version(1, 0, 4), A, new TakeoverRequest(V102.ToString(3), B)));

    [Fact]
    public void PipeRoundTripTakeOver()
    {
        var id = "ForeverScoutTest." + Guid.NewGuid().ToString("N");
        TakeoverRequest? seen = null;
        using var acted = new ManualResetEventSlim();
        TakeoverDecision acted1 = TakeoverDecision.Show;
        using var listener = Takeover.Listen(id, req => { seen = req; return Takeover.Decide(V103, A, req); }, d => { acted1 = d; acted.Set(); });
        var outcome = Takeover.Request(id, new TakeoverRequest("1.0.4", B), TimeSpan.FromSeconds(5));
        Assert.Equal(TakeoverOutcome.TakeOver, outcome);
        Assert.True(acted.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(TakeoverDecision.Exit, acted1);
        Assert.Equal(B, seen!.ExePath);
    }

    [Fact]
    public void PipeRoundTripShow()
    {
        var id = "ForeverScoutTest." + Guid.NewGuid().ToString("N");
        using var listener = Takeover.Listen(id, req => Takeover.Decide(V103, A, req), _ => { });
        Assert.Equal(TakeoverOutcome.Shown, Takeover.Request(id, new TakeoverRequest("1.0.3", A), TimeSpan.FromSeconds(5)));
        // lyssnaren fortsätter efter "show"
        Assert.Equal(TakeoverOutcome.Shown, Takeover.Request(id, new TakeoverRequest("1.0.3", A), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void OldInstanceWithoutPipeGivesNoAnswerWithinTimeout()
    {
        var id = "ForeverScoutTest." + Guid.NewGuid().ToString("N");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(TakeoverOutcome.NoAnswer, Takeover.Request(id, new TakeoverRequest("1.0.3", A), TimeSpan.FromSeconds(1)));
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.5, 5);
    }

    [Fact]
    public void PipeNameIsPerUserAndSession() =>
        Assert.Matches(@"^ForeverScout\.Takeover\.[0-9a-f]{16}$", Takeover.PipeName("ForeverScout"));
}
