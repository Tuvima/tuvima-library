using MediaEngine.Api.Endpoints;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;

namespace MediaEngine.Api.Tests;

public sealed class ItemCanonicalTvProviderTests
{
    [Fact]
    public void TelevisionRetailApply_RequiresTvdbNameAndProviderId()
    {
        Assert.True(ItemCanonicalEndpoints.IsRetailProviderAllowed("TV", "tvdb", WellKnownProviders.Tvdb));
        Assert.False(ItemCanonicalEndpoints.IsRetailProviderAllowed("TV", "tmdb", WellKnownProviders.Tmdb));
        Assert.False(ItemCanonicalEndpoints.IsRetailProviderAllowed("TV", "tvdb", WellKnownProviders.Tmdb));
        Assert.False(ItemCanonicalEndpoints.IsRetailProviderAllowed("TV", "tmdb", WellKnownProviders.Tvdb));
        Assert.True(ItemCanonicalEndpoints.IsRetailProviderAllowed("Movies", "tmdb", WellKnownProviders.Tmdb));
    }

    [Fact]
    public void SeriesSearch_DoesNotTurnOwnedEpisodeNumbersIntoEpisodeCandidates()
    {
        var policy = new ItemCanonicalEndpoints.CanonicalTargetPolicy("TV", "container", "show",
            [MetadataFieldConstants.ShowName], [], [BridgeIdKeys.TvdbId], [], [], true, true, true);
        var draft = new Dictionary<string, string>
        {
            [MetadataFieldConstants.ShowName] = "Solo Leveling",
            [MetadataFieldConstants.SeasonNumber] = "2",
            [MetadataFieldConstants.EpisodeNumber] = "1",
        };

        var fields = ItemCanonicalEndpoints.BuildRetailSearchFields(policy, draft, null);

        Assert.Equal("Solo Leveling", fields?[MetadataFieldConstants.ShowName]);
        Assert.False(fields!.ContainsKey(MetadataFieldConstants.SeasonNumber));
        Assert.False(fields.ContainsKey(MetadataFieldConstants.EpisodeNumber));
        Assert.True(ItemCanonicalEndpoints.IsTvdbShowCandidate("389597",
            new Dictionary<string, string> { [BridgeIdKeys.TvdbId] = "389597" }));
        Assert.False(ItemCanonicalEndpoints.IsTvdbShowCandidate("5876034",
            new Dictionary<string, string>
            {
                [BridgeIdKeys.TvdbId] = "389597",
                [BridgeIdKeys.TvdbEpisodeId] = "5876034",
            }));
    }
}
