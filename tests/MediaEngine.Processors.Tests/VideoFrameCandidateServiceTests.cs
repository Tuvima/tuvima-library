using System.Globalization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Processors;
using SkiaSharp;

namespace MediaEngine.Processors.Tests;

public sealed class VideoFrameCandidateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-strip-{Guid.NewGuid():N}");
    private readonly string _video;

    public VideoFrameCandidateServiceTests()
    {
        Directory.CreateDirectory(_root);
        _video = Path.Combine(_root, "lesson.mp4");
        File.WriteAllBytes(_video, [1, 2, 3, 4]);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private VideoFrameCandidateService Service(FakeFfmpeg ffmpeg) =>
        new(new VideoFrameExtractor(ffmpeg), ffmpeg, Path.Combine(_root, "cache"));

    [Fact]
    public void SampleSeconds_SpreadsEightMomentsOverTheVideo_NeverAtTheVeryStartOrEnd()
    {
        var points = VideoFrameCandidateService.SampleSeconds(90);

        Assert.Equal(VideoFrameCandidateService.SampleCount, points.Count);
        Assert.True(points[0] > 0);
        Assert.True(points[^1] < 90);
        Assert.Equal(points.Order().ToList(), points);
        Assert.Empty(VideoFrameCandidateService.SampleSeconds(0));
    }

    [Fact]
    public void PickBest_TakesTheHighestScore_AndTheEarliestWhenTwoTie()
    {
        Assert.Equal<int?>(2, VideoFrameCandidateService.PickBest([(0, 1.0), (1, 2.0), (2, 5.0), (3, 5.0)]));
        Assert.Null(VideoFrameCandidateService.PickBest([]));
        Assert.Equal<int?>(4, VideoFrameCandidateService.PickBest([(4, 3.0), (7, 3.0)]));
    }

    [Fact]
    public async Task AVideo_GivesOneFramePerUsableMoment_WithExactlyOneMarkedBest()
    {
        var ffmpeg = new FakeFfmpeg(TimeSpan.FromSeconds(90));

        var frames = await Service(ffmpeg).GetAsync(_video);

        Assert.Equal(VideoFrameCandidateService.SampleCount, frames.Count);
        Assert.Single(frames, frame => frame.IsBest);
        Assert.Equal(frames.Max(frame => frame.Score), frames.Single(frame => frame.IsBest).Score);
        Assert.All(frames, frame => Assert.NotNull(VideoStillFrameService.Score(frame.Bytes)));
    }

    [Fact]
    public async Task BlackFrames_AreLeftOutOfTheStrip()
    {
        var ffmpeg = new FakeFfmpeg(TimeSpan.FromSeconds(90)) { BlackAtOrBefore = 30 };

        var frames = await Service(ffmpeg).GetAsync(_video);

        Assert.NotEmpty(frames);
        Assert.True(frames.Count < VideoFrameCandidateService.SampleCount);
        Assert.All(frames, frame => Assert.True(frame.Seconds > 30));
    }

    [Fact]
    public async Task TheSecondRead_ReusesTheSavedFrames_AndDoesNotRunFfmpegAgain()
    {
        var ffmpeg = new FakeFfmpeg(TimeSpan.FromSeconds(90));
        var service = Service(ffmpeg);

        var first = await service.GetAsync(_video);
        var runsAfterFirst = ffmpeg.Runs;
        var second = await service.GetAsync(_video);

        Assert.Equal(VideoFrameCandidateService.SampleCount, runsAfterFirst);
        Assert.Equal(runsAfterFirst, ffmpeg.Runs);
        Assert.Equal(first.Select(frame => frame.Index), second.Select(frame => frame.Index));
        Assert.Equal(first.Single(frame => frame.IsBest).Index, second.Single(frame => frame.IsBest).Index);
    }

    [Fact]
    public async Task OneFrameCanBeReadByItsIndex_AndAnUnknownIndexIsNull()
    {
        var service = Service(new FakeFfmpeg(TimeSpan.FromSeconds(90)));

        var frame = await service.GetOneAsync(_video, 3);

        Assert.NotNull(frame);
        Assert.Equal(3, frame!.Index);
        Assert.Null(await service.GetOneAsync(_video, 99));
    }

    [Fact]
    public async Task AMissingFile_OrNoFfmpeg_GivesNoFrames()
    {
        Assert.Empty(await Service(new FakeFfmpeg(TimeSpan.FromSeconds(90))).GetAsync(Path.Combine(_root, "gone.mp4")));
        Assert.Empty(await Service(new FakeFfmpeg(TimeSpan.FromSeconds(90)) { Available = false }).GetAsync(_video));
    }

    private static byte[] Busy(int seed)
    {
        using var bitmap = new SKBitmap(320, 180);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.DarkSlateBlue);
        using var paint = new SKPaint { IsAntialias = true };
        var random = new Random(seed);
        for (var i = 0; i < 40 + seed; i++)
        {
            paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            canvas.DrawRect(random.Next(300), random.Next(160), 20, 20, paint);
        }

        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static byte[] Black()
    {
        using var bitmap = new SKBitmap(320, 180);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    // Writes a picture where the real FFmpeg would: the last argument is the frame's file; "-ss" says which moment it is.
    private sealed class FakeFfmpeg(TimeSpan duration) : IFFmpegService
    {
        public bool Available { get; init; } = true;

        public double BlackAtOrBefore { get; init; } = -1;

        public int Runs { get; private set; }

        public string? FfmpegPath => "fake";

        public string? FfprobePath => null;

        public bool IsAvailable => Available;

        public HardwareCapabilities HardwareCapabilities => throw new NotSupportedException();

        public Task<MediaProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default) =>
            Task.FromResult<MediaProbeResult?>(new MediaProbeResult { Duration = duration });

        public Task<(int ExitCode, string Output, string Error)> RunAsync(string arguments, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async Task<(int ExitCode, string Output, string Error)> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
        {
            Runs++;
            var at = double.Parse(arguments[arguments.ToList().IndexOf("-ss") + 1], CultureInfo.InvariantCulture);
            var bytes = at <= BlackAtOrBefore ? Black() : Busy((int)Math.Round(at));
            await File.WriteAllBytesAsync(arguments[^1], bytes, ct);
            return (0, string.Empty, string.Empty);
        }
    }
}
