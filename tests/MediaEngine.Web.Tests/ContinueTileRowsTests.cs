using MediaEngine.Contracts.Display;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.MediaTiles;

namespace MediaEngine.Web.Tests;

/// <summary>The words on a Continue tile (review mock-ups D6): the title row, the second row, and the percent badge.</summary>
public sealed class ContinueTileRowsTests
{
    [Fact]
    public void AnEpisodeTitleRow_ReadsSeasonEpisodeColonTitle()
    {
        var item = Episode("Night 1", 1, 1);

        Assert.Equal("S1 E1: Night 1", MediaTileLabels.ContinueHeading(item));
    }

    [Fact]
    public void AnEpisodeWithNoTitleOfItsOwn_SaysJustItsCode()
    {
        Assert.Equal("S2 E5", MediaTileLabels.ContinueHeading(Episode(string.Empty, 2, 5)));
    }

    [Fact]
    public void ANonEpisodeTitleRow_IsItsTitle()
    {
        Assert.Equal("Dune", MediaTileLabels.ContinueHeading(new MediaTileViewModel { Title = "Dune", MediaKind = "Book" }));
    }

    [Theory]
    [InlineData("R", 2021, "R · 2021")]
    [InlineData("  PG-13 ", 1999, "PG-13 · 1999")]
    [InlineData("", 1965, "1965")]
    [InlineData(null, 1965, "1965")]
    [InlineData("TV-MA", 0, "TV-MA")]
    public void TheSecondRow_IsTheContentRatingThenTheYear(string? rating, int year, string expected)
    {
        var item = new MediaTileViewModel { ContentRating = rating, SortYear = year, HoverFacts = ["★ 8.1"] };

        Assert.Equal(expected, MediaTileLabels.ContinueSecondLine(item));
    }

    [Theory]
    [InlineData("Movie", "R", 2023, "Film · R · 2023")]
    [InlineData("Book", null, 2017, "Book · 2017")]
    [InlineData("Audiobook", null, 2021, "Audiobook · 2021")]
    [InlineData("TV", "TV-MA", 2021, "Show · TV-MA · 2021")]
    [InlineData("Video", null, 0, "Video")]
    public void TheSecondRow_SaysTheTypeInWordsFirst(string kind, string? rating, int year, string expected)
    {
        var item = new MediaTileViewModel { MediaKind = kind, ContentRating = rating, SortYear = year };

        Assert.Equal(expected, MediaTileLabels.ContinueSecondLine(item));
    }

    [Fact]
    public void TheSecondRow_IsEmptyWhenThereIsNeitherARatingNorAYear()
    {
        Assert.Null(MediaTileLabels.ContinueSecondLine(new MediaTileViewModel { HoverFacts = ["★ 8.1"], Creator = "Someone" }));
    }

    [Theory]
    [InlineData(42.4, "42%")]
    [InlineData(3, "3%")]
    public void ThePercentBadge_IsTheRoundedPercentComplete(double progress, string expected)
    {
        Assert.Equal(expected, MediaTileLabels.ContinuePercent(new MediaTileViewModel { ProgressPct = progress }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(100.0)]
    public void ThereIsNoPercentBadge_BeforeAnyProgressOrOnceFinished(double? progress)
    {
        Assert.Null(MediaTileLabels.ContinuePercent(new MediaTileViewModel { ProgressPct = progress }));
    }

    private static MediaTileViewModel Episode(string episodeTitle, int season, int episode) => new()
    {
        Title = "Show",
        MediaKind = "TV",
        EpisodeContext = new DisplayEpisodeContextDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Show", episodeTitle, season, episode, DisplayContinuationState.InProgress, null, null),
    };
}
