using MediaEngine.Api.Services.Playback;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>The stream limit is how many test conversions together stay faster than playback with room to spare.</summary>
public sealed class PlaybackCapacityBenchmarkTests : IDisposable
{
    private MediaEngine.Storage.ConfigurationDirectoryLoader? _loader;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima_capacity_{Guid.NewGuid():N}");

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

    [Fact]
    public async Task FindStreams_CanReportZero_WhenTheCallerAllowsIt()
    {
        Assert.Equal(0, await PlaybackCapacityBenchmark.FindStreamsAsync(_ => Task.FromResult(0.4), maximum: 4, minimum: 0));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(8, 4, 16, 100)]
    [InlineData(8, 4, 200, 100)]
    [InlineData(1, 0, 2, 9)]
    [InlineData(4, 2, 8, 50)]
    public void ScoreFor_IsOutOfOneHundred_AndNeverPastIt(int streams, int fourK, double speed, int expected)
    {
        Assert.Equal(expected, PlaybackCapacityBenchmark.ScoreFor(streams, fourK, speed));
    }

    [Fact]
    public async Task Run_OnAFastComputer_ReportsStreamsFourKSpeedAndScore_AndKeepsThemForNextTime()
    {
        var benchmark = Benchmark(new FastFfmpeg(failFourK: false), out var loader);

        var answer = await benchmark.RunAsync(apply: true, CancellationToken.None);

        Assert.True(answer.Measured);
        Assert.Equal(PlaybackCapacityBenchmark.MaxStreams, answer.StreamsSupported);
        Assert.Equal(PlaybackCapacityBenchmark.MaxFourKStreams, answer.FourKStreamsSupported);
        Assert.True(answer.SpeedFactor >= PlaybackCapacityBenchmark.FullScoreSpeed);
        Assert.Equal(100, answer.Score);
        Assert.NotNull(answer.TestedAt);
        Assert.Equal(PlaybackCapacityBenchmark.MaxStreams, loader.LoadTranscoding().MaxConcurrentTranscodes);

        var kept = benchmark.Current();
        Assert.True(kept.Measured);
        Assert.Equal(answer.Score, kept.Score);
        Assert.Equal(answer.FourKStreamsSupported, kept.FourKStreamsSupported);
        Assert.Equal(answer.SpeedFactor, kept.SpeedFactor);
        Assert.Equal(answer.TestedAt, kept.TestedAt);
    }

    [Fact]
    public async Task Current_AfterARestart_StillShowsTheLastMeasuredAnswer()
    {
        var first = Benchmark(new FastFfmpeg(failFourK: false), out _);
        var answer = await first.RunAsync(apply: false, CancellationToken.None);

        var afterRestart = Benchmark(new FastFfmpeg(failFourK: false), out _).Current();

        Assert.True(afterRestart.Measured);
        Assert.Equal(answer.Score, afterRestart.Score);
        Assert.Equal(answer.TestedAt, afterRestart.TestedAt);
    }

    [Fact]
    public async Task Run_WhenFourKFails_ReportsZeroFourK_ButStillMeasuresTheRest()
    {
        var benchmark = Benchmark(new FastFfmpeg(failFourK: true), out _);

        var answer = await benchmark.RunAsync(apply: false, CancellationToken.None);

        Assert.True(answer.Measured);
        Assert.Equal(0, answer.FourKStreamsSupported);
        Assert.Equal(PlaybackCapacityBenchmark.MaxStreams, answer.StreamsSupported);
        Assert.InRange(answer.Score, 1, 99);
    }

    [Fact]
    public void Current_BeforeAnyTest_IsNotMeasured_WithNoDate()
    {
        var kept = Benchmark(new FastFfmpeg(failFourK: false), out _).Current();

        Assert.False(kept.Measured);
        Assert.Null(kept.TestedAt);
        Assert.Equal(0, kept.Score);
    }

    [Fact]
    public async Task Run_WhenTheVideoToolIsNotReady_IsNotMeasured_AndKeepsNothing()
    {
        var benchmark = Benchmark(new FastFfmpeg(failFourK: false) { Available = false }, out var loader);

        var answer = await benchmark.RunAsync(apply: true, CancellationToken.None);

        Assert.False(answer.Measured);
        Assert.Equal(0, answer.Score);
        Assert.False(benchmark.Current().Measured);
    }

    private PlaybackCapacityBenchmark Benchmark(FastFfmpeg ffmpeg, out MediaEngine.Storage.ConfigurationDirectoryLoader loader)
    {
        Directory.CreateDirectory(_root);
        loader = _loader = new MediaEngine.Storage.ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        loader.SaveTranscoding(new TranscodingSettings { HardwareAcceleration = "none" });
        return new PlaybackCapacityBenchmark(ffmpeg, loader, new MediaEngine.Domain.Services.TuvimaDataPaths(Path.Combine(_root, "data")), TimeProvider.System, NullLogger<PlaybackCapacityBenchmark>.Instance);
    }

    public void Dispose()
    {
        _loader?.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>A video tool that answers every test conversion at once, optionally failing the 4K ones.</summary>
    private sealed class FastFfmpeg(bool failFourK) : IFFmpegService
    {
        public bool Available { get; init; } = true;
        public string? FfmpegPath => null;
        public string? FfprobePath => null;
        public bool IsAvailable => Available;
        public HardwareCapabilities HardwareCapabilities { get; } = new();
        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) => Task.FromResult<MediaProbeResult?>(null);

        public async Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default)
        {
            await Task.Delay(2, ct);
            return failFourK && arguments.Contains("3840x2160", StringComparison.Ordinal) ? (1, string.Empty, "too big") : (0, string.Empty, string.Empty);
        }
    }
}
