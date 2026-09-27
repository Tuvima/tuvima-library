using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using Xunit;

namespace MediaEngine.Providers.Tests;

public class MusicAlbumIdentityEvidenceTests
{
    [Fact]
    public void SameTitleFromDifferentArtistIsNotAnIdentityMatch()
    {
        Assert.False(MusicAlbumIdentityEvidence.Corroborates("Artist A", new Dictionary<string,string>(),
            [new ProviderClaim("artist", "Artist B", 1)]));
    }
    [Fact]
    public void MatchingReleaseGroupCorroboratesLocalizedArtistName()
    {
        Assert.True(MusicAlbumIdentityEvidence.Corroborates("Localized name", new Dictionary<string,string> { ["musicbrainz_release_group_id"] = "release-id" },
            [new ProviderClaim("musicbrainz_release_group_id", "release-id", 1)]));
    }
}
