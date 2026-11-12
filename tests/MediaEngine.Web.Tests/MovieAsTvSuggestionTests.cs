using System.Text.Json;
using MediaEngine.Contracts.Review;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>
/// "Found as a TV title": reading and wording the Engine's TV suggestion for a Movies item that TMDB lists as a short TV series.
/// </summary>
public sealed class MovieAsTvSuggestionTests
{
    private static readonly Guid ReviewId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid EntityId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static MovieTvSuggestionDto DrHorrible() => new()
    {
        TmdbTvId = "5739",
        Name = "Dr. Horrible's Sing-Along Blog",
        FirstAirYear = 2008,
        Type = "Miniseries",
        Seasons = 1,
        Episodes = 3,
        PosterUrl = "https://image.tmdb.org/t/p/w500/dr.jpg",
    };

    [Fact]
    public void Reader_ReadsTheEngineSuggestionAndIgnoresMissingOrBrokenCandidates()
    {
        var json = JsonSerializer.Serialize(new[] { DrHorrible() });

        var suggestion = MovieTvSuggestionReader.Read(json);

        Assert.NotNull(suggestion);
        Assert.Equal("5739", suggestion!.TmdbTvId);
        Assert.Equal(3, suggestion.Episodes);
        Assert.Null(MovieTvSuggestionReader.Read(null));
        Assert.Null(MovieTvSuggestionReader.Read("not json"));
        Assert.Null(MovieTvSuggestionReader.Read("[]"));
    }

    [Theory]
    [InlineData("Miniseries", 3, "TMDB lists this as the miniseries 'X' (2008, 3 parts). Move it to TV?")]
    [InlineData(null, 1, "TMDB lists this as the TV series 'X' (2008, 1 part). Move it to TV?")]
    public void Describe_UsesTheSeriesTypeAndPartCount(string? type, int episodes, string expected)
    {
        var suggestion = new MovieTvSuggestionDto { Name = "X", FirstAirYear = 2008, Type = type, Episodes = episodes };

        Assert.Equal(expected, MovieTvSuggestionReader.Describe(suggestion));
    }

    [Fact]
    public void DescribeMove_ForAWholeMiniseries_NamesSeasonOneAndTheEpisodeRange()
    {
        var moved = new ReviewMoveToTvResponse(true, ReviewId, EntityId, "Dr. Horrible's Sing-Along Blog", "tv-library", 1, [1, 2, 3], null);

        Assert.Equal(
            "Moved to TV: 'Dr. Horrible's Sing-Along Blog' Season 1, Episodes 1\u20133.",
            MovieTvSuggestionReader.DescribeMove(moved));
    }

    [Fact]
    public void DescribeMove_ForASinglePart_NamesTheOneEpisode()
    {
        var moved = new ReviewMoveToTvResponse(true, ReviewId, EntityId, "X", "tv-library", 1, [1], null);

        Assert.Equal("Moved to TV: 'X' Season 1, Episode 1.", MovieTvSuggestionReader.DescribeMove(moved));
    }

    [Fact]
    public void DescribeMove_WhenThePartsCouldNotBeMatched_SaysItWasFiledAsASpecialAndWhy()
    {
        var moved = new ReviewMoveToTvResponse(
            true, ReviewId, EntityId, "X", "tv-library", 0, [1], "TMDB lists 7 parts; one file can cover at most 6.");

        Assert.Equal(
            "Moved to TV as a special of 'X'. TMDB lists 7 parts; one file can cover at most 6.",
            MovieTvSuggestionReader.DescribeMove(moved));
    }
}
