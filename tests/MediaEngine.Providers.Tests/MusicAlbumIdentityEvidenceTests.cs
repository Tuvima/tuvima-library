using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Models;
using Xunit;

namespace MediaEngine.Providers.Tests;

public class MusicAlbumIdentityEvidenceTests
{
    [Fact]
    public void ExactReleaseMustNotOverrideExplicitDifferentArtist()
    {
        Assert.False(MusicAlbumIdentityEvidence.Corroborates("Age of Days",
            new Dictionary<string, string> { ["musicbrainz_release_group_id"] = "group" },
            [new ProviderClaim("musicbrainz_release_group_id", "group", 1), new ProviderClaim("artist", "Kraftwerk", 1)]));
    }

    [Fact]
    public void MatchingArtistMustNotOverrideConflictingReleaseGroup()
    {
        Assert.False(MusicAlbumIdentityEvidence.Corroborates("Age of Days",
            new Dictionary<string, string> { ["musicbrainz_release_group_id"] = "24054974-5181-4df2-ae31-3510945d8269" },
            [new ProviderClaim("musicbrainz_release_group_id", "104cc34f-b4f9-3fb1-b2a0-fce32b36ec1f", 1),
             new ProviderClaim("artist", "Age of Days", 1)]));
    }
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
