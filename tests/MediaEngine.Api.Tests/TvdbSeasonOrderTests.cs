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
}
