using System.Diagnostics;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Processors;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace MediaEngine.Processors.Tests;

public sealed class VideoStillFrameServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-stills-{Guid.NewGuid():N}");

    public VideoStillFrameServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static byte[] Image(Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(320, 180);
        using var canvas = new SKCanvas(bitmap);
        draw(canvas);
        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static byte[] Busy() => Image(canvas =>
    {
        canvas.Clear(SKColors.DarkSlateBlue);
        using var paint = new SKPaint { IsAntialias = true };
        var random = new Random(7);
        for (var i = 0; i < 80; i++)
        {
            paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            canvas.DrawRect(random.Next(300), random.Next(160), 20, 20, paint);
        }
    });

    [Fact]
    public void BlackAndUniformFrames_AreRejected()
    {
        Assert.Null(VideoStillFrameService.Score(Image(canvas => canvas.Clear(SKColors.Black))));
        Assert.Null(VideoStillFrameService.Score(Image(canvas => canvas.Clear(SKColors.Gray))));
    }

    [Fact]
    public void ABusyColourfulFrame_Scores()
    {
        Assert.NotNull(VideoStillFrameService.Score(Busy()));
    }

    [Fact]
    public async Task VideoThatOpensOnBlack_GetsANonBlackStill_AndTheSecondCallReusesIt()
    {
        var ffmpeg = FfmpegPath();
        if (ffmpeg is null)
        {
            return; // The synthetic-frame tests above carry the logic; this one needs a real ffmpeg.
        }

        var video = Path.Combine(_root, "intro.mp4");
        // 3 s of black followed by 9 s of colourful test pattern.
        Run(ffmpeg, $"-v error -y -f lavfi -i color=black:s=320x180:d=3:r=10 -f lavfi -i testsrc2=s=320x180:d=9:r=10 -filter_complex [0:v][1:v]concat=n=2:v=1 -pix_fmt yuv420p \"{video}\"");
        Assert.True(File.Exists(video));
        var service = new VideoStillFrameService(new Ffmpeg(ffmpeg), Path.Combine(_root, "cache"));

        var first = await service.GetOrCreateAsync(video);
        var second = await service.GetOrCreateAsync(video);

        Assert.NotNull(first);
        Assert.NotNull(VideoStillFrameService.Score(first!.Bytes));
        Assert.Equal(first.SourcePath, second!.SourcePath);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "cache")));
    }

    /// <summary>Issue 997: the frame step used to log nothing, so a skipped frame could not be seen in the Engine log.</summary>
    [Fact]
    public async Task TheFrameStep_SaysInTheLogWhyItMadeNoFrame_WithoutTheFolderPath()
    {
        var video = Path.Combine(_root, "clip.mp4");
        await File.WriteAllBytesAsync(video, [1, 2, 3]);
        var log = new Capture();

        Assert.Null(await new VideoStillFrameService(null, Path.Combine(_root, "cache"), log).GetOrCreateAsync(video));
        Assert.Null(await new VideoStillFrameService(null, Path.Combine(_root, "cache"), log).GetOrCreateAsync(Path.Combine(_root, "gone.mp4")));

        Assert.Contains(log.Lines, line => line.Contains("FFmpeg is not available", StringComparison.Ordinal) && line.Contains("clip.mp4", StringComparison.Ordinal));
        Assert.Contains(log.Lines, line => line.Contains("gone.mp4", StringComparison.Ordinal) && line.Contains("not found", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, line => line.Contains(_root, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFrameStep_SaysInTheLogWhenItReusesASavedFrame()
    {
        var video = Path.Combine(_root, "again.mp4");
        await File.WriteAllBytesAsync(video, [1, 2, 3]);
        var cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(cache);
        var key = typeof(VideoStillFrameService).GetMethod("CacheKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        await File.WriteAllBytesAsync(Path.Combine(cache, (string)key.Invoke(null, [video])! + ".jpg"), Busy());
        var log = new Capture();

        Assert.NotNull(await new VideoStillFrameService(null, cache, log).GetOrCreateAsync(video));

        Assert.Contains(log.Lines, line => line.Contains("reused", StringComparison.Ordinal) && line.Contains("again.mp4", StringComparison.Ordinal));
    }

    private sealed class Capture : ILogger<VideoStillFrameService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    private static string? FfmpegPath() =>
        new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg" }.FirstOrDefault(File.Exists);

    private static void Run(string exe, string args)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardError = true, RedirectStandardOutput = true })!;
        process.WaitForExit(60_000);
    }

    private sealed class Ffmpeg(string path) : IFFmpegService
    {
        public string? FfmpegPath => path;
        public string? FfprobePath => null;
        public bool IsAvailable => true;
        public HardwareCapabilities HardwareCapabilities => throw new NotSupportedException();
        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<MediaProbeResult?>(new MediaProbeResult { Duration = TimeSpan.FromSeconds(12) });

        public async Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default)
        {
            using var process = Process.Start(new ProcessStartInfo(path, arguments) { RedirectStandardError = true, RedirectStandardOutput = true })!;
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, string.Empty, string.Empty);
        }
    }
}
