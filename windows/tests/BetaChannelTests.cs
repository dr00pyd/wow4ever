using ForeverScout.Core;
using Xunit;

namespace ForeverScout.Tests;

/// <summary>1.0.13: beta-kanalen ("Betaversioner" i Inställningar) – egen versionsinfo och egen tidsstämpel.</summary>
public class BetaChannelTests
{
    [Fact]
    public void VersionPath_BetaHasItsOwnChannel()
    {
        Assert.Equal("/api/bidrag/version", ApiClient.VersionPath(false));
        Assert.Equal("/api/bidrag/version?kanal=beta", ApiClient.VersionPath(true));
    }

    [Fact]
    public void Settings_BetaIsOffByDefault()
    {
        Assert.False(new Settings().Beta);
    }
}
