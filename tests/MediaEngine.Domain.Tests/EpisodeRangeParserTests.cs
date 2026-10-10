using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class EpisodeRangeParserTests
{
    [Theory]
    [InlineData("Show Name S01E01E02", 1, 1, 2)]
    [InlineData("Show Name S01E01-E02", 1, 1, 2)]
    [InlineData("Show Name S01E01 - E02", 1, 1, 2)]
    [InlineData("Show Name S01E01-02", 1, 1, 2)]
    [InlineData("show name s02e05e06", 2, 5, 6)]
    [InlineData("Show Name S01E01E02E03", 1, 1, 3)]
    [InlineData("Show Name S01E01-E03", 1, 1, 3)]
    [InlineData("Show Name S01E10-E15", 1, 10, 15)]
    [InlineData("Show Name 1x01-02", 1, 1, 2)]
    [InlineData("Show Name 2x07-09", 2, 7, 9)]
    [InlineData("S03E04E05 - Episode Title", 3, 4, 5)]
    [InlineData("Show Name S01E01E02 - Two Part Premiere", 1, 1, 2)]
    public void Parse_ReadsUnbrokenRange(string stem, int season, int first, int last)
    {
        var range = EpisodeRangeParser.Parse(stem);

        Assert.NotNull(range);
        Assert.Equal(season, range.Season);
        Assert.Equal(first, range.FirstEpisode);
        Assert.Equal(last, range.LastEpisode);
        Assert.True(range.IsRange);
        Assert.False(range.IsUnresolvedRange);
        Assert.Equal(last - first + 1, range.Count);
    }

    [Theory]
    [InlineData("Show Name S01E01")]
    [InlineData("Show Name 1x05")]
    [InlineData("S01E01 - Episode Title")]
    [InlineData("Show Name S01E01-1080p")]
    [InlineData("Show Name S01E01 - 2 Fast")]
    [InlineData("Show Name S01E01 WEBRip")]
    public void Parse_SingleEpisode_IsNotARange(string stem)
    {
        var range = EpisodeRangeParser.Parse(stem);

        Assert.NotNull(range);
        Assert.False(range.IsRange);
        Assert.False(range.IsUnresolvedRange);
        Assert.Equal(range.FirstEpisode, range.LastEpisode);
    }

    [Theory]
    [InlineData("Show Name S01E01E03")]
    [InlineData("Show Name S01E01E02E04")]
    [InlineData("Show Name S01E01E200")]
    [InlineData("Show Name S01E01-E200")]
    [InlineData("Show Name S01E05-E03")]
    [InlineData("Show Name S01E01E01")]
    [InlineData("Show Name S01E01-E07")]
    [InlineData("Show Name 1x01-30")]
    public void Parse_BrokenOrOversizedRange_IsUnresolvedAndKeepsFirstEpisodeOnly(string stem)
    {
        var range = EpisodeRangeParser.Parse(stem);

        Assert.NotNull(range);
        Assert.False(range.IsRange);
        Assert.True(range.IsUnresolvedRange);
        Assert.Equal(1, range.Count);
    }

    [Fact]
    public void Parse_AllowsExactlyTheMaximumNumberOfEpisodes()
    {
        var range = EpisodeRangeParser.Parse("Show Name S01E01-E06");

        Assert.NotNull(range);
        Assert.Equal(EpisodeRangeParser.MaxEpisodesPerFile, range.Count);
        Assert.False(range.IsUnresolvedRange);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Arrival (2016)")]
    [InlineData("Show Name Season 1 Episode 1")]
    public void Parse_ReturnsNull_WhenNoSeasonEpisodePattern(string stem)
    {
        Assert.Null(EpisodeRangeParser.Parse(stem));
    }

    [Fact]
    public void Parse_EndIndexPointsPastTheWholeRange()
    {
        const string stem = "Show Name S01E01-E02 - Title";

        var range = EpisodeRangeParser.Parse(stem);

        Assert.NotNull(range);
        Assert.Equal(" - Title", stem[range.EndIndex..]);
    }
}
