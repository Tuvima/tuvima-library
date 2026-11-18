using System.Text.Json;
using MediaEngine.Contracts.Artwork;

namespace MediaEngine.Contracts.Tests;

/// <summary>The frames of a video the editor's Artwork tab offers (review mock-ups 2026-11-18, D2).</summary>
public sealed class VideoFrameContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void TheFrameStrip_RoundTripsWithItsWireNames()
    {
        var dto = new VideoFramesDto([new VideoFrameDto("3", 120.5, "/metadata/x/video-frames/video/3", true, "video-frame:120500")], "Some frames were dark.");

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<VideoFramesDto>(json, JsonOptions);

        Assert.Equal(
            "{\"items\":[{\"id\":\"3\",\"seconds\":120.5,\"thumbnailUrl\":\"/metadata/x/video-frames/video/3\",\"isBest\":true,\"sourceKey\":\"video-frame:120500\"}],\"message\":\"Some frames were dark.\"}",
            json);
        Assert.Equal("3", roundTrip!.Items.Single().Id);
        Assert.True(roundTrip.Items.Single().IsBest);
        Assert.Equal("Some frames were dark.", roundTrip.Message);
    }

    [Theory]
    [InlineData(0, "video-frame:0")]
    [InlineData(10, "video-frame:10000")]
    [InlineData(120.5, "video-frame:120500")]
    [InlineData(3725.0004, "video-frame:3725000")]
    public void AFramesSourceKey_NamesTheMomentItWasTakenAt_InWholeMilliseconds(double seconds, string expected) =>
        Assert.Equal(expected, VideoFrameSources.SourceKeyFor(seconds));

    [Fact]
    public void AFramePicture_IsRecordedAsGeneratedByTheEngine() =>
        Assert.Equal("generated", VideoFrameSources.GeneratedProvider);
}
