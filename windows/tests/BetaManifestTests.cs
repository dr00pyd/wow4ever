using ForeverScout.Core;
using Xunit;

namespace ForeverScout.Tests;

/// <summary>1.0.13: den signerade beta-filen (public/scout/version-beta.json, scout-sign --channel beta) godtas av
/// samma verifiering som den vanliga och pekar på beta-filerna.</summary>
public class BetaManifestTests
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    [Fact]
    public void BetaManifest_VerifiesWithReleaseKey()
    {
        var path = Path.Combine(Repo(), "public", "scout", "version-beta.json");
        if (!File.Exists(path)) return; // ingen beta ute
        var r = ReleaseVerifier.Verify(File.ReadAllText(path), UpdateKeys.PublicKeyPem);
        Assert.Equal("ForeverScout-beta.exe", r.File(ReleaseKind.Client)?.FileName);
        Assert.Equal("ForeverScout-lite-beta.exe", r.File(ReleaseKind.Lite)?.FileName);
        Assert.Equal("ForeverScout-addon-beta.zip", r.File(ReleaseKind.Addon)?.FileName);
    }
}
