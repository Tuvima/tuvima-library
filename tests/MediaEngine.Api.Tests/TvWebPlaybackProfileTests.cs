using System.Security.Claims;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Authentication;
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

    private static ClaimsPrincipal PairedTv() => new(new ClaimsIdentity(
        [new Claim(TuvimaClaimTypes.ClientId, "tuvima-tv"), new Claim(TuvimaClaimTypes.DeviceId, Guid.NewGuid().ToString())],
        "test"));

    private static ClientCapabilitiesDto TvCapabilities(bool hevc) => new()
    {
        Containers = [],
        VideoCodecs = hevc ? ["h264", "hevc"] : ["h264"],
        AudioCodecs = ["aac"],
        SubtitleFormats = ["vtt"],
        MaxHeight = 1080,
    };

    [Theory]
    [InlineData(false, new[] { "h264" })]
    [InlineData(true, new[] { "h264", "hevc" })]
    public void PairedTv_GetsItsStoredCapabilities_NotTheKeyedProfile(bool hevc, string[] expectedVideo)
    {
        // The token's client id (tuvima-tv) wins over the ?client=tv-web(-hevc) the app sends.
        var client = PlaybackEndpoints.TrustedClient(PairedTv(), hevc ? "tv-web-hevc" : "tv-web");
        Assert.Equal("tuvima-tv", client);

        var profile = PlaybackCapabilitiesService.ResolveProfile(client!, TvCapabilities(hevc));

        Assert.Equal("tuvima-tv", profile.Key);
        Assert.Empty(profile.SupportedContainers); // nothing direct-plays, so every title is served as HLS
        Assert.Equal(expectedVideo, profile.SupportedVideoCodecs);
        Assert.Equal(["aac"], profile.SupportedAudioCodecs);
    }

    [Theory]
    [InlineData("tv-web", "tv-web")]
    [InlineData("tv-web-hevc", "tv-web-hevc")]
    public void CallerWithoutAClientClaimOrStoredCapabilities_GetsTheKeyedProfile(string query, string expectedKey)
    {
        var client = PlaybackEndpoints.TrustedClient(new ClaimsPrincipal(new ClaimsIdentity()), query);

        Assert.Equal(expectedKey, PlaybackCapabilitiesService.ResolveProfile(client!, null).Key);
    }
}
