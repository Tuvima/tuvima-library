using MediaEngine.Api.Services.Details.Internals;

namespace MediaEngine.Api.Tests;

/// <summary>The AI results on an item's detail page: shown when an AI plugin left something, absent otherwise.</summary>
public sealed class DetailAiResultsTests
{
    [Fact]
    public void AiResults_CarryVibesThemesMoodsWarningsAndTheShortSummary()
    {
        var results = DetailCompositionOrchestrator.BuildAiResults(new Dictionary<string, string>
        {
            ["vibe"] = "cozy; slow-burn",
            ["themes"] = "friendship;loss",
            ["mood"] = "warm",
            ["content_warnings"] = "violence",
            ["tldr"] = "A quiet story about moving on.",
        });

        Assert.NotNull(results);
        Assert.Equal(["cozy", "slow-burn"], results.Vibes);
        Assert.Equal(["friendship", "loss"], results.Themes);
        Assert.Equal(["warm"], results.Moods);
        Assert.Equal(["violence"], results.ContentWarnings);
        Assert.Equal("A quiet story about moving on.", results.Summary);
    }

    [Fact]
    public void AiResults_AreAbsentWhenNoAiPluginLeftAnything()
    {
        var results = DetailCompositionOrchestrator.BuildAiResults(new Dictionary<string, string>
        {
            ["genre"] = "Drama",
            ["description"] = "From a provider.",
        });

        Assert.Null(results);
    }

    [Fact]
    public void AiResults_IgnoreBlankValuesAndRepeats()
    {
        var results = DetailCompositionOrchestrator.BuildAiResults(new Dictionary<string, string>
        {
            ["vibe"] = "cozy;Cozy; ",
            ["themes"] = "  ",
        });

        Assert.NotNull(results);
        Assert.Equal(["cozy"], results.Vibes);
        Assert.Empty(results.Themes);
    }
}
