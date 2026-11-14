using MediaEngine.Api.Services.Playback;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

public sealed class PlaybackLadderTests
{
    private static PlaybackProfileDto Web => PlaybackCapabilitiesService.ProfileFor("web");

    private static PlaybackDecision Decide(string extension, string video, string audio, int height = 1080,
        PlaybackProfileDto? profile = null, string? pixelFormat = null, bool forceCopy = false, bool forceEncode = false) =>
        PlaybackLadder.Decide(new PlaybackLadderInput(
            extension,
            profile ?? Web,
            new MediaProbeResult { VideoCodec = video, AudioCodec = audio, Height = height, PixelFormat = pixelFormat },
            forceCopy,
            forceEncode,
            forceEncode ? "Remote streaming quality is capped at 720p for this connection." : null));

    [Fact]
    public void Mp4WithH264AndAac_PlaysDirectly() =>
        Assert.Equal(PlaybackRungs.Direct, Decide(".mp4", "h264", "aac").Rung);

    [Fact]
    public void MkvWithH264AndAac_IsRepackagedWithNothingEncoded()
    {
        var decision = Decide(".mkv", "h264", "aac");

        Assert.Equal(PlaybackRungs.Repackage, decision.Rung);
        Assert.True(decision.CopyVideo);
        Assert.Equal("MKV is not a Web container; video copied, audio copied", decision.FullReason);
    }

    [Fact]
    public void MkvWithHevcAndDts_ForABrowserWithoutHevc_EncodesThePicture()
    {
        var decision = Decide(".mkv", "hevc", "dts");

        Assert.Equal(PlaybackRungs.EncodePicture, decision.Rung);
        Assert.False(decision.CopyVideo);
    }

    [Fact]
    public void MkvWithHevcAndDts_ForAPlayerThatReportsHevc_OnlyConvertsTheSound()
    {
        var decision = Decide(".mkv", "hevc", "dts", profile: Web with { SupportedVideoCodecs = ["h264", "hevc"] });

        Assert.Equal(PlaybackRungs.ConvertSound, decision.Rung);
        Assert.True(decision.CopyVideo);
    }

    [Fact]
    public void FourKHevc_ForAPlayerCappedAt1080p_EncodesThePicture()
    {
        var decision = Decide(".mp4", "hevc", "aac", 2160,
            Web with { SupportedVideoCodecs = ["h264", "hevc"], MaxHeight = 1080 });

        Assert.Equal(PlaybackRungs.EncodePicture, decision.Rung);
        Assert.Contains("1080p", decision.Reason);
    }

    [Fact]
    public void Mp4WithAc3_ConvertsTheSoundOnly()
    {
        var decision = Decide(".mp4", "h264", "ac3");

        Assert.Equal(PlaybackRungs.ConvertSound, decision.Rung);
        Assert.True(decision.CopyVideo);
    }

    [Fact]
    public void TenBitH264_IsEncodedBecauseBrowsersDoNotPlayIt() =>
        Assert.Equal(PlaybackRungs.EncodePicture, Decide(".mkv", "h264", "aac", pixelFormat: "yuv420p10le").Rung);

    [Fact]
    public void ASlowRemoteConnection_EncodesEvenAFileThatCouldPlayDirectly()
    {
        var decision = Decide(".mp4", "h264", "aac", forceEncode: true);

        Assert.Equal(PlaybackRungs.EncodePicture, decision.Rung);
        Assert.Contains("capped at 720p", decision.Reason);
    }

    [Fact]
    public void AnAlternateAudioTrackOnADirectFile_GetsARepackageNotAnEncode()
    {
        var decision = Decide(".mp4", "h264", "aac", forceCopy: true);

        Assert.Equal(PlaybackRungs.Repackage, decision.Rung);
        Assert.True(decision.CopyVideo);
    }

    [Fact]
    public void TheFirstWorkingRungWins_AnEncodeIsNeverChosenWhenACopyWorks()
    {
        var tv = PlaybackCapabilitiesService.ProfileFor("tv-web");

        Assert.Equal(PlaybackRungs.Repackage, Decide(".mkv", "h264", "aac", profile: tv).Rung);
    }

    [Fact]
    public void WaitingLabels_NeverSayOnlyPreparingVideo()
    {
        Assert.Equal("Getting this ready · converting sound", PlaybackPathBuilder.LabelFor(PlaybackRungs.ConvertSound, PlaybackEncodePlaces.None, null));
        Assert.Equal("Preparing picture · using your graphics card", PlaybackPathBuilder.LabelFor(PlaybackRungs.EncodePicture, PlaybackEncodePlaces.GraphicsCard, null));
        Assert.Equal("Preparing picture · using processor: graphics card unavailable",
            PlaybackPathBuilder.LabelFor(PlaybackRungs.EncodePicture, PlaybackEncodePlaces.Processor, "No graphics card encoding was found"));
    }
}
