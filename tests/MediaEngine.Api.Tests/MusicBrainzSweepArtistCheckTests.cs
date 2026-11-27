using MediaEngine.Api.Services;
using MediaEngine.Providers.Models;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The MusicBrainz sweep must not apply another act's recording of the same song title to a track
/// (a Glenn Gould "Aria" must not pick up Placebo's), so its answer has to name the artist the file is tagged with.
/// </summary>
public sealed class MusicBrainzSweepArtistCheckTests
{
    private static ProviderClaim Claim(string key, string value) => new(key, value, 0.9);

    [Fact]
    public void AnAnswerNamingAnotherArtist_IsNotApplied()
    {
        Assert.False(MusicBrainzEnrichmentHostedService.ClaimsNameTheTaggedArtist(
            "Glenn Gould", [Claim("artist", "Placebo"), Claim("title", "Aria")]));
    }

    [Fact]
    public void AnAnswerNamingTheTaggedArtist_IsApplied_AndCompilationsAreNeverChecked()
    {
        Assert.True(MusicBrainzEnrichmentHostedService.ClaimsNameTheTaggedArtist("Glenn Gould", [Claim("artist", "Glenn Gould")]));
        Assert.True(MusicBrainzEnrichmentHostedService.ClaimsNameTheTaggedArtist("Various Artists", [Claim("artist", "Placebo")]));
        Assert.True(MusicBrainzEnrichmentHostedService.ClaimsNameTheTaggedArtist("Glenn Gould", [Claim("title", "Aria")]));
    }
}
