using System.Diagnostics;
using System.Text.Json;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;

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
    TuvimaDataPaths dataPaths,
    TimeProvider clock,
    ILogger<PlaybackCapacityBenchmark> logger)
{
    private static readonly JsonSerializerOptions StoreOptions = new(JsonSerializerDefaults.Web);

    private readonly object _storeLock = new();

    /// <summary>Where the last measured answer is kept, so Settings can show it again after a restart.</summary>
    private string StorePath => Path.Combine(dataPaths.Root, "playback-capacity.json");

    /// <summary>Seconds of video each test conversion makes.</summary>
    internal const int TestSeconds = 8;

    /// <summary>A conversion must run at least this many times faster than playback to count (room for busy moments).</summary>
    internal const double RequiredSpeed = 1.3;

    /// <summary>The most conversions at once the Engine will ever allow.</summary>
    internal const int MaxStreams = 8;

    /// <summary>The most 4K conversions at once the test looks for (a 4K picture is four times the work of 1080p).</summary>
    internal const int MaxFourKStreams = 4;

    /// <summary>Conversion speed (times faster than playback) at or above which the speed part of the score is full.</summary>
    internal const double FullScoreSpeed = 16;

    private static readonly TimeSpan OneRunLimit = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The number of streams a machine keeps up with, given how fast <paramref name="slowestSpeedAt"/> says the slowest of n
    /// simultaneous conversions ran (1.0 is exactly real time). Stops at the first count that falls short.
    /// </summary>
    internal static async Task<int> FindStreamsAsync(Func<int, Task<double>> slowestSpeedAt, int maximum, int minimum = 1)
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

        return Math.Max(minimum, supported);
    }

    /// <summary>
    /// The one number out of 100 drawn as the gauge: 40 points for how many 1080p conversions run at once (of <see cref="MaxStreams"/>),
    /// 30 for how many 4K conversions (of <see cref="MaxFourKStreams"/>) and 30 for how much faster than playback one conversion runs
    /// (full at <see cref="FullScoreSpeed"/> times). A test that did not run scores 0.
    /// </summary>
    internal static int ScoreFor(int streams, int fourKStreams, double speedFactor)
    {
        var points = 0.4 * Math.Clamp(streams, 0, MaxStreams) / MaxStreams
            + 0.3 * Math.Clamp(fourKStreams, 0, MaxFourKStreams) / MaxFourKStreams
            + 0.3 * Math.Clamp(speedFactor, 0, FullScoreSpeed) / FullScoreSpeed;
        return (int)Math.Round(100 * points, MidpointRounding.AwayFromZero);
    }

    /// <summary>The answer from the last test (kept in the Engine's data folder), or an unmeasured answer when it has never run.</summary>
    public PlaybackCapacityDto Current()
    {
        var last = ReadStored();
        if (last is { Measured: true })
        {
            return last with
            {
                StreamsSupported = Math.Clamp(last.StreamsSupported, 1, MaxStreams),
                FourKStreamsSupported = Math.Clamp(last.FourKStreamsSupported, 0, MaxFourKStreams),
                Score = Math.Clamp(last.Score, 0, 100),
            };
        }

        var settings = configuration.LoadTranscoding();
        var choice = AdaptiveHlsService.ChooseEncoder(settings.HardwareAcceleration, ffmpeg.HardwareCapabilities);
        var onCard = choice.RanOn == PlaybackEncodePlaces.GraphicsCard;
        return new PlaybackCapacityDto
        {
            StreamsSupported = Math.Clamp(settings.MaxConcurrentTranscodes, 1, MaxStreams),
            Measured = false,
            RanOn = onCard ? PlaybackEncodePlaces.GraphicsCard : PlaybackEncodePlaces.Processor,
            CardName = onCard ? ffmpeg.HardwareCapabilities.GpuName : null,
        };
    }

    private PlaybackCapacityDto? ReadStored()
    {
        try
        {
            lock (_storeLock)
            {
                return File.Exists(StorePath)
                    ? JsonSerializer.Deserialize<PlaybackCapacityDto>(File.ReadAllText(StorePath), StoreOptions)
                    : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "The last computer capacity test could not be read; showing it as not tested");
            return null;
        }
    }

    private void WriteStored(PlaybackCapacityDto result)
    {
        try
        {
            lock (_storeLock)
            {
                Directory.CreateDirectory(dataPaths.Root);
                File.WriteAllText(StorePath, JsonSerializer.Serialize(result, StoreOptions));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The computer capacity test result could not be kept; it will show as not tested after a restart");
        }
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
        var speedAtOne = 0.0;
        try
        {
            streams = await FindStreamsAsync(async count =>
            {
                var speed = await SlowestSpeedAsync(count, choice.Encoder, Full, ct).ConfigureAwait(false);
                if (count == 1)
                {
                    speedAtOne = speed;
                }

                return speed;
            }, MaxStreams).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result(1, false, onCard, "The test took too long to finish.", started);
        }

        // 4K is only tried when one 1080p conversion kept up, and never needs more than the 1080p count. A 4K run that is too slow to
        // finish in time simply means this computer does not manage 4K.
        var fourK = 0;
        if (speedAtOne >= RequiredSpeed)
        {
            try
            {
                fourK = await FindStreamsAsync(count => SlowestSpeedAsync(count, choice.Encoder, FourK, ct),
                    Math.Min(streams, MaxFourKStreams), minimum: 0).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                fourK = 0;
            }
        }

        var score = ScoreFor(streams, fourK, speedAtOne);
        if (apply)
        {
            settings.MaxConcurrentTranscodes = streams;
            configuration.SaveTranscoding(settings);
        }

        var result = Result(streams, true, onCard, onCard ? null : choice.FallbackReason, started, fourK, speedAtOne, score);
        WriteStored(result);

        logger.LogInformation("Playback capacity measured: {Streams} stream(s) at once, {FourK} in 4K, {Speed:0.0}x, score {Score} on the {Place} ({Seconds}s)",
            streams, fourK, speedAtOne, score, onCard ? "graphics card" : "processor", (int)started.Elapsed.TotalSeconds);
        return result;
    }

    private PlaybackCapacityDto Result(
        int streams, bool measured, bool onCard, string? reason, Stopwatch started, int fourK = 0, double speed = 0, int score = 0) => new()
        {
            FourKStreamsSupported = fourK,
            SpeedFactor = Math.Round(speed, 1),
            Score = score,
            StreamsSupported = Math.Clamp(streams, 1, MaxStreams),
            Measured = measured,
            RanOn = onCard ? PlaybackEncodePlaces.GraphicsCard : PlaybackEncodePlaces.Processor,
            CardName = onCard ? ffmpeg.HardwareCapabilities.GpuName : null,
            Reason = reason,
            SecondsTaken = (int)started.Elapsed.TotalSeconds,
            TestedAt = clock.GetUtcNow(),
        };

    /// <summary>Runs <paramref name="count"/> test conversions together and returns the speed of the slowest (0 when one failed).</summary>
    private async Task<double> SlowestSpeedAsync(int count, string encoder, TestPicture picture, CancellationToken ct)
    {
        var runs = Enumerable.Range(0, count).Select(_ => OneRunAsync(encoder, picture, ct)).ToArray();
        var speeds = await Task.WhenAll(runs).ConfigureAwait(false);
        return speeds.Min();
    }

    /// <summary>The generated test picture: its size and the bitrate a real conversion of that size would use.</summary>
    private readonly record struct TestPicture(int Width, int Height, int BitrateKbps);

    private static readonly TestPicture Full = new(1920, 1080, 6000);
    private static readonly TestPicture FourK = new(3840, 2160, 16000);

    private async Task<double> OneRunAsync(string encoder, TestPicture picture, CancellationToken ct)
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
            "-f", "lavfi", "-i", $"testsrc2=size={picture.Width}x{picture.Height}:rate=30:duration={TestSeconds}",
            "-vf", "format=yuv420p", "-c:v", encoder, "-b:v", $"{picture.BitrateKbps}k",
            "-maxrate", $"{picture.BitrateKbps * 11 / 10}k", "-bufsize", $"{picture.BitrateKbps * 2}k",
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
