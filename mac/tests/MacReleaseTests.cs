using System.Text;
using System.Text.Json.Nodes;
using ForeverScout.Core;
using Old102 = ForeverScout.Tests.Compat102;
using Old106 = ForeverScout.Mac.Tests.Compat106;

namespace ForeverScout.Mac.Tests;

/// <summary>
/// version.json med mac-fält och signature3. Testvektorn är skapad med <c>scripts/scout-sign.mjs</c> och en
/// engångsnyckel (inte släppnyckeln): först <c>--exe --exe-lite --setup --addon</c>, sedan bara <c>--mac</c> (så att
/// det också visas att de tidigare delarna behålls). Bevisar att
/// <list type="bullet">
/// <item>macOS-klienten kräver signature3 och får mac-filen,</item>
/// <item>Windows-klienterna (den ordagranna 1.0.6- och 1.0.2-koden) godtar samma fil och får samma exe som förut,</item>
/// <item>att ändrade mac-fält bara stoppar macOS, aldrig Windows (signature/signature2 täcker dem inte).</item>
/// </list>
/// </summary>
public class MacReleaseTests
{
    private const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE+bB9dKJZIcRRxULlnvTGXIqiFIEw
        EnVkxfJEI/jYKEaRtkJ439yzTe8CTlvV66vGzk1Dje4KWTvuUQYb4jo3sw==
        -----END PUBLIC KEY-----
        """;

    internal const string V3Json = """
        {
          "v": 1,
          "addon": "1.0.3",
          "addonFile": "ForeverScout-addon.zip",
          "addonSha256": "d34a0d276ab049e0e64f4299d5de84a000596ef895bda2c4c55f17751383c226",
          "addonSig": "gCzjjDBlaHCCUwtDOd+abMcG5DZlv1133pbaciS76+4JTbzgxdZlxMN9E8G72uhUlkHsdeJh4/qMWHTxnWkf5A==",
          "client": "1.0.7",
          "clientFile": "ForeverScout.exe",
          "clientSig": "wp92Io8mRGA53wg17tixqr54u3tYWUEdaMM4dMa1QehwXojSUPrr00AOllixligzCBtneKY3TjymQUEXUpgssw==",
          "lite": "1.0.7",
          "liteFile": "ForeverScout-lite.exe",
          "liteSha256": "fe26167bf2415918b5e24dd9d6b9a54821c7276aab9185c6e88065ef2823b80c",
          "liteSig": "dgDI4l6+8N/TZZp/jr5n+/7T3IW2mHRgoTH0Rg5HjYgOhWnP38RXTZZyp4afN6f0bIDxq98UDg09bDp7TIUkmw==",
          "setup": "1.0.7",
          "setupFile": "ForeverScout-Setup.exe",
          "setupSha256": "4987eae4372f0f9f8b5a79a16599d66dde273cd33e6df74a54cafde2841e3299",
          "setupSig": "hkW0wcRujn8hRbz2Yufm7sieVQYQ5RDoz++Had1D5Ij5vyaTm2PYPd1wAgMqfsREytli5oA8MrXZ+fnAejPXhw==",
          "sha256": "19b387464e4d2a028f1685af70569291beaa8887eb18eae8188ec0dacea70c0b",
          "mac": "1.0.7",
          "macFile": "ForeverScout-mac.zip",
          "macSha256": "3f672328a0118e22355f3c8a746c92c5b6f5189ea10548f51fc509a7c51cb2b1",
          "macSig": "mfo2J4z01CDMXg0QQIAQMRzMnaeyvuP6kNdC0X99tVFwoD/SqunakusUTeWECZ4fQ4848FtXX6brKOFZB8hiSw==",
          "issued": "2026-11-05T01:40:54.352Z",
          "blockedAddons": [],
          "signature": "WVR6b2/jDpUVsctrpZVU68YmH0s7EdisR1ko8xPZbDmtDRwBgKFaP5fz0KBSDlCAlwnbh0bRruPThjDbnwU3dA==",
          "signature2": "EExA6EHAqW1tJRCo5oEtIyFEnz/095smpRY764sLKPd9Jw3aefbVmT5woi3QEQXN0nQ9Rdg75So2lQJKDhZ5Tg==",
          "signature3": "KSn7ZF6nZhAhKLuuQ1vfMLkSWla//CzbSoUUlkgi3DrE1e55gom2w2F8rBSo/d/CfsUmbkI5fWQc2gqP2MzATg=="
        }
        """;

    /// <summary>Zip:en som signerades som macFile (ett låtsaspaket: Info.plist, binär, _CodeSignature och staplad biljett).</summary>
    private static readonly byte[] MacZip = Convert.FromBase64String(
        "UEsDBBQAAAAAABoVZV2DFtyMAQAAAAEAAAAlAAAARm9yZXZlciBTY291dC5hcHAvQ29udGVudHMvSW5mby5wbGlzdHhQSwMEFAAAAAAAGhVlXYMW3IwBAAAAAQAAAC0AAABGb3JldmVyIFNjb3V0LmFwcC9Db250ZW50cy9NYWNPUy9Gb3JldmVyU2NvdXR4UEsDBBQAAAAAABoVZV2DFtyMAQAAAAEAAAA3AAAARm9yZXZlciBTY291dC5hcHAvQ29udGVudHMvX0NvZGVTaWduYXR1cmUvQ29kZVJlc291cmNlc3hQSwMEFAAAAAAAGhVlXYMW3IwBAAAAAQAAACgAAABGb3JldmVyIFNjb3V0LmFwcC9Db250ZW50cy9Db2RlUmVzb3VyY2VzeFBLAQIUABQAAAAAABoVZV2DFtyMAQAAAAEAAAAlAAAAAAAAAAAAAACAAQAAAABGb3JldmVyIFNjb3V0LmFwcC9Db250ZW50cy9JbmZvLnBsaXN0UEsBAhQAFAAAAAAAGhVlXYMW3IwBAAAAAQAAAC0AAAAAAAAAAAAAAIABRAAAAEZvcmV2ZXIgU2NvdXQuYXBwL0NvbnRlbnRzL01hY09TL0ZvcmV2ZXJTY291dFBLAQIUABQAAAAAABoVZV2DFtyMAQAAAAEAAAA3AAAAAAAAAAAAAACAAZAAAABGb3JldmVyIFNjb3V0LmFwcC9Db250ZW50cy9fQ29kZVNpZ25hdHVyZS9Db2RlUmVzb3VyY2VzUEsBAhQAFAAAAAAAGhVlXYMW3IwBAAAAAQAAACgAAAAAAAAAAAAAAIAB5gAAAEZvcmV2ZXIgU2NvdXQuYXBwL0NvbnRlbnRzL0NvZGVSZXNvdXJjZXNQSwUGAAAAAAQABABpAQAALQEAAAAA");

    private static readonly byte[] FullExe = Encoding.ASCII.GetBytes("MZ full 1.0.7");

    private static string Code(Action a) => Assert.IsAssignableFrom<ScoutException>(Record.Exception(a)).Code;

    private static string With(Action<JsonObject> change)
    {
        var o = JsonNode.Parse(V3Json)!.AsObject();
        change(o);
        return o.ToJsonString();
    }

    [Fact]
    public void MacClientVerifiesSignature3AndGetsTheMacZip()
    {
        var r = ReleaseVerifier.VerifyMac(V3Json, PublicKey);
        Assert.True(r.HasSignature3);
        Assert.Equal("1.0.7", r.Mac);
        Assert.Equal("ForeverScout-mac.zip", r.MacFile!.FileName);
        Assert.Equal(ReleaseKind.Mac, r.MacFile.Kind);
        ReleaseVerifier.VerifyFile(MacZip, ReleaseKind.Mac, r.MacFile, PublicKey);
        Assert.Equal("1.0.3", r.Addon); // addonen kommer också med (samma installationsväg som på Windows)
    }

    [Fact]
    public void WindowsVerifyIgnoresTheMacFieldsAndKeepsItsFiles()
    {
        var r = ReleaseVerifier.Verify(V3Json, PublicKey);
        Assert.True(r.HasSignature2);
        Assert.False(r.HasSignature3);
        Assert.Null(r.MacFile);
        Assert.Null(r.Mac);
        Assert.Equal("ForeverScout.exe", r.ClientFile!.FileName);
        Assert.Equal("ForeverScout-lite.exe", r.LiteFile!.FileName);
        ReleaseVerifier.VerifyFile(FullExe, ReleaseKind.Client, r.ClientFile, PublicKey);
    }

    [Fact]
    public void Windows106ClientAcceptsTheNewFileAndGetsTheSameExe()
    {
        var r = Old106.ReleaseVerifier.Verify(V3Json, PublicKey);
        Assert.True(r.HasSignature2);
        Assert.Equal("ForeverScout.exe", r.ClientFile!.FileName);
        Assert.Equal("ForeverScout-lite.exe", r.LiteFile!.FileName);
        Old106.ReleaseVerifier.VerifyFile(FullExe, Old106.ReleaseKind.Client, r.ClientFile, PublicKey);
    }

    [Fact]
    public void Windows102ClientAcceptsTheNewFileAndGetsTheFullExe()
    {
        var r = Old102.ReleaseVerifier.Verify(V3Json, PublicKey);
        Assert.Equal("ForeverScout.exe", r.ClientFile!.FileName);
        Old102.ReleaseVerifier.VerifyFile(FullExe, Old102.ReleaseKind.Client, r.ClientFile, PublicKey);
    }

    [Fact]
    public void ChangedMacFieldsStopOnlyTheMacClient()
    {
        var bad = With(o => o["macSha256"] = new string('0', 64));
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyMac(bad, PublicKey)));
        // Windows (ny Core, 1.0.6 och 1.0.2) påverkas inte – mac-fälten ingår inte i signature/signature2.
        Assert.NotNull(ReleaseVerifier.Verify(bad, PublicKey).ClientFile);
        Assert.NotNull(Old106.ReleaseVerifier.Verify(bad, PublicKey).ClientFile);
        Assert.NotNull(Old102.ReleaseVerifier.Verify(bad, PublicKey).ClientFile);
    }

    [Fact]
    public void ChangedWindowsFieldsAlsoStopTheMacClient()
    {
        var bad = With(o => o["clientFile"] = "Evil.exe");
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyMac(bad, PublicKey)));
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.Verify(bad, PublicKey)));
    }

    [Fact]
    public void MacClientRequiresSignature3()
    {
        var none = With(o => o.Remove("signature3"));
        Assert.Equal(ErrorCodes.SignatureMissing, Code(() => ReleaseVerifier.VerifyMac(none, PublicKey)));
        // Ingen reserv: en giltig signature2 räcker inte för macOS.
        var swapped = With(o => o["signature3"] = o["signature2"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyMac(swapped, PublicKey)));
    }

    [Fact]
    public void MacZipCannotPassAsAnotherKindAndExeCannotPassAsMac()
    {
        var r = ReleaseVerifier.VerifyMac(V3Json, PublicKey);
        var asClient = r.MacFile! with { Kind = ReleaseKind.Client };
        Assert.Equal(ErrorCodes.SignatureInvalid, Code(() => ReleaseVerifier.VerifyFile(MacZip, ReleaseKind.Client, asClient, PublicKey)));
        Assert.Equal(ErrorCodes.ShaMismatch, Code(() => ReleaseVerifier.VerifyFile(FullExe, ReleaseKind.Mac, r.MacFile!, PublicKey)));
        var tampered = (byte[])MacZip.Clone();
        tampered[^3] ^= 1;
        Assert.Equal(ErrorCodes.ShaMismatch, Code(() => ReleaseVerifier.VerifyFile(tampered, ReleaseKind.Mac, r.MacFile!, PublicKey)));
    }

    [Fact]
    public void FileMessageUsesKindMac() =>
        Assert.Equal("ForeverScout-file/v1\nmac\n1.0.7\nab", Encoding.UTF8.GetString(ReleaseVerifier.FileMessage(ReleaseKind.Mac, "1.0.7", "AB")));

    [Fact]
    public void SignedFields3IsSignedFields2PlusMacFieldsSorted()
    {
        var expected = ReleaseVerifier.SignedFields2.Concat(["mac", "macFile", "macSha256", "macSig"]).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, ReleaseVerifier.SignedFields3);
    }

    [Fact]
    public void ManifestWithoutMacFieldsStillVerifiesForWindows()
    {
        // Så ser den publicerade filen ut innan något Mac-släpp finns: inga mac-fält (signature3 med null-värden).
        var noMac = With(o => { foreach (var k in new[] { "mac", "macFile", "macSha256", "macSig", "signature3" }) o.Remove(k); });
        Assert.NotNull(ReleaseVerifier.Verify(noMac, PublicKey).ClientFile);
        Assert.Equal(ErrorCodes.SignatureMissing, Code(() => ReleaseVerifier.VerifyMac(noMac, PublicKey)));
    }
}
