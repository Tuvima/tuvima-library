using System.Security.Cryptography;
using System.Text;
using MediaEngine.Domain.Contracts;
using SkiaSharp;

namespace MediaEngine.Processors;

/// <summary>
/// Picks a good still frame for a video that has no picture of its own: samples about 10, 20, 35 and 50 percent in,
/// throws away near-black and near-uniform frames, and keeps the sharpest, most colourful one. The pick is cached, so a
/// second scan reuses it. When every frame is dull the answer is "no picture", never a black tile.
/// </summary>
public sealed class VideoStillFrameService(IFFmpegService? ffmpeg, string cacheRoot)
{
    private static readonly double[] SamplePoints = [0.10, 0.20, 0.35, 0.50];
    private const double MinimumBrightness = 0.08;
    private const double MinimumContrast = 0.03;

    public async Task<FolderCoverImage?> GetOrCreateAsync(string videoPath, CancellationToken ct = default)
    {
        if (!File.Exists(videoPath))
        {
            return null;
        }

        var cachePath = Path.Combine(cacheRoot, CacheKey(videoPath) + ".jpg");
        if (File.Exists(cachePath))
        {
            return new FolderCoverImage(await File.ReadAllBytesAsync(cachePath, ct).ConfigureAwait(false), "image/jpeg", cachePath);
        }

        if (ffmpeg is null || !ffmpeg.IsAvailable)
        {
            return null;
        }

        var duration = (await ffmpeg.ProbeAsync(videoPath, ct).ConfigureAwait(false))?.Duration.TotalSeconds ?? 0;
        var scratch = Path.Combine(Path.GetTempPath(), $"tuvima-frames-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            byte[]? best = null;
            var bestScore = double.MinValue;
            var index = 0;
            foreach (var point in SamplePoints)
            {
                // A very short clip is sampled near its start so a still always exists.
                var second = duration > 4 ? duration * point : Math.Min(point * 4, Math.Max(0, duration - 0.1));
                var frame = Path.Combine(scratch, $"{index++}.jpg");
                var (exit, _, _) = await ffmpeg.RunAsync(
                    ["-v", "error", "-y", "-ss", second.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), "-i", videoPath,
                     "-frames:v", "1", "-vf", "scale=640:-2", "-q:v", "3", frame], ct).ConfigureAwait(false);
                if (exit != 0 || !File.Exists(frame))
                {
                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(frame, ct).ConfigureAwait(false);
                if (Score(bytes) is { } score && score > bestScore)
                {
                    bestScore = score;
                    best = bytes;
                }
            }

            if (best is null)
            {
                return null;
            }

            Directory.CreateDirectory(cacheRoot);
            await File.WriteAllBytesAsync(cachePath, best, ct).ConfigureAwait(false);
            return new FolderCoverImage(best, "image/jpeg", cachePath);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// How good a still this image is: null for a near-black or near-uniform frame, otherwise sharpness weighted by
    /// colourfulness (higher is better).
    /// </summary>
    public static double? Score(byte[] image)
    {
        using var bitmap = SKBitmap.Decode(image);
        if (bitmap is null || bitmap.Width == 0 || bitmap.Height == 0)
        {
            return null;
        }

        using var small = bitmap.Resize(new SKImageInfo(64, 64, SKColorType.Rgba8888), SKSamplingOptions.Default);
        if (small is null)
        {
            return null;
        }

        var luma = new double[64 * 64];
        double sum = 0, rgSum = 0, ybSum = 0, rgSq = 0, ybSq = 0;
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var pixel = small.GetPixel(x, y);
                double r = pixel.Red / 255d, g = pixel.Green / 255d, b = pixel.Blue / 255d;
                var l = 0.299 * r + 0.587 * g + 0.114 * b;
                luma[y * 64 + x] = l;
                sum += l;
                var rg = r - g;
                var yb = 0.5 * (r + g) - b;
                rgSum += rg;
                ybSum += yb;
                rgSq += rg * rg;
                ybSq += yb * yb;
            }
        }

        const int count = 64 * 64;
        var mean = sum / count;
        var variance = luma.Sum(l => (l - mean) * (l - mean)) / count;
        var contrast = Math.Sqrt(variance);
        if (mean < MinimumBrightness || contrast < MinimumContrast)
        {
            return null;
        }

        double edges = 0;
        for (var y = 1; y < 63; y++)
        {
            for (var x = 1; x < 63; x++)
            {
                var laplacian = 4 * luma[y * 64 + x] - luma[y * 64 + x - 1] - luma[y * 64 + x + 1] - luma[(y - 1) * 64 + x] - luma[(y + 1) * 64 + x];
                edges += laplacian * laplacian;
            }
        }

        var sharpness = edges / (62 * 62);
        var rgMean = rgSum / count;
        var ybMean = ybSum / count;
        var colourful = Math.Sqrt(rgSq / count - rgMean * rgMean + ybSq / count - ybMean * ybMean)
            + 0.3 * Math.Sqrt(rgMean * rgMean + ybMean * ybMean);
        return (sharpness + 0.001) * (1 + colourful) + contrast;
    }

    private static string CacheKey(string videoPath)
    {
        var info = new FileInfo(videoPath);
        var key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }
}
