using MediaEngine.Web.Components.Setup;

namespace MediaEngine.Web.Tests;

// Back reaches the Vault step again (setup fixes D3, #856): the walk through setup, and when an answer is recorded.
public sealed class SetupVaultStepRulesTests
{
    [Fact]
    public void BackFromLibraries_ReachesTheVaultStep_WheneverTheVaultWasOffered()
    {
        Assert.Equal("vault", SetupVaultStepRules.Previous("libraries", vaultOffered: true));
        Assert.Equal("tools", SetupVaultStepRules.Previous("vault", vaultOffered: true));
    }

    [Fact]
    public void BackFromLibraries_SkipsTheVaultStep_ForAPersonItWasNeverOfferedTo()
    {
        Assert.Equal("tools", SetupVaultStepRules.Previous("libraries", vaultOffered: false));
        Assert.DoesNotContain("vault", SetupVaultStepRules.NavigableSteps(vaultOffered: false));
    }

    [Fact]
    public void TheVaultStaysInTheWalk_ForTheWholeOfSetup_OnceItWasOffered()
    {
        Assert.Equal(["welcome", "profile", "tools", "vault", "libraries", "metadata", "done"], SetupVaultStepRules.NavigableSteps(vaultOffered: true));
    }

    [Theory]
    [InlineData("passed", false, true)]
    [InlineData("passed", true, true)]
    [InlineData("deferred", false, false)]
    [InlineData("not_started", false, false)]
    [InlineData("deferred", true, true)]
    public void TheStepShowsYourVaultIsOn_OnlyWhenTheVaultIsOn_NeverAfterNotNow(string recorded, bool vaultAvailable, bool expected)
    {
        Assert.Equal(expected, SetupVaultStepRules.IsAlreadyOn(recorded, vaultAvailable));
    }

    [Theory]
    [InlineData("not_started", true, true)]
    [InlineData("not_started", false, true)]
    [InlineData("deferred", true, true)]
    [InlineData("passed", false, true)]
    [InlineData("passed", true, false)]
    [InlineData("deferred", false, false)]
    public void ContinuingFromARevisitedStep_RecordsNothing_UnlessTheAnswerChanged(string recorded, bool turnedOn, bool expected)
    {
        Assert.Equal(expected, SetupVaultStepRules.NeedsDecision(recorded, turnedOn));
    }

    [Theory]
    [InlineData("passed", true)]
    [InlineData("deferred", true)]
    [InlineData("not_started", false)]
    [InlineData("in_progress", false)]
    public void AfterAReload_OnlyAnAnsweredVaultStepSendsThePageLookingForThePersonItWasOfferedTo(string recorded, bool expected)
    {
        Assert.Equal(expected, SetupVaultStepRules.WasAnswered(recorded));
    }
}
