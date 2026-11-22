using MediaEngine.Api.Services.Playback;

namespace MediaEngine.Api.Tests;

public sealed class PlaybackStreamStartTests
{
    [Theory]
    [InlineData(null, 6700.0, 0.0)]
    [InlineData(0.0, 6700.0, 0.0)]
    [InlineData(3.0, 6700.0, 0.0)]
    [InlineData(6000.0, 6700.0, 6000.0)]
    [InlineData(6003.9, 6700.0, 6000.0)]
    [InlineData(6699.0, 6700.0, 6696.0)]
    [InlineData(9000.0, 6700.0, 6696.0)]
    [InlineData(-5.0, 6700.0, 0.0)]
    public void AStartIsTheWantedPointRoundedDownToASegment_NeverInTheLastSegment(double? wanted, double duration, double expected) =>
        Assert.Equal(expected, PlaybackCapabilitiesService.StreamStartFor(wanted, duration, 4));

    [Fact]
    public void AnUnknownLengthStillRoundsDownToASegment() =>
        Assert.Equal(600, PlaybackCapabilitiesService.StreamStartFor(601.5, null, 4));

    [Fact]
    public void ABrokenSegmentLengthMeansNoStart() =>
        Assert.Equal(0, PlaybackCapabilitiesService.StreamStartFor(600, 6700, 0));
}
