using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

public sealed class EditorTvMatchScoreTests
{
    private static TvMatchBrowserShowDto Show(string name, string? year = null) => new("1", name, year, null, null, "", false);

    private static TvMatchBrowserEpisodeDto Episode(int season, int number, string title) =>
        new("e", "1", "The Expanse", "s", season, number, title, null, null, null, "", false, false, null);

    [Fact]
    public void AnExactNameWithTheSameYearScoresFull() =>
        Assert.Equal(100, EditorTvMatchScore.Show("The Expanse", "2015", Show("The Expanse", "2015")));

    [Fact]
    public void ADifferentYearLowersTheScore() =>
        Assert.True(EditorTvMatchScore.Show("The Expanse", "2015", Show("The Expanse", "1999")) < 85);

    [Fact]
    public void AnUnrelatedShowScoresLow() =>
        Assert.True(EditorTvMatchScore.Show("The Expanse", null, Show("Peppa Pig")) < 20);

    [Fact]
    public void NothingSearchedMeansNoScore() =>
        Assert.Equal(0, EditorTvMatchScore.Show("  ", null, Show("Anything")));

    [Fact]
    public void AnEpisodeInTheNamedSlotWithTheRightTitleScoresHigh()
    {
        var context = new EditorMatchContextDto { ParsedSeason = 2, ParsedEpisode = 5, ParsedTitle = "Pyramid" };

        Assert.Equal(100, EditorTvMatchScore.Episode(context, Episode(2, 5, "Pyramid")));
        Assert.True(EditorTvMatchScore.Episode(context, Episode(2, 6, "Other")) < 20);
    }

    [Theory]
    [InlineData(90, "strong")]
    [InlineData(70, "medium")]
    [InlineData(30, "weak")]
    public void BandsFollowThePercentage(int percent, string band) => Assert.Equal(band, EditorTvMatchScore.Band(percent));
}
