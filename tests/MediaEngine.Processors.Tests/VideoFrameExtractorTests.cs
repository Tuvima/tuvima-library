using MediaEngine.TestSupport;
using SkiaSharp;

namespace MediaEngine.Processors.Tests;

public sealed class VideoFrameExtractorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("tuvima-frames-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(7200, 10, 720)]
    [InlineData(125, 10, 12)]
    [InlineData(120, 10, 12)]
    [InlineData(5, 10, 1)]
    [InlineData(0, 10, 0)]
    public void AVideoGetsOneFrameForEachWholeInterval(double duration, double interval, int expected) =>
        Assert.Equal(expected, ScrubSpriteLayout.FrameCount(duration, interval));

    [Theory]
    [InlineData(720, 100, 8)]
    [InlineData(100, 100, 1)]
    [InlineData(101, 100, 2)]
    [InlineData(0, 100, 0)]
    public void FramesAreTiledIntoWholeSheets(int frames, int perSheet, int sheets) =>
        Assert.Equal(sheets, ScrubSpriteLayout.SheetCount(frames, perSheet));

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(9, 0, 9, 0)]
    [InlineData(10, 0, 0, 1)]
    [InlineData(99, 0, 9, 9)]
    [InlineData(100, 1, 0, 0)]
    [InlineData(215, 2, 5, 1)]
    public void AFrameSitsAtItsSheetColumnAndRow(int frame, int sheet, int column, int row) =>
        Assert.Equal((sheet, column, row), ScrubSpriteLayout.Locate(frame, 10, 10));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(9.9, 0)]
    [InlineData(10, 1)]
    [InlineData(125, 11)]
    [InlineData(99999, 11)]
    [InlineData(-5, 0)]
    public void AMomentShowsTheFrameAtOrBeforeItAndNeverPastTheLastOne(double seconds, int frame) =>
        Assert.Equal(frame, ScrubSpriteLayout.FrameIndexAt(seconds, 10, 12));

    [Fact]
    public async Task WithoutFfmpeg_NothingIsExtractedAndTheReasonIsPlain()
    {
        var extractor = new VideoFrameExtractor(null);

        Assert.False(extractor.IsAvailable);
        Assert.Null(await extractor.ExtractFrameAsync(Path.Combine(_root, "none.mp4"), 3, 320));
        var built = await extractor.BuildSpriteSheetsAsync(Path.Combine(_root, "none.mp4"), 60, new ScrubSpriteOptions(), Path.Combine(_root, "out"));
        Assert.False(built.Succeeded);
        Assert.Equal("FFmpeg is not available.", built.Error);
    }

    [RealFfmpegFact]
    public async Task ASampleVideo_GetsSpriteSheetsWithTheFramesInOrder()
    {
        var ffmpeg = RealFfmpeg.Find()!;
        var video = Path.Combine(_root, "sample.mp4");
        await ffmpeg.MakeSampleVideoAsync(video, 25);
        var extractor = new VideoFrameExtractor(ffmpeg);
        var options = new ScrubSpriteOptions(IntervalSeconds: 2, TileWidth: 80, Columns: 3, Rows: 2);

        var result = await extractor.BuildSpriteSheetsAsync(video, 25, options, Path.Combine(_root, "sprites"));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(12, result.FrameCount);
        Assert.Equal(["sheet-000.jpg", "sheet-001.jpg"], result.SheetFileNames);
        Assert.Equal(80, result.TileWidth);
        Assert.Equal(46, result.TileHeight); // 80 px wide keeps the picture's 16:9 shape, rounded to an even height
        foreach (var sheet in result.SheetFileNames)
        {
            using var codec = SKCodec.Create(Path.Combine(_root, "sprites", sheet));
            Assert.Equal(3 * 80, codec.Info.Width);
            Assert.Equal(2 * 46, codec.Info.Height);
        }
    }

    [RealFfmpegFact]
    public async Task ASingleFrameIsExtractedAtTheAskedWidth()
    {
        var ffmpeg = RealFfmpeg.Find()!;
        var video = Path.Combine(_root, "sample.mp4");
        await ffmpeg.MakeSampleVideoAsync(video, 10);

        var frame = await new VideoFrameExtractor(ffmpeg).ExtractFrameAsync(video, 4, 100);

        Assert.NotNull(frame);
        using var bitmap = SKBitmap.Decode(frame);
        Assert.Equal(100, bitmap.Width);
    }

    [RealFfmpegFact]
    public async Task AFrameBeyondTheEndOfTheVideoIsNull()
    {
        var ffmpeg = RealFfmpeg.Find()!;
        var video = Path.Combine(_root, "sample.mp4");
        await ffmpeg.MakeSampleVideoAsync(video, 5);

        Assert.Null(await new VideoFrameExtractor(ffmpeg).ExtractFrameAsync(video, 500, 100));
    }
}
