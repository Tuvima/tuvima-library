using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;

namespace MediaEngine.Api.Tests;

public sealed class TvWebPlaybackProfileTests
{
    [Fact]
    public void TvWeb_IsH264AacAdaptiveHls_WithNoDirectPlayContainers()
    {
        var profile = PlaybackCapabilitiesService.ProfileFor("tv-web");

        Assert.Equal("tv-web", profile.Key);
        Assert.Equal(PlaybackDeliveryModes.Hls, profile.PreferredDelivery);
        // No container is listed, so no title direct-plays: a TV browser's video element cannot send the bearer token.
        Assert.Empty(profile.SupportedContainers);
        Assert.Equal(["h264"], profile.SupportedVideoCodecs);
        Assert.Equal(["aac"], profile.SupportedAudioCodecs);
        Assert.Equal(["vtt"], profile.SupportedSubtitleFormats);
        Assert.Equal(1080, profile.MaxHeight);
        Assert.True(profile.SupportsSubtitles);
        Assert.False(profile.SupportsOfflineDownloads);
    }

    [Fact]
    public void TvWebHevc_AddsOnlyHevcVideo()
    {
        var standard = PlaybackCapabilitiesService.ProfileFor("tv-web");
        var hevc = PlaybackCapabilitiesService.ProfileFor("tv-web-hevc");

        Assert.Equal("tv-web-hevc", hevc.Key);
        Assert.Equal(["h264", "hevc"], hevc.SupportedVideoCodecs);
        Assert.Equal(standard.PreferredDelivery, hevc.PreferredDelivery);
        Assert.Equal(standard.SupportedAudioCodecs, hevc.SupportedAudioCodecs);
        Assert.Equal(standard.SupportedContainers, hevc.SupportedContainers);
        Assert.Equal(standard.MaxHeight, hevc.MaxHeight);
    }

    [Theory]
    [InlineData("android-tv")]
    [InlineData("roku")]
    [InlineData("apple-tv")]
    [InlineData("web")]
    public void OtherProfiles_AreUnchanged(string client)
    {
        var profile = PlaybackCapabilitiesService.ProfileFor(client);

        Assert.Equal(client, profile.Key);
        Assert.NotEmpty(profile.SupportedContainers);
    }

    [Fact]
    public void UnknownClient_StillFallsBackToTheWebProfile()
    {
        Assert.Equal("web", PlaybackCapabilitiesService.ProfileFor("tv-webby").Key);
    }
}
