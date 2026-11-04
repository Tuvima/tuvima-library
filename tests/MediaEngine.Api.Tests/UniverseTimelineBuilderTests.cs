using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Universe;

namespace MediaEngine.Api.Tests;

public sealed class UniverseTimelineBuilderTests
{
    [Theory]
    [InlineData("+1015-00-00T00:00:00Z", 1015d)]
    [InlineData("-0044-03-15", -44d)]
    [InlineData("2400", 2400d)]
    [InlineData("  +7  ", 7d)]
    public void ParseYear_ReadsTheLeadingYear(string text, double expected) =>
        Assert.Equal(expected, UniverseTimelineBuilder.ParseYear(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("later")]
    [InlineData("+")]
    [InlineData("12345678901")]
    public void ParseYear_ReturnsNull_WhenThereIsNoYear(string? text) =>
        Assert.Null(UniverseTimelineBuilder.ParseYear(text));

    [Fact]
    public void Build_OffersNothing_BelowThreeDatedStops()
    {
        var timeline = UniverseTimelineBuilder.Build([Stop("a", 10, 2000), Stop("b", null, null), Stop("c", null, 2001)]);

        Assert.Null(timeline);
    }

    [Fact]
    public void Build_UsesReleaseOrder_WhenTooFewStopsHaveAStoryYear()
    {
        var timeline = UniverseTimelineBuilder.Build([Stop("late", null, 2010), Stop("early", null, 1999), Stop("mid", 5, 2004)]);

        Assert.NotNull(timeline);
        Assert.Equal(UniverseTimelineDto.ReleaseOrder, timeline.DefaultOrder);
        Assert.False(timeline.StoryOrderAvailable);
        Assert.Equal(["early", "mid", "late"], timeline.Stops.Select(stop => stop.Label));
    }

    [Fact]
    public void Build_UsesStoryOrder_WhenThreeStopsHaveAStoryYear_AndListsUnplacedStopsLast()
    {
        var timeline = UniverseTimelineBuilder.Build([Stop("c", 300, 2000), Stop("a", -50, 2005), Stop("b", 100, 1990), Stop("undated", null, 1980)]);

        Assert.NotNull(timeline);
        Assert.Equal(UniverseTimelineDto.StoryOrder, timeline.DefaultOrder);
        Assert.Equal(["a", "b", "c", "undated"], timeline.Stops.Select(stop => stop.Label));
    }

    private static UniverseTimelineBuilder.Candidate Stop(string label, double? storyYear, int? releaseYear) =>
        new(Guid.NewGuid(), label, "events", null, storyYear, releaseYear, null);
}
