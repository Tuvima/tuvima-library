using System.Diagnostics;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Measures how many videos this computer can convert at the same time and still keep up with playback, with the encoder
/// Library would really use (graphics card first, processor otherwise). It starts one test conversion, then adds one more at a
/// time until any of them falls behind real time (with a safety margin). The answer becomes the default for "Maximum active encodes".
/// It is an estimate: the test video is generated, so the real number also depends on the sources that people play.
/// </summary>
public sealed class PlaybackCapacityBenchmark(
    IFFmpegService ffmpeg,
    IConfigurationLoader configuration,
    TimeProvider clock,
    ILogger<PlaybackCapacityBenchmark> logger)
{
    /// <summary>Seconds of video each test conversion makes.</summary>
    internal const int TestSeconds = 8;

    /// <summary>A conversion must run at least this many times faster than playback to count (room for busy moments).</summary>
    internal const double RequiredSpeed = 1.3;

    /// <summary>The most conversions at once the Engine will ever allow.</summary>
    internal const int MaxStreams = 8;

    private static readonly TimeSpan OneRunLimit = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The number of streams a machine keeps up with, given how fast <paramref name="slowestSpeedAt"/> says the slowest of n
    /// simultaneous conversions ran (1.0 is exactly real time). Stops at the first count that falls short.
    /// </summary>
    internal static async Task<int> FindStreamsAsync(Func<int, Task<double>> slowestSpeedAt, int maximum)
    {
        var supported = 0;
        for (var streams = 1; streams <= maximum; streams++)
        {
            if (await slowestSpeedAt(streams).ConfigureAwait(false) < RequiredSpeed)
            {
                break;
            }

            supported = streams;
        }

        return Math.Max(1, supported);
    }

    public async Task<PlaybackCapacityDto> RunAsync(bool apply, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var settings = configuration.LoadTranscoding();
        var choice = AdaptiveHlsService.ChooseEncoder(settings.HardwareAcceleration, ffmpeg.HardwareCapabilities);
        var onCard = choice.RanOn == PlaybackEncodePlaces.GraphicsCard;
        if (!ffmpeg.IsAvailable)
        {
            return Result(1, false, onCard, "The video tool is not ready yet, so nothing could be measured.", started);
        }

        int streams;
        try
        {
            streams = await FindStreamsAsync(count => SlowestSpeedAsync(count, choice.Encoder, ct), MaxStreams).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result(1, false, onCard, "The test took too long to finish.", started);
        }

        if (apply)
        {
            settings.MaxConcurrentTranscodes = streams;
            configuration.SaveTranscoding(settings);
        }

        logger.LogInformation("Playback capacity measured: {Streams} stream(s) at once on the {Place} ({Seconds}s)",
            streams, onCard ? "graphics card" : "processor", (int)started.Elapsed.TotalSeconds);
        return Result(streams, true, onCard, onCard ? null : choice.FallbackReason, started);
    }

    private PlaybackCapacityDto Result(int streams, bool measured, bool onCard, string? reason, Stopwatch started) => new()
    {
        StreamsSupported = Math.Clamp(streams, 1, MaxStreams),
        Measured = measured,
        RanOn = onCard ? PlaybackEncodePlaces.GraphicsCard : PlaybackEncodePlaces.Processor,
        CardName = onCard ? ffmpeg.HardwareCapabilities.GpuName : null,
        Reason = reason,
        SecondsTaken = (int)started.Elapsed.TotalSeconds,
        TestedAt = clock.GetUtcNow(),
    };

    /// <summary>Runs <paramref name="count"/> test conversions together and returns the speed of the slowest (0 when one failed).</summary>
    private async Task<double> SlowestSpeedAsync(int count, string encoder, CancellationToken ct)
    {
        var runs = Enumerable.Range(0, count).Select(_ => OneRunAsync(encoder, ct)).ToArray();
        var speeds = await Task.WhenAll(runs).ConfigureAwait(false);
        return speeds.Min();
    }

    private async Task<double> OneRunAsync(string encoder, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(OneRunLimit);
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (encoder != "libx264")
        {
            arguments.AddRange(["-hwaccel", "auto"]);
        }

        arguments.AddRange(
        [
            "-f", "lavfi", "-i", $"testsrc2=size=1920x1080:rate=30:duration={TestSeconds}",
            "-vf", "format=yuv420p", "-c:v", encoder, "-b:v", "6000k", "-maxrate", "6600k", "-bufsize", "12000k",
        ]);
        if (encoder == "libx264")
        {
            arguments.AddRange(["-preset", "veryfast", "-profile:v", "main"]);
        }

        arguments.AddRange(["-f", "null", "-"]);
        var clockWatch = Stopwatch.StartNew();
        var (exitCode, _, _) = await ffmpeg.RunAsync(arguments, limit.Token).ConfigureAwait(false);
        clockWatch.Stop();
        return exitCode == 0 && clockWatch.Elapsed.TotalSeconds > 0 ? TestSeconds / clockWatch.Elapsed.TotalSeconds : 0;
    }
}
