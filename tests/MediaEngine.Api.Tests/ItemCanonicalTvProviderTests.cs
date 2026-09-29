using MediaEngine.Api.Endpoints;
using MediaEngine.Domain;

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
}
