using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Processors;

/// <summary>One frame taken from a video for the editor's "Frames from the video" strip.</summary>
/// <param name="Index">Its place in the strip (0 based); also the id the editor asks for it by.</param>
/// <param name="Seconds">How far into the video it was taken.</param>
/// <param name="Bytes">The JPEG picture.</param>
/// <param name="Score">How good a still it is (see <see cref="VideoStillFrameService.Score"/>); higher is better.</param>
/// <param name="IsBest">The frame the Engine would pick for the video's cover.</param>
public sealed record VideoFrameCandidate(int Index, double Seconds, byte[] Bytes, double Score, bool IsBest);

/// <summary>
/// The frames the editor's Artwork tab offers for a video (spec review-mockups-2026-11-18, D2): a handful spread over the whole video,
/// with near-black and near-flat ones thrown away, and the best of them marked (the Engine's pick for the cover, never a fixed 10 percent
/// point). Frames are cached by the file's identity so the strip and its pictures read the same frames.
/// </summary>
public sealed class VideoFrameCandidateService(
    VideoFrameExtractor extractor,
    IFFmpegService? ffmpeg,
    string cacheRoot,
    ILogger<VideoFrameCandidateService>? logger = null)
{
    /// <summary>How many places in the video a frame is taken from (some may be dropped for being dark or flat).</summary>
    public const int SampleCount = 8;

    /// <summary>The width the frames are saved at: sharp enough for a cover, small enough to read quickly.</summary>
    public const int FrameWidth = 640;

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>The moments a video of this length is sampled at: evenly spread, never at the very start or end.</summary>
    public static IReadOnlyList<double> SampleSeconds(double durationSeconds)
    {
        if (durationSeconds <= 0)
        {
            return [];
        }

        return Enumerable.Range(1, SampleCount)
            .Select(step => Math.Round(durationSeconds * step / (SampleCount + 1), 3))
            .ToList();
    }

    /// <summary>The best frame of a set: the highest score, the earliest one when two tie. Null for an empty set.</summary>
    public static int? PickBest(IReadOnlyList<(int Index, double Score)> frames) =>
        frames.Count == 0 ? null : frames.OrderByDescending(frame => frame.Score).ThenBy(frame => frame.Index).First().Index;

    /// <summary>The frames of a video, in order. Empty when the file is missing, FFmpeg is not available or every frame was dark or flat.</summary>
    public async Task<IReadOnlyList<VideoFrameCandidate>> GetAsync(string videoPath, CancellationToken ct = default)
    {
        // Only the file's name is logged, never its folder (the log may be shared when someone asks for help).
        var name = Path.GetFileName(videoPath);
        if (!File.Exists(videoPath))
        {
            _logger.LogInformation("Video frame strip: {File} was not found, so no frames were made", name);
            return [];
        }

        var folder = Path.Combine(cacheRoot, CacheKey(videoPath));
        var saved = ReadSaved(folder);
        if (saved.Count == 0)
        {
            if (ffmpeg is not { IsAvailable: true })
            {
                _logger.LogWarning("Video frame strip: FFmpeg is not available, so no frames were made for {File}", name);
                return [];
            }

            var duration = (await ffmpeg.ProbeAsync(videoPath, ct).ConfigureAwait(false))?.Duration.TotalSeconds ?? 0;
            var points = SampleSeconds(duration);
            for (var i = 0; i < points.Count; i++)
            {
                var bytes = await extractor.ExtractFrameAsync(videoPath, points[i], FrameWidth, ct).ConfigureAwait(false);
                if (bytes is not { Length: > 0 } || VideoStillFrameService.Score(bytes) is null)
                {
                    continue;
                }

                Directory.CreateDirectory(folder);
                await File.WriteAllBytesAsync(FramePath(folder, i, points[i]), bytes, ct).ConfigureAwait(false);
            }

            saved = ReadSaved(folder);
            _logger.LogInformation("Video frame strip: made {Count} frames for {File}", saved.Count, name);
        }

        var scored = new List<(int Index, double Seconds, byte[] Bytes, double Score)>();
        foreach (var (index, seconds, path) in saved)
        {
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (VideoStillFrameService.Score(bytes) is { } score)
            {
                scored.Add((index, seconds, bytes, score));
            }
        }

        var best = PickBest(scored.Select(frame => (frame.Index, frame.Score)).ToList());
        return scored.Select(frame => new VideoFrameCandidate(frame.Index, frame.Seconds, frame.Bytes, frame.Score, frame.Index == best)).ToList();
    }

    /// <summary>One frame by its index, or null when there is no such frame.</summary>
    public async Task<VideoFrameCandidate?> GetOneAsync(string videoPath, int index, CancellationToken ct = default) =>
        (await GetAsync(videoPath, ct).ConfigureAwait(false)).FirstOrDefault(frame => frame.Index == index);

    // File names carry the index and the moment: "3-120500.jpg" is the fourth sample, taken 120.5 seconds in.
    private static string FramePath(string folder, int index, double seconds) =>
        Path.Combine(folder, $"{index}-{(long)Math.Round(seconds * 1000)}.jpg");

    private static List<(int Index, double Seconds, string Path)> ReadSaved(string folder)
    {
        var found = new List<(int Index, double Seconds, string Path)>();
        if (!Directory.Exists(folder))
        {
            return found;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.jpg"))
        {
            var parts = Path.GetFileNameWithoutExtension(file).Split('-');
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var millis))
            {
                found.Add((index, millis / 1000d, file));
            }
        }

        return found.OrderBy(frame => frame.Index).ToList();
    }

    private static string CacheKey(string videoPath)
    {
        var info = new FileInfo(videoPath);
        var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|strip";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }
}
