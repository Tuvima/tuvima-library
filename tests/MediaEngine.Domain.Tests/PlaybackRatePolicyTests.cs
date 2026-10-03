using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class PlaybackRatePolicyTests
{
    [Theory]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.25)]
    [InlineData(1.75)]
    [InlineData(3)]
    public void NormalPlaybackRates_AreAcceptedExactly(double rate) =>
        Assert.Equal(rate, PlaybackRatePolicy.RequireValid(rate));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.49)]
    [InlineData(3.01)]
    public void NormalPlaybackRates_RejectInvalidWithoutCoercion(double rate)
    {
        Assert.False(PlaybackRatePolicy.IsValid(rate));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlaybackRatePolicy.RequireValid(rate));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public void ScanRates_KeepTheirSeparateRange(double rate) =>
        Assert.True(PlaybackRatePolicy.IsValidScan(rate));

    [Fact]
    public void NormalPlaybackPolicy_DoesNotAcceptScanRates() =>
        Assert.False(PlaybackRatePolicy.IsValid(4));
}
