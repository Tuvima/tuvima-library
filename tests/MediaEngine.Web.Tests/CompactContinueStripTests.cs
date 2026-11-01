using Bunit;
using MediaEngine.Web.Components.Browse;
using MediaEngine.Web.Models.ViewDTOs;
using Xunit;

namespace MediaEngine.Web.Tests;

public sealed class CompactContinueStripTests : LocalizedBunitContext
{
    private static MediaTileViewModel Item(int n, double? progress = 40) => new()
    {
        Id = Guid.NewGuid(),
        Title = $"Title {n}",
        Subtitle = $"Author {n}",
        ProgressPct = progress,
        NavigationUrl = $"/read/{n}",
    };

    [Fact]
    public void Strip_ShowsUpToTwelveCards_NotJustThree()
    {
        var items = Enumerable.Range(1, 15).Select(n => Item(n)).ToList();

        var cut = Render<CompactContinueStrip>(p => p
            .Add(c => c.Title, "Continue Reading")
            .Add(c => c.Items, items));

        Assert.Equal(12, cut.FindAll(".compact-continue__card").Count);
    }

    [Fact]
    public void Card_WithProgress_ShowsPercentAndResume()
    {
        var cut = Render<CompactContinueStrip>(p => p
            .Add(c => c.Title, "Continue Reading")
            .Add(c => c.Items, [Item(1, 42)]));

        Assert.Equal("42%", cut.Find("[data-continue-percent]").TextContent.Trim());
        Assert.Contains("Resume", cut.Find(".compact-continue__foot").TextContent);
    }

    [Fact]
    public void EmptyList_RendersNothing()
    {
        var cut = Render<CompactContinueStrip>(p => p
            .Add(c => c.Title, "Continue Reading")
            .Add(c => c.Items, []));

        Assert.Empty(cut.FindAll(".compact-continue"));
    }

    [Fact]
    public void Loading_ShowsThreeSkeletonCards()
    {
        var cut = Render<CompactContinueStrip>(p => p
            .Add(c => c.Title, "Continue Reading")
            .Add(c => c.Items, [])
            .Add(c => c.Loading, true));

        Assert.Equal(3, cut.FindAll("[data-continue-skeleton]").Count);
    }

    [Fact]
    public void ViewAll_AppearsOnlyWhenAHandlerIsGiven_AndRaisesIt()
    {
        var without = Render<CompactContinueStrip>(p => p.Add(c => c.Title, "Continue").Add(c => c.Items, [Item(1)]));
        Assert.DoesNotContain("View all", without.Markup);

        var clicked = 0;
        var with = Render<CompactContinueStrip>(p => p
            .Add(c => c.Title, "Continue")
            .Add(c => c.Items, [Item(1)])
            .Add(c => c.OnViewAll, () => clicked++));
        with.Find(".compact-continue__heading button").Click();
        Assert.Equal(1, clicked);
    }
}
