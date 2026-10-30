using System.Text.Json.Nodes;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Api.Tests;

public sealed class TvMatchBrowserMapperTests
{
    private static readonly JsonNode ShimmerShow = JsonNode.Parse("""
        { "id": 300, "seasons": [
            { "id": 9001, "number": 1, "type": { "id": 1, "name": "Aired Order", "type": "official" } },
            { "id": 9002, "number": 2, "type": { "id": 1, "name": "Aired Order", "type": "official" } } ] }
        """)!;

    [Fact]
    public void EpisodesOfAnotherShow_CarryTheFullShowSeasonAndEpisodeIdentity()
    {
        var season = new TvMatchBrowserSeasonDto("9001", "300", 1, "Season 1", null, false);
        var ordered = new[]
        {
            JsonNode.Parse("""{ "id": 7003, "seasonId": 9001, "seasonNumber": 1, "number": 3, "name": "Pet Pals", "aired": "2015-02-01", "image": "/banners/x.jpg" }""")!,
            JsonNode.Parse("""{ "id": 7101, "seasonId": 9002, "seasonNumber": 2, "number": 1, "name": "Elsewhere" }""")!,
        };

        var rows = TvMatchBrowserMapper.MapEpisodes(
            TvMatchBrowserMapper.SelectSeasonEpisodes(ordered, season), "300", "Shimmer and Shine", season, null,
            new Dictionary<string, Guid>());

        var row = Assert.Single(rows);
        Assert.Equal("7003", row.EpisodeId);
        Assert.Equal("300", row.SeriesId);
        Assert.Equal("Shimmer and Shine", row.ShowName);
        Assert.Equal("9001", row.SeasonId);
        Assert.Equal(1, row.SeasonNumber);
        Assert.Equal(3, row.EpisodeNumber);
        Assert.False(row.HasFile);
    }

    [Fact]
    public void AShowSearchResult_IsAShowRowAndNeverAnEpisodeRow()
    {
        var search = JsonNode.Parse("""
            [ { "tvdb_id": "300", "name": "Shimmer and Shine", "year": "2015", "type": "series" },
              { "id": "series-301", "name": "The Continental", "translations": { "eng": "The Continental" } },
              { "id": "movie-5", "name": "Not a show" } ]
            """);

        var shows = TvMatchBrowserMapper.MapShows(search, "301");

        Assert.Equal(["300", "301"], shows.Select(show => show.SeriesId));
        Assert.True(shows[1].IsCurrent);
        Assert.False(shows[0].IsCurrent);
    }

    [Fact]
    public void EpisodesWithoutAnIdOrANumber_AreLeftOut_SoNoRowIsHalfAnIdentity()
    {
        var season = new TvMatchBrowserSeasonDto("9001", "300", 1, "Season 1", null, false);
        var ordered = new[]
        {
            JsonNode.Parse("""{ "seasonId": 9001, "number": 1 }""")!,
            JsonNode.Parse("""{ "id": 1, "seasonId": 9001, "number": 0 }""")!,
            JsonNode.Parse("""{ "id": 2, "seasonId": 9001, "number": 2 }""")!,
        };

        var rows = TvMatchBrowserMapper.MapEpisodes(ordered, "300", "Show", season, "2", new Dictionary<string, Guid>());

        var row = Assert.Single(rows);
        Assert.Equal("Episode 2", row.Title);
        Assert.True(row.IsCurrent);
    }

    [Fact]
    public void RowsMarkTheSlotsThatAlreadyHoldAFile()
    {
        var season = new TvMatchBrowserSeasonDto("9001", "300", 1, "Season 1", null, false);
        var file = Guid.NewGuid();
        var ordered = new[] { JsonNode.Parse("""{ "id": 7, "seasonId": 9001, "number": 1 }""")! };

        var rows = TvMatchBrowserMapper.MapEpisodes(ordered, "300", "Show", season, null,
            new Dictionary<string, Guid> { ["7"] = file });

        Assert.True(rows[0].HasFile);
        Assert.Equal(file, rows[0].FileEntityId);
    }

    [Fact]
    public void Seasons_AreListedInDefaultOrderLowestFirst()
    {
        var seasons = TvMatchBrowserMapper.MapSeasons(ShimmerShow, "300", "9002");

        Assert.Equal([1, 2], seasons.Select(season => season.SeasonNumber));
        Assert.True(seasons[1].IsCurrent);
    }

    [Fact]
    public void AnEpisodeThatDoesNotBelongToTheShow_IsRefusedByTheSameEvidenceTheSaveUses()
    {
        var show = JsonNode.Parse("""{ "id": "300", "seasons": [ { "id": "9001", "number": "1", "type": { "type": "official" } } ] }""");
        var foreign = JsonNode.Parse("""{ "id": "7003", "seriesId": "999", "seasonNumber": "1", "number": "3" }""");

        var refused = ItemCanonicalEndpoints.ValidateTvdbEpisodeMoveEvidence("300", "7003", 1, 3, show, foreign, []);

        Assert.NotNull(refused);
    }
}
