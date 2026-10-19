using System.Security.Cryptography;
using MediaEngine.Api.Endpoints;
using MediaEngine.Domain.Activity;
using MediaEngine.Domain.Contracts;
using SkiaSharp;

namespace MediaEngine.Api.Services.Activity;

/// <summary>
/// Makes the small copy of a cover that an activity entry keeps, so the picture survives deleting the item. The copy
/// has a longest edge of 240 px, is shared by every entry with the same source cover, and is written once.
/// </summary>
public sealed class PersonalActivityArtworkService(
    IWorkRepository works,
    IEntityAssetRepository entityAssets,
    IPersonalActivityRepository activity,
    ILogger<PersonalActivityArtworkService> logger)
{
    internal const int MaximumEdge = 240;

    /// <summary>
    /// Stores the small copy of the asset's current cover and returns its key; null when the item has no readable
    /// cover. A failure is logged and answered with null, so it never stops the activity being recorded.
    /// </summary>
    public async Task<string?> CaptureAsync(Guid assetId, CancellationToken ct)
    {
        if (assetId == Guid.Empty)
        {
            return null;
        }

        try
        {
            var owner = await StreamEndpoints.ResolveArtworkOwnerEntityIdAsync(assetId, works, ct).ConfigureAwait(false);
            var cover = await entityAssets.GetPreferredAsync(owner.ToString(), "CoverArt", ct).ConfigureAwait(false);
            var path = new[] { cover?.LocalImagePathMedium, cover?.LocalImagePathSmall, cover?.LocalImagePath }
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));
            if (path is null)
            {
                return null;
            }

            var source = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var key = Convert.ToHexStringLower(SHA256.HashData(source));
            if (await activity.HasArtworkAsync(key, ct).ConfigureAwait(false))
            {
                return key;
            }

            var small = Shrink(source);
            if (small is null)
            {
                return null;
            }

            await activity.SaveArtworkAsync(new PersonalActivityArtwork(key, small.Image, small.ContentType, small.Width, small.Height), ct)
                .ConfigureAwait(false);
            return key;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not keep a small copy of the cover for asset {AssetId}; its activity is recorded without a picture.", assetId);
            return null;
        }
    }

    /// <summary>Decodes the cover and writes a copy with a longest edge of at most 240 px: JPEG, or WebP when the cover has transparency.</summary>
    internal static ShrunkImage? Shrink(byte[] source)
    {
        using var bitmap = SKBitmap.Decode(source);
        if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return null;
        }

        var scale = Math.Min(1d, MaximumEdge / (double)Math.Max(bitmap.Width, bitmap.Height));
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        using var resized = scale < 1d
            ? bitmap.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            : null;
        var target = resized ?? bitmap;
        var opaque = bitmap.AlphaType == SKAlphaType.Opaque;
        using var image = SKImage.FromBitmap(target);
        using var data = image.Encode(opaque ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Webp, 82);
        return data is null
            ? null
            : new ShrunkImage(data.ToArray(), opaque ? "image/jpeg" : "image/webp", target.Width, target.Height);
    }

    internal sealed record ShrunkImage(byte[] Image, string ContentType, int Width, int Height);
}
