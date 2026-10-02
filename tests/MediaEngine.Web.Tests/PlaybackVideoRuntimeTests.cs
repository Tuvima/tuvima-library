using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackVideoRuntimeTests
{
    [Fact]
    public void LibraryDetailBareRuntimeUsesMinutesInPlaybackQueue()
    {
        var duration = PlaybackVideoRuntime.NormalizeLibraryDetailRuntime("24");

        Assert.Equal("24:00", duration);
        Assert.Equal(24 * 60, PlaybackTimeParser.TryParseDurationSeconds(duration));
        Assert.Equal("24 min", PlaybackVideoRuntime.FormatQueueRuntime(duration));
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("", null)]
    [InlineData("unknown", null)]
    public void InvalidOrNonpositiveDetailRuntimeIsOmitted(string value, string? expected)
    {
        Assert.Equal(expected, PlaybackVideoRuntime.NormalizeLibraryDetailRuntime(value));
    }

    [Fact]
    public void ExplicitShortClockRuntimeKeepsItsSeconds()
    {
        var duration = PlaybackVideoRuntime.NormalizeLibraryDetailRuntime("0:24");

        Assert.Equal("0:24", duration);
        Assert.Equal("0:24", PlaybackVideoRuntime.FormatQueueRuntime(duration));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-4")]
    [InlineData("unknown")]
    public void QueueRuntimeOmitsMissingNonpositiveAndUnparseableValues(string? value)
    {
        Assert.Null(PlaybackVideoRuntime.FormatQueueRuntime(value));
    }
}
