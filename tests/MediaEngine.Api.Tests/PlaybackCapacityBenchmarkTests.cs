using MediaEngine.Api.Services.Playback;

namespace MediaEngine.Api.Tests;

/// <summary>The stream limit is how many test conversions together stay faster than playback with room to spare.</summary>
public sealed class PlaybackCapacityBenchmarkTests
{
    [Fact]
    public async Task FindStreams_StopsAtTheFirstCountThatFallsBehind()
    {
        // One stream runs at 6x, each added stream shares the same total work.
        var streams = await PlaybackCapacityBenchmark.FindStreamsAsync(count => Task.FromResult(6.0 / count), maximum: 8);

        Assert.Equal(4, streams); // 6/4 = 1.5 keeps up; 6/5 = 1.2 is under the 1.3 margin.
    }

    [Fact]
    public async Task FindStreams_NeverReportsLessThanOne()
    {
        Assert.Equal(1, await PlaybackCapacityBenchmark.FindStreamsAsync(_ => Task.FromResult(0.4), maximum: 8));
    }

    [Fact]
    public async Task FindStreams_NeverGoesPastTheMaximum()
    {
        Assert.Equal(8, await PlaybackCapacityBenchmark.FindStreamsAsync(_ => Task.FromResult(50.0), maximum: 8));
    }
}
