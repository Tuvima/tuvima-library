using Bunit;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

public sealed class ViewOnThisDayStripTests : AsyncBunitContext
{
    public ViewOnThisDayStripTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static LocalAssetDto Cover() => new(
        Guid.NewGuid(), Guid.NewGuid(), null, null, "image", "Beach", "beach.jpg", "image/jpeg",
        new DateTimeOffset(2021, 7, 4, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null,
        false, false, null, null, 1, [], [], string.Empty, string.Empty);

    [Fact]
    public void NothingIsDrawnWhenThereAreNoEarlierYears()
    {
        var cut = Render<ViewOnThisDayStrip>(parameters => parameters
            .Add(p => p.Years, [])
            .Add(p => p.CurrentYear, 2026)
            .Add(p => p.ThumbnailUrl, _ => "/x"));

        Assert.Empty(cut.FindAll(".view-on-this-day"));
    }

    [Fact]
    public void OneCardPerYearOpensThatYear()
    {
        var opened = 0;
        var cut = Render<ViewOnThisDayStrip>(parameters => parameters
            .Add(p => p.Years, [new ViewOnThisDayYearDto(2025, 1, Cover()), new ViewOnThisDayYearDto(2021, 3, Cover())])
            .Add(p => p.CurrentYear, 2026)
            .Add(p => p.ThumbnailUrl, _ => "/x")
            .Add(p => p.OnOpenYear, year => opened = year));

        var cards = cut.FindAll(".view-on-this-day__card");
        Assert.Equal(2, cards.Count);
        cards[1].Click();
        Assert.Equal(2021, opened);
    }
}
