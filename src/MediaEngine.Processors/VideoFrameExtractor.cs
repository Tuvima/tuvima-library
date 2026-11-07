using System.Globalization;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace MediaEngine.Processors;

/// <summary>How the scrub-preview frames are taken and tiled (the "scrub_previews" settings of config/transcoding.json).</summary>
/// <param name="IntervalSeconds">Seconds between two frames.</param>
/// <param name="TileWidth">Width of one frame in pixels; the height follows the picture's shape.</param>
/// <param name="Columns">Frames across one sprite sheet.</param>
/// <param name="Rows">Frames down one sprite sheet.</param>
public sealed record ScrubSpriteOptions(double IntervalSeconds = 10, int TileWidth = 320, int Columns = 10, int Rows = 10)
{
    public int FramesPerSheet => Columns * Rows;
}

/// <summary>The sprite sheets one run produced. <see cref="Error"/> is set (and the lists are empty) when it did not.</summary>
public sealed record ScrubSpriteBuildResult(
    IReadOnlyList<string> SheetFileNames,
    int TileWidth,
    int TileHeight,
    int FrameCount,
    string? Error = null)
{
    public bool Succeeded => Error is null && SheetFileNames.Count > 0;

    public static ScrubSpriteBuildResult Failed(string error) => new([], 0, 0, 0, error);
}

/// <summary>
/// The arithmetic of a sprite-sheet set, kept apart from FFmpeg so it can be checked without a video. Frame <c>n</c> is the picture
/// at <c>n * interval</c>; sheets hold <c>columns * rows</c> frames each, row by row.
/// </summary>
public static class ScrubSpriteLayout
{
    /// <summary>
    /// How many frames cover a video of this length: one at the start and one for each whole interval after that. A moment past the
    /// last frame shows the last frame, so a partial final interval never needs a frame of its own (FFmpeg does not make one).
    /// </summary>
    public static int FrameCount(double durationSeconds, double intervalSeconds) =>
        durationSeconds <= 0 || intervalSeconds <= 0 ? 0 : Math.Max(1, (int)Math.Floor(durationSeconds / intervalSeconds));

    public static int SheetCount(int frameCount, int framesPerSheet) =>
        frameCount <= 0 || framesPerSheet <= 0 ? 0 : (frameCount + framesPerSheet - 1) / framesPerSheet;

    public static string SheetFileName(int sheetIndex) => $"sheet-{sheetIndex:000}.jpg";

    /// <summary>Which frame shows a given moment: the last one at or before it, held within the frames that exist.</summary>
    public static int FrameIndexAt(double seconds, double intervalSeconds, int frameCount)
    {
        if (frameCount <= 0 || intervalSeconds <= 0 || !double.IsFinite(seconds))
        {
            return 0;
        }

        return Math.Clamp((int)Math.Floor(Math.Max(0, seconds) / intervalSeconds), 0, frameCount - 1);
    }

    /// <summary>Where a frame sits: its sheet, then its column and row inside that sheet.</summary>
    public static (int Sheet, int Column, int Row) Locate(int frameIndex, int columns, int rows)
    {
        var perSheet = columns * rows;
        var inSheet = frameIndex % perSheet;
        return (frameIndex / perSheet, inSheet % columns, inSheet / columns);
    }
}

/// <summary>
/// Takes still pictures out of a video with FFmpeg: one exact frame at a moment (the bookmark frames of a later packet reuse this),
/// or the whole run of timeline frames tiled into sprite sheets for scrub previews. Shared so no feature starts its own FFmpeg calls.
/// </summary>
public sealed class VideoFrameExtractor(IFFmpegService? ffmpeg, ILogger<VideoFrameExtractor>? logger = null)
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public bool IsAvailable => ffmpeg is { IsAvailable: true };

    /// <summary>
    /// One JPEG frame at <paramref name="seconds"/>, <paramref name="width"/> pixels wide. Null when FFmpeg is missing, the file cannot be
    /// read, or nothing could be decoded there.
    /// </summary>
    public async Task<byte[]?> ExtractFrameAsync(string videoPath, double seconds, int width, CancellationToken ct = default)
    {
        if (ffmpeg is not { IsAvailable: true } || !File.Exists(videoPath) || width <= 0)
        {
            return null;
        }

        var scratch = Path.Combine(Path.GetTempPath(), $"tuvima-frame-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            var frame = Path.Combine(scratch, "frame.jpg");
            var (exit, _, error) = await ffmpeg.RunAsync(
                ["-v", "error", "-nostdin", "-y",
                 "-ss", Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture),
                 "-i", videoPath,
                 "-frames:v", "1", "-an", "-sn",
                 "-vf", $"scale={width.ToString(CultureInfo.InvariantCulture)}:-2",
                 "-q:v", "4", frame], ct).ConfigureAwait(false);
            if (exit != 0 || !File.Exists(frame))
            {
                _logger.LogDebug("FFmpeg could not extract a frame at {Seconds}s (exit {Exit}): {Error}", seconds, exit, Tail(error));
                return null;
            }

            return await File.ReadAllBytesAsync(frame, ct).ConfigureAwait(false);
        }
        finally
        {
            DeleteQuietly(scratch);
        }
    }

    /// <summary>
    /// Takes a frame every <see cref="ScrubSpriteOptions.IntervalSeconds"/> over the whole video in one pass and tiles them into JPEG
    /// sprite sheets named <c>sheet-000.jpg</c>, <c>sheet-001.jpg</c> and so on, written into <paramref name="outputFolder"/>. Only
    /// key frames are decoded, so the pass reads a film far faster than it plays and stays light on the machine.
    /// </summary>
    public async Task<ScrubSpriteBuildResult> BuildSpriteSheetsAsync(
        string videoPath,
        double durationSeconds,
        ScrubSpriteOptions options,
        string outputFolder,
        CancellationToken ct = default)
    {
        if (ffmpeg is not { IsAvailable: true })
        {
            return ScrubSpriteBuildResult.Failed("FFmpeg is not available.");
        }

        if (!File.Exists(videoPath))
        {
            return ScrubSpriteBuildResult.Failed("The video file is missing.");
        }

        var frameCount = ScrubSpriteLayout.FrameCount(durationSeconds, options.IntervalSeconds);
        if (frameCount == 0)
        {
            return ScrubSpriteBuildResult.Failed("The video has no length to take frames from.");
        }

        Directory.CreateDirectory(outputFolder);
        var interval = options.IntervalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var pattern = Path.Combine(outputFolder, "sheet-%03d.jpg");
        var (exit, _, error) = await ffmpeg.RunAsync(
            ["-v", "error", "-nostdin", "-y", "-threads", "1",
             "-skip_frame", "nokey",
             "-i", videoPath,
             "-an", "-sn", "-dn",
             "-vf", $"fps=1/{interval},scale={options.TileWidth.ToString(CultureInfo.InvariantCulture)}:-2,tile={options.Columns.ToString(CultureInfo.InvariantCulture)}x{options.Rows.ToString(CultureInfo.InvariantCulture)}",
             "-q:v", "6", "-start_number", "0", pattern], ct).ConfigureAwait(false);
        if (exit != 0)
        {
            return ScrubSpriteBuildResult.Failed($"FFmpeg stopped with code {exit}: {Tail(error)}");
        }

        var expectedSheets = ScrubSpriteLayout.SheetCount(frameCount, options.FramesPerSheet);
        var sheets = Enumerable.Range(0, expectedSheets).Select(ScrubSpriteLayout.SheetFileName).ToList();
        if (sheets.Any(name => !File.Exists(Path.Combine(outputFolder, name))))
        {
            return ScrubSpriteBuildResult.Failed($"FFmpeg produced fewer than the expected {expectedSheets} sprite sheets.");
        }

        // The sheets are all the same size, so the first one tells the real size of a frame (its height follows the picture's shape).
        using var codec = SKCodec.Create(Path.Combine(outputFolder, sheets[0]));
        if (codec is null || codec.Info.Width < options.Columns || codec.Info.Height < options.Rows)
        {
            return ScrubSpriteBuildResult.Failed("The first sprite sheet could not be read back.");
        }

        return new ScrubSpriteBuildResult(sheets, codec.Info.Width / options.Columns, codec.Info.Height / options.Rows, frameCount);
    }

    private static string Tail(string text) => text.Length <= 400 ? text.Trim() : text[^400..].Trim();

    private void DeleteQuietly(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not remove the scratch folder {Folder}", folder);
        }
    }
}
