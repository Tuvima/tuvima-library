using MediaEngine.Api.Services.Metadata;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;

namespace MediaEngine.Api.Tests;

public sealed class ArtworkScopeProviderBridgeTests
{
    [Fact]
    public void MusicAlbum_UsesReleaseGroupWithoutRequiringWikidataIdentity()
    {
        var bridge = ArtworkScopeService.ResolveProviderArtworkBridge(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BridgeIdKeys.MusicBrainzReleaseGroupId] = "release-group-1",
            },
            "Music");

        Assert.Equal(
            (BridgeIdKeys.MusicBrainzReleaseGroupId, "release-group-1"),
            bridge);
    }

    [Fact]
    public void Movie_UsesTmdbIdentityWithoutRequiringWikidataIdentity()
    {
        var bridge = ArtworkScopeService.ResolveProviderArtworkBridge(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BridgeIdKeys.TmdbId] = "1234",
            },
            "Movies");

        Assert.Equal((BridgeIdKeys.TmdbId, "1234"), bridge);
    }

    [Fact]
    public void Tv_DoesNotUseLegacyTmdbIdentityForNewArtwork()
    {
        var bridge = ArtworkScopeService.ResolveProviderArtworkBridge(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [BridgeIdKeys.TmdbId] = "9876",
            },
            "TV");

        Assert.Null(bridge);
    }

    [Fact]
    public void Tv_UsesConfirmedTvdbIdentity()
    {
        var bridge = ArtworkScopeService.ResolveProviderArtworkBridge(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MetadataFieldConstants.IdentityProvider] = "tvdb",
                [BridgeIdKeys.TvdbId] = "1234",
                [BridgeIdKeys.TmdbId] = "9876",
            },
            "TV");

        Assert.Equal((BridgeIdKeys.TvdbId, "1234"), bridge);
    }
}
