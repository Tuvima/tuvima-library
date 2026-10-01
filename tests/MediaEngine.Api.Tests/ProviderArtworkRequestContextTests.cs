using MediaEngine.Api.Endpoints;
using MediaEngine.Contracts.Artwork;

namespace MediaEngine.Api.Tests;

public sealed class ProviderArtworkRequestContextTests
{
    private static readonly ProviderArtworkRequestContextDto Current = new(
        "episode", "Primary", "EpisodeStill", "tvdb", "98765", null,
        "season_number=2;episode_number=7");

    [Fact]
    public void ExactContext_AllowsProviderRetrieval()
    {
        Assert.True(MetadataEndpoints.MatchesProviderArtworkRequestContext(
            Current, "EpisodeStill", "tvdb", "98765", null,
            "season_number=2;episode_number=7"));
    }

    [Theory]
    [InlineData("CoverArt", "tvdb", "98765", null, "season_number=2;episode_number=7")]
    [InlineData("EpisodeStill", "tmdb", "98765", null, "season_number=2;episode_number=7")]
    [InlineData("EpisodeStill", "tvdb", "12345", null, "season_number=2;episode_number=7")]
    [InlineData("EpisodeStill", "tvdb", "98765", null, "season_number=3;episode_number=7")]
    public void ChangedContext_BlocksUnrelatedProviderRetrieval(
        string sourceType, string provider, string providerId, string? releaseId, string orderContext)
    {
        Assert.False(MetadataEndpoints.MatchesProviderArtworkRequestContext(
            Current, sourceType, provider, providerId, releaseId, orderContext));
    }

    [Fact]
    public void LegacyCallerWithoutContext_UsesServerResolvedContext()
    {
        Assert.True(MetadataEndpoints.MatchesProviderArtworkRequestContext(
            Current, null, null, null, null, null));
    }
}
