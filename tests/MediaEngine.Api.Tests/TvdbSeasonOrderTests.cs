using System.Text.Json.Nodes;
using MediaEngine.Api.Endpoints;

namespace MediaEngine.Api.Tests;

public sealed class TvdbSeasonOrderTests
{
    [Fact]
    public void DefaultOrderExcludesDvdSeasonWithTheSameNumber()
    {
        var series = JsonNode.Parse("""{"defaultSeasonType":1}""");
        var aired = JsonNode.Parse("""{"id":1871836,"number":1,"type":{"id":1}}""");
        var dvd = JsonNode.Parse("""{"id":2203923,"number":1,"type":{"id":2}}""");
        var unknown = JsonNode.Parse("""{"id":999,"number":1}""");

        Assert.True(MetadataEndpoints.IsDefaultTvdbSeason(aired, series));
        Assert.False(MetadataEndpoints.IsDefaultTvdbSeason(dvd, series));
        Assert.False(MetadataEndpoints.IsDefaultTvdbSeason(unknown, series));
    }

    [Fact]
    public void AvailableOrdersAndSeasonMembershipFollowTheSeriesManifest()
    {
        var series = JsonNode.Parse("""{"defaultSeasonType":1,"seasons":[{"id":101,"number":1,"type":{"id":1,"name":"official"}},{"id":201,"number":1,"type":{"id":2,"name":"dvd"}},{"id":301,"number":1,"type":{"id":3,"name":"absolute"}}]}""");
        var official = series!["seasons"]![0];
        var dvd = series["seasons"]![1];
        var absolute = series["seasons"]![2];

        Assert.Equal(["default", "official", "dvd", "absolute"], MetadataEndpoints.GetAvailableTvdbSeasonTypes(series));
        Assert.True(MetadataEndpoints.IsTvdbSeasonInOrder(official, series, "official"));
        Assert.False(MetadataEndpoints.IsTvdbSeasonInOrder(dvd, series, "official"));
        Assert.True(MetadataEndpoints.IsTvdbSeasonInOrder(dvd, series, "dvd"));
        Assert.True(MetadataEndpoints.IsTvdbSeasonInOrder(absolute, series, "absolute"));
    }

    [Fact]
    public void EpisodeMustBelongToTheConfirmedSeason()
    {
        var seasonOne = JsonNode.Parse("""{"id":101,"number":1,"type":{"id":1}}""");
        var firstEpisode = JsonNode.Parse("""{"id":1001,"seasonId":101,"seasonNumber":1,"number":1}""");
        var secondSeasonEpisode = JsonNode.Parse("""{"id":2001,"seasonNumber":2,"number":1}""");
        var conflictingSeasonId = JsonNode.Parse("""{"id":1002,"seasonId":999,"seasonNumber":1,"number":2}""");

        Assert.True(MetadataEndpoints.IsTvdbEpisodeInSeason(firstEpisode, seasonOne));
        Assert.False(MetadataEndpoints.IsTvdbEpisodeInSeason(secondSeasonEpisode, seasonOne));
        Assert.False(MetadataEndpoints.IsTvdbEpisodeInSeason(conflictingSeasonId, seasonOne));
    }

    [Fact]
    public void OwnedSeasonTwoSelectsDefaultSeasonTwoRatherThanSpecialsOrAnotherOrder()
    {
        var series = JsonNode.Parse("""{"defaultSeasonType":1,"seasons":[{"id":100,"number":0,"type":{"id":1}},{"id":201,"number":2,"type":{"id":2}},{"id":102,"number":2,"type":{"id":1}}]}""");

        Assert.Equal("102", MetadataEndpoints.FindDefaultTvdbSeason(series, 2)?["id"]?.ToString());
        Assert.Null(MetadataEndpoints.FindDefaultTvdbSeason(series, 3));
    }

    [Fact]
    public void ShowOrderTakesPrecedenceOverLegacySeasonOrder()
    {
        var series = JsonNode.Parse("""{"seasons":[{"id":101,"number":1,"type":{"id":1,"name":"official"}},{"id":201,"number":1,"type":{"id":2,"name":"dvd"}}]}""");
        var legacySeason = new Dictionary<string, string> { ["tvdb_season_type"] = "dvd" };
        var show = new Dictionary<string, string> { ["tvdb_season_type"] = "official" };

        Assert.Equal("official", MetadataEndpoints.ResolveTvdbSeasonType(null, legacySeason, show, series));
    }

    [Fact]
    public void UnknownNamedOrderIsNotAdvertisedOrAccepted()
    {
        var series = JsonNode.Parse("""{"seasons":[{"id":401,"number":1,"type":{"id":4,"name":"alternate"}}]}""");

        Assert.Equal(["default"], MetadataEndpoints.GetAvailableTvdbSeasonTypes(series));
        Assert.Null(MetadataEndpoints.ResolveTvdbSeasonType(
            "alternate", new Dictionary<string, string>(), new Dictionary<string, string>(), series));
    }
}
