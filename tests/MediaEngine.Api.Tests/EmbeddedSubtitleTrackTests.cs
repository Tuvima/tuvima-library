using MediaEngine.Api.Services.Playback;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

/// <summary>Issue 1058: every subtitle track inside a video file is listed with its own name and flags, never one per language.</summary>
public sealed class EmbeddedSubtitleTrackTests
{
    private static readonly Guid Asset = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void TwoEnglishTextTracksAreBothListedWithTheirOwnNamesAndAReadableAddress()
    {
        var tracks = PlaybackCapabilitiesService.BuildEmbeddedSubtitleTracks(Asset,
        [
            new EmbeddedSubtitleStream(3, "subrip", "eng", false, false, "English"),
            new EmbeddedSubtitleStream(4, "subrip", "eng", false, false, "SDH"),
        ]);

        Assert.Equal(2, tracks.Count);
        Assert.Equal("English", tracks[0].DisplayName);
        Assert.Equal("SDH", tracks[1].DisplayName);
        Assert.False(tracks[0].IsHearingImpaired);
        Assert.True(tracks[1].IsHearingImpaired);
        Assert.All(tracks, track => Assert.True(track.IsEmbedded));
        Assert.Equal($"/stream/{Asset}/subtitles/embedded/3", tracks[0].DeliveryUrl);
        Assert.Equal($"/stream/{Asset}/subtitles/embedded/4", tracks[1].DeliveryUrl);
    }

    [Fact]
    public void APictureTrackIsListedButHasNoTextAddressSoItCanOnlyBeBurnedIn()
    {
        var tracks = PlaybackCapabilitiesService.BuildEmbeddedSubtitleTracks(Asset,
        [
            new EmbeddedSubtitleStream(5, "hdmv_pgs_subtitle", "eng", false, false),
            new EmbeddedSubtitleStream(6, "dvd_subtitle", "fra", true, false),
        ]);

        Assert.All(tracks, track => { Assert.True(track.IsPicture); Assert.Null(track.DeliveryUrl); });
        Assert.Equal(5, tracks[0].StreamIndex);
        Assert.True(tracks[1].IsForced);
    }

    [Fact]
    public void ATrackWithoutANameIsNamedByItsLanguageAndTheHearingImpairedFlagIsKept()
    {
        var tracks = PlaybackCapabilitiesService.BuildEmbeddedSubtitleTracks(Asset,
            [new EmbeddedSubtitleStream(2, "ass", "eng", false, true)]);

        Assert.Equal("Subtitles (ENG)", tracks[0].DisplayName);
        Assert.True(tracks[0].IsHearingImpaired);
        Assert.NotNull(tracks[0].DeliveryUrl);
    }
}
