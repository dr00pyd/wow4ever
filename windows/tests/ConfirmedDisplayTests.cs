using ForeverScout.Core;
using Xunit;

namespace ForeverScout.Tests;

/// <summary>"Mitt bidrag": högst två senaste fynd, samma namn bara en gång (2026-11-12: "Contingency Plan" tre gånger
/// och den tredje raden klipptes av rutan).</summary>
public class ConfirmedDisplayTests
{
    private static ConfirmedItem I(string name, string kind = "quest") => new() { Kind = kind, Id = name, Name = name };

    [Fact]
    public void SameNameOnlyOnce_AtMostTwo()
    {
        var list = ConfirmedItem.ForDisplay([I("Contingency Plan"), I("Contingency Plan", "item"), I("contingency plan", "npc"), I("Shadowgem"), I("Copper Ore")]);
        Assert.Equal(["Contingency Plan", "Shadowgem"], list.Select(i => i.Name));
    }

    [Fact]
    public void EmptyNamesSkipped_FewerThanTwoIsFine()
    {
        Assert.Equal(["Linen Cloth"], ConfirmedItem.ForDisplay([I(""), I("  "), I("Linen Cloth")]).Select(i => i.Name));
        Assert.Empty(ConfirmedItem.ForDisplay([]));
    }
}
