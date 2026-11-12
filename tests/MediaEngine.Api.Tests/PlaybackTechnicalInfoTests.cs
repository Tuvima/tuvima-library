using MediaEngine.Api.Services.Playback;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

/// <summary>Playback information facts the Engine sends with the manifest (video-player follow-ups D13): file name only, size, colour format and video profile.</summary>
public sealed class PlaybackTechnicalInfoTests
{
    [Theory]
    [InlineData("smpte2084", "HDR10")]
    [InlineData("SMPTE2084", "HDR10")]
    [InlineData("arib-std-b67", "HLG")]
    [InlineData("bt709", "SDR")]
    [InlineData("smpte170m", "SDR")]
    [InlineData("bt2020-10", "SDR")]
    [InlineData("unknown", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ColorFormatFor_NamesSdrHdr10AndHlgAndLeavesAnUnknownTransferOut(string? transfer, string? expected)
    {
        Assert.Equal(expected, PlaybackCapabilitiesService.ColorFormatFor(transfer));
    }

    [Theory]
    [InlineData(@"D:\Movies\Dune (2021)\Dune.mkv", "Dune.mkv")]
    [InlineData("/media/movies/Dune (2021)/Dune.mkv", "Dune.mkv")]
    [InlineData("Dune.mkv", "Dune.mkv")]
    [InlineData(@"\\nas\share\Films\Arrival.mp4", "Arrival.mp4")]
    public void BuildTechnicalInfo_FileNameNeverHoldsAFolderOrADirectorySeparator(string path, string expected)
    {
        var technical = PlaybackCapabilitiesService.BuildTechnicalInfo(".mkv", new MediaProbeResult(), path);

        Assert.Equal(expected, technical.FileName);
        Assert.DoesNotContain('/', technical.FileName!);
        Assert.DoesNotContain('\\', technical.FileName!);
    }

    [Fact]
    public void BuildTechnicalInfo_LeavesTheFileNameOutWhenThereIsNoPath()
    {
        Assert.Null(PlaybackCapabilitiesService.BuildTechnicalInfo(".mkv", null, null).FileName);
        Assert.Null(PlaybackCapabilitiesService.BuildTechnicalInfo(".mkv", null, "  ").FileName);
    }

    [Fact]
    public void BuildTechnicalInfo_PassesThroughSizeColourAndProfileFromTheProbe()
    {
        var probe = new MediaProbeResult
        {
            FileSizeBytes = 4_800_000_000,
            ColorTransfer = "smpte2084",
            VideoProfile = "Main 10",
            VideoCodec = "hevc",
            Width = 3840,
            Height = 2160,
        };

        var technical = PlaybackCapabilitiesService.BuildTechnicalInfo(".mkv", probe, "Dune.mkv");

        Assert.Equal(4_800_000_000, technical.FileSizeBytes);
        Assert.Equal("HDR10", technical.ColorFormat);
        Assert.Equal("Main 10", technical.VideoProfile);
    }

    [Fact]
    public void BuildTechnicalInfo_LeavesSizeColourAndProfileNullWhenTheProbeDoesNotKnowThem()
    {
        var technical = PlaybackCapabilitiesService.BuildTechnicalInfo(".mp3", new MediaProbeResult { AudioCodec = "mp3" }, "Song.mp3");

        Assert.Null(technical.FileSizeBytes);
        Assert.Null(technical.ColorFormat);
        Assert.Null(technical.VideoProfile);
    }
}
