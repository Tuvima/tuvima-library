using System.Collections.Concurrent;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;
using SkiaSharp;

namespace MediaEngine.Api.Services.View;

/// <summary>
/// Produces managed timeline derivatives without modifying personal originals.
/// A derivative is refreshed only when its source file is newer.
/// </summary>
public sealed class ViewThumbnailService(
    AssetPathService assetPaths,
    ILogger<ViewThumbnailService> logger,
    IFFmpegService? ffmpeg = null)
{
    private const int MaximumEdge = 640;
    private const int PreviewMaximumEdge = 2048;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _itemLocks = new();
    private readonly SemaphoreSlim _decodeSlots = new(2, 2);

    public async Task<string?> GetOrCreateAsync(
        Guid itemId,
        LocalAssetContentLocation source,
        CancellationToken ct = default) => await GetOrCreateAsync(itemId, source, MaximumEdge, "thumbnail", ct).ConfigureAwait(false);

    public async Task<string?> GetOrCreatePreviewAsync(
        Guid itemId,
        LocalAssetContentLocation source,
        CancellationToken ct = default) => await GetOrCreateAsync(itemId, source, PreviewMaximumEdge, "preview", ct).ConfigureAwait(false);

    private async Task<string?> GetOrCreateAsync(
        Guid itemId,
        LocalAssetContentLocation source,
        int maximumEdge,
        string purpose,
        CancellationToken ct)
    {
        if (itemId == Guid.Empty
            || (!source.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                && !source.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            || !File.Exists(source.FilePath))
        {
            return null;
        }

        var target = assetPaths.GetCentralDerivedPath(
            "local-item",
            itemId,
            purpose,
            $"{purpose}-{maximumEdge}.jpg");
        if (IsCurrent(target, source.FilePath))
        {
            return target;
        }

        var gate = _itemLocks.GetOrAdd(itemId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var decodeSlot = false;
        try
        {
            if (IsCurrent(target, source.FilePath))
            {
                return target;
            }

            await _decodeSlots.WaitAsync(ct).ConfigureAwait(false);
            decodeSlot = true;
            if (source.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                return await GenerateVideoAsync(source.FilePath, target, maximumEdge, ct).ConfigureAwait(false)
                    ? target
                    : null;
            }

            var decoded = false;
            try { decoded = await Task.Run(() => GenerateImage(source.FilePath, target, maximumEdge), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogDebug(ex, "Native decoder could not read View item {ItemId}; trying FFmpeg", itemId); }
            if (decoded)
            {
                return target;
            }

            return await GenerateImageWithFfmpegAsync(source.FilePath, target, maximumEdge, ct).ConfigureAwait(false)
                ? target
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not create the managed View thumbnail for item {ItemId}.", itemId);
            return null;
        }
        finally
        {
            if (decodeSlot) _decodeSlots.Release();
            gate.Release();
        }
    }

    private static bool IsCurrent(string target, string source) =>
        File.Exists(target)
        && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(source);

    private async Task<bool> GenerateVideoAsync(string source, string target, int maximumEdge, CancellationToken ct)
    {
        if (ffmpeg is not { IsAvailable: true })
        {
            return false;
        }

        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(target)}.{Guid.NewGuid():N}.tmp.jpg");
        try
        {
            var result = await ffmpeg.RunAsync(
                $"-y -ss 0.5 -i {Quote(source)} -frames:v 1 -vf scale={maximumEdge}:-2:force_original_aspect_ratio=decrease {Quote(temporary)}",
                ct).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(temporary))
            {
                return false;
            }

            File.Move(temporary, target, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task<bool> GenerateImageWithFfmpegAsync(string source, string target, int maximumEdge, CancellationToken ct)
    {
        if (ffmpeg is not { IsAvailable: true })
        {
            return false;
        }

        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(target)}.{Guid.NewGuid():N}.tmp.jpg");
        try
        {
            // HEIF tile grids cause FFmpeg to build an implicit complex graph.
            // Adding -vf conflicts with that graph; selecting stream 0 would
            // instead decode only one tile. Assemble the full still, then resize
            // the managed intermediate with the ordinary image decoder.
            var result = await ffmpeg.RunAsync(
                ["-y", "-hide_banner", "-loglevel", "error", "-i", source, "-frames:v", "1", temporary],
                ct).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(temporary))
            {
                logger.LogWarning("View image decoder failed ({ExitCode}): {Error}", result.ExitCode, result.Error);
                return false;
            }

            return GenerateImage(temporary, target, maximumEdge);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool GenerateImage(string source, string target, int maximumEdge)
    {
        using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bitmap = SKBitmap.Decode(input);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return false;
        }

        var scale = Math.Min(1d, maximumEdge / (double)Math.Max(bitmap.Width, bitmap.Height));
        using var resized = bitmap.Resize(new SKImageInfo(
            Math.Max(1, (int)Math.Round(bitmap.Width * scale)),
            Math.Max(1, (int)Math.Round(bitmap.Height * scale))),
            new SKSamplingOptions(SKFilterMode.Linear));
        using var image = SKImage.FromBitmap(resized ?? bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 82);
        if (data is null)
        {
            return false;
        }

        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                data.SaveTo(output);
            }
            File.Move(temporary, target, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string Quote(string path) => $"\"{path.Replace("\"", "\\\"")}\"";
}
