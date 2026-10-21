using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace MediaEngine.Providers.Helpers;

/// <summary>
/// Measures artwork, extracts palette hexes, and prepares UI renditions.
/// </summary>
public static class ArtworkVariantHelper
{
    private const int TinyLongEdge = 96;
    private const int SmallLongEdge = 320;
    private const int MediumLongEdge = 960;
    private const int LargeLongEdge = 2160;

    // Decoding a large original is memory-heavy, so copies made while a person waits are made a few at a time.
    private static readonly SemaphoreSlim RenditionWorkGate = new(Math.Max(2, Environment.ProcessorCount / 2));

    /// <summary>
    /// Waits for one of the few slots for making copies on demand (at most <c>Math.Max(2, cores / 2)</c> at once, the whole
    /// Engine over). Dispose the result to give the slot back.
    /// </summary>
    public static async Task<IDisposable> EnterRenditionWorkAsync(CancellationToken ct)
    {
        await RenditionWorkGate.WaitAsync(ct).ConfigureAwait(false);
        return new RenditionWorkSlot();
    }

    private sealed class RenditionWorkSlot : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                RenditionWorkGate.Release();
            }
        }
    }

    /// <summary>The size keys, smallest to largest. <c>t</c> is 96px, <c>s</c> 320, <c>m</c> 960 and <c>l</c> 2160 on the long edge.</summary>
    public static IReadOnlyList<string> RenditionSizeKeys { get; } = ["t", "s", "m", "l"];

    /// <summary>
    /// Reads a size key (<c>t</c>, <c>s</c>, <c>m</c> or <c>l</c>, any case, surrounding spaces ignored).
    /// Returns false for anything else, including an empty value.
    /// </summary>
    public static bool TryParseRenditionSize(string? size, out string key)
    {
        var normalized = (size ?? string.Empty).Trim().ToLowerInvariant();
        key = normalized;
        return normalized is "t" or "s" or "m" or "l";
    }

    /// <summary>The long-edge pixel size of a copy, or 0 for an unknown key.</summary>
    public static int LongEdgeFor(string sizeKey) => sizeKey switch
    {
        "t" => TinyLongEdge,
        "s" => SmallLongEdge,
        "m" => MediumLongEdge,
        "l" => LargeLongEdge,
        _ => 0,
    };

    /// <summary>The stored path of one copy, or null when the asset has none for that key.</summary>
    public static string? GetRenditionPath(EntityAsset asset, string sizeKey)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return sizeKey switch
        {
            "t" => asset.LocalImagePathTiny,
            "s" => asset.LocalImagePathSmall,
            "m" => asset.LocalImagePathMedium,
            "l" => asset.LocalImagePathLarge,
            _ => null,
        };
    }

    private static void SetRenditionPath(EntityAsset asset, string sizeKey, string? path)
    {
        switch (sizeKey)
        {
            case "t": asset.LocalImagePathTiny = path; break;
            case "s": asset.LocalImagePathSmall = path; break;
            case "m": asset.LocalImagePathMedium = path; break;
            case "l": asset.LocalImagePathLarge = path; break;
        }
    }

    public static void StampMetadataAndRenditions(EntityAsset asset, AssetPathService assetPathService)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(assetPathService);

        if (string.IsNullOrWhiteSpace(asset.LocalImagePath) || !File.Exists(asset.LocalImagePath))
        {
            return;
        }

        using var bitmap = SKBitmap.Decode(asset.LocalImagePath);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return;
        }

        asset.WidthPx = bitmap.Width;
        asset.HeightPx = bitmap.Height;
        asset.AspectClass = ClassifyAspect(bitmap.Width, bitmap.Height);

        var palette = string.Equals(asset.AssetTypeValue, "Background", StringComparison.OrdinalIgnoreCase)
            ? ExtractBackdropLeftEdgePalette(bitmap)
            : ArtworkPaletteColorEngine.ExtractLegacyPalette(bitmap);
        asset.PrimaryHex = palette.PrimaryHex;
        asset.SecondaryHex = palette.SecondaryHex;
        asset.AccentHex = palette.AccentHex;

        if (!ShouldGenerateRenditions(asset.AssetTypeValue))
        {
            asset.LocalImagePathTiny = null;
            asset.LocalImagePathSmall = null;
            asset.LocalImagePathMedium = null;
            asset.LocalImagePathLarge = null;
            return;
        }

        var extension = NormalizeExtension(asset.LocalImagePath);
        var longEdge = Math.Max(bitmap.Width, bitmap.Height);

        asset.LocalImagePathTiny = BuildRenditionPath(assetPathService, asset, "t", extension);
        asset.LocalImagePathSmall = BuildRenditionPath(assetPathService, asset, "s", extension);
        asset.LocalImagePathMedium = BuildRenditionPath(assetPathService, asset, "m", extension);
        asset.LocalImagePathLarge = BuildRenditionPath(assetPathService, asset, "l", extension);

        WriteRendition(bitmap, asset.LocalImagePathTiny, extension, TinyLongEdge, longEdge);
        WriteRendition(bitmap, asset.LocalImagePathSmall, extension, SmallLongEdge, longEdge);
        WriteRendition(bitmap, asset.LocalImagePathMedium, extension, MediumLongEdge, longEdge);
        WriteRendition(bitmap, asset.LocalImagePathLarge, extension, LargeLongEdge, longEdge);
    }

    /// <summary>
    /// True when the asset should have copies and at least one of the four is not recorded or its file is missing.
    /// An asset whose original is missing is never reported (nothing can be made).
    /// </summary>
    public static bool HasMissingRenditions(EntityAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);

        if (!ShouldGenerateRenditions(asset.AssetTypeValue)
            || string.IsNullOrWhiteSpace(asset.LocalImagePath)
            || !File.Exists(asset.LocalImagePath))
        {
            return false;
        }

        return RenditionSizeKeys.Any(key =>
        {
            var path = GetRenditionPath(asset, key);
            return string.IsNullOrWhiteSpace(path) || !File.Exists(path);
        });
    }

    /// <summary>
    /// Creates only the copies the asset is missing, from its original, and records their paths on the asset
    /// (the caller saves it). Copies that already exist are left alone. Returns true when anything was made.
    /// A missing or unreadable original, or an asset type without copies, makes nothing.
    /// </summary>
    public static bool EnsureMissingRenditions(EntityAsset asset, AssetPathService assetPathService)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(assetPathService);

        if (!HasMissingRenditions(asset))
        {
            return false;
        }

        using var bitmap = SKBitmap.Decode(asset.LocalImagePath!);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return false;
        }

        var extension = NormalizeExtension(asset.LocalImagePath!);
        var longEdge = Math.Max(bitmap.Width, bitmap.Height);
        var made = false;
        foreach (var key in RenditionSizeKeys)
        {
            var existing = GetRenditionPath(asset, key);
            if (!string.IsNullOrWhiteSpace(existing) && File.Exists(existing))
            {
                continue;
            }

            var path = BuildRenditionPath(assetPathService, asset, key, extension);
            WriteRendition(bitmap, path, extension, LongEdgeFor(key), longEdge);
            if (File.Exists(path))
            {
                SetRenditionPath(asset, key, path);
                made = true;
            }
        }

        return made;
    }

    /// <summary>
    /// Writes one resized copy of a picture file to <paramref name="outputPath"/> (PNG when the output name ends in
    /// <c>.png</c>, otherwise JPEG). A picture already at or under the long edge is re-encoded as is. Returns false when the
    /// source cannot be read as a picture.
    /// </summary>
    public static bool WriteResizedCopy(string sourcePath, string outputPath, int targetLongEdge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        if (!File.Exists(sourcePath))
        {
            return false;
        }

        using var bitmap = SKBitmap.Decode(sourcePath);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return false;
        }

        WriteRendition(bitmap, outputPath, NormalizeExtension(outputPath), targetLongEdge, Math.Max(bitmap.Width, bitmap.Height));
        return File.Exists(outputPath);
    }

    internal static (string PrimaryHex, string SecondaryHex, string AccentHex) ExtractBackdropLeftEdgePalette(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var sampleWidth = Math.Max(1, (int)Math.Ceiling(bitmap.Width * 0.24));
        var third = Math.Max(1, bitmap.Height / 3);
        return (
            DominantRegionColor(bitmap, sampleWidth, 0, third),
            DominantRegionColor(bitmap, sampleWidth, third, Math.Min(bitmap.Height, third * 2)),
            DominantRegionColor(bitmap, sampleWidth, Math.Min(bitmap.Height, third * 2), bitmap.Height));
    }

    private static string DominantRegionColor(SKBitmap bitmap, int sampleWidth, int top, int bottom)
    {
        var buckets = new Dictionary<int, (long R, long G, long B, int Count)>();
        var stepX = Math.Max(1, sampleWidth / 96);
        var stepY = Math.Max(1, Math.Max(1, bottom - top) / 72);

        for (var y = top; y < bottom; y += stepY)
        {
            for (var x = 0; x < sampleWidth; x += stepX)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha < 32)
                {
                    continue;
                }

                var key = ((pixel.Red >> 4) << 8) | ((pixel.Green >> 4) << 4) | (pixel.Blue >> 4);
                buckets.TryGetValue(key, out var bucket);
                buckets[key] = (bucket.R + pixel.Red, bucket.G + pixel.Green, bucket.B + pixel.Blue, bucket.Count + 1);
            }
        }

        var dominant = buckets.Values
            .OrderByDescending(bucket => bucket.Count)
            .ThenByDescending(bucket => bucket.R + bucket.G + bucket.B)
            .FirstOrDefault();
        if (dominant.Count == 0)
        {
            return "#080C12";
        }

        return $"#{dominant.R / dominant.Count:X2}{dominant.G / dominant.Count:X2}{dominant.B / dominant.Count:X2}";
    }

    /// <summary>
    /// Deletes a removed variant and the files of its that nothing else references
    /// (<see cref="IEntityAssetRepository.DeleteVariantAsync"/>). Only files under the managed asset root are touched.
    /// </summary>
    public static async Task DeleteVariantAndUnusedFilesAsync(IEntityAssetRepository repository, EntityAsset asset,
        AssetPathService assetPathService, ILogger logger, CancellationToken ct)
    {
        var unused = await repository.DeleteVariantAsync(asset.Id, ct).ConfigureAwait(false);
        var managedRoot = Path.GetFullPath(assetPathService.AssetsRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        foreach (var path in unused)
        {
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "Skipped deletion of artwork file outside the managed asset root: {Path}",
                    fullPath);
                continue;
            }

            try
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Failed to delete stale managed artwork file {Path}", fullPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Access was denied deleting stale managed artwork file {Path}", fullPath);
            }
        }
    }

    public static bool ShouldGenerateRenditions(string assetTypeValue) => assetTypeValue switch
    {
        "Logo" => false,
        _ => true,
    };

    public static string ClassifyAspect(int widthPx, int heightPx)
    {
        if (widthPx <= 0 || heightPx <= 0)
        {
            return ArtworkAspectClasses.UnsupportedRect;
        }

        var ratio = widthPx / (double)heightPx;
        if (ratio < 0.82d)
        {
            return ArtworkAspectClasses.Portrait;
        }

        if (ratio <= 1.18d)
        {
            return ArtworkAspectClasses.Square;
        }

        if (ratio >= 1.50d && ratio <= 2.40d)
        {
            return ArtworkAspectClasses.LandscapeWide;
        }

        if (ratio > 2.40d)
        {
            return ArtworkAspectClasses.BannerStrip;
        }

        return ArtworkAspectClasses.UnsupportedRect;
    }

    private static string BuildRenditionPath(
        AssetPathService assetPathService,
        EntityAsset asset,
        string sizeKey,
        string extension) =>
        assetPathService.GetCentralDerivedPath(
            asset.EntityType,
            asset.EntityId,
            $"renditions-{asset.AssetTypeValue}-{asset.Id:N}",
            $"{sizeKey}{extension}");

    private static void WriteRendition(
        SKBitmap sourceBitmap,
        string outputPath,
        string extension,
        int targetLongEdge,
        int sourceLongEdge)
    {
        AssetPathService.EnsureDirectory(outputPath);

        if (sourceLongEdge <= targetLongEdge)
        {
            using var originalImage = SKImage.FromBitmap(sourceBitmap);
            using var originalData = EncodeImage(originalImage, extension);
            using var output = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            originalData.SaveTo(output);
            return;
        }

        var scale = targetLongEdge / (double)sourceLongEdge;
        var width = Math.Max(1, (int)Math.Round(sourceBitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(sourceBitmap.Height * scale));
        using var resized = sourceBitmap.Resize(
            new SKImageInfo(width, height, sourceBitmap.ColorType, sourceBitmap.AlphaType),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (resized is null)
        {
            return;
        }

        using var image = SKImage.FromBitmap(resized);
        using var data = EncodeImage(image, extension);
        using var resizedOutput = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        data.SaveTo(resizedOutput);
    }

    private static SKData EncodeImage(SKImage image, string extension) =>
        string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            ? image.Encode(SKEncodedImageFormat.Png, 100)
            : image.Encode(SKEncodedImageFormat.Jpeg, 88);

    private static string NormalizeExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
            ? ".png"
            : ".jpg";
    }

}
