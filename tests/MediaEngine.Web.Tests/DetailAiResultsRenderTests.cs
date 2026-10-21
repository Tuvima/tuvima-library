using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;

namespace MediaEngine.Web.Tests;

/// <summary>Local AI results on an item's detail page are always marked AI, and nothing shows when there are none.</summary>
public sealed class DetailAiResultsRenderTests : BunitContext
{
    [Fact]
    public void WithResults_ShowsEachGroupUnderAnAiMark()
    {
        var cut = Render<DetailAiResults>(parameters => parameters.Add(p => p.Results, new DetailAiResultsViewModel
        {
            Vibes = ["cozy", "slow-burn"],
            Themes = ["friendship"],
            ContentWarnings = ["violence"],
            Summary = "A quiet story about moving on.",
        }));

        Assert.Contains("Suggested by Local AI", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(">AI<", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("A quiet story about moving on.", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(["cozy", "slow-burn", "friendship", "violence"],
            cut.FindAll(".tl-ai-results__value").Select(value => value.TextContent.Trim()).ToArray());
        Assert.DoesNotContain("Moods", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutResults_ShowsNothingAtAll()
    {
        Assert.Empty(Render<DetailAiResults>().Markup.Trim());
        Assert.Empty(Render<DetailAiResults>(parameters => parameters.Add(p => p.Results, new DetailAiResultsViewModel())).Markup.Trim());
    }
}
