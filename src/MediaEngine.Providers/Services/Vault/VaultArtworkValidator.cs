using System.Security.Cryptography;
using SkiaSharp;

namespace MediaEngine.Providers.Services.Vault;

/// <summary>An image that passed every check, re-encoded without any embedded camera or location data.</summary>
public sealed record ValidatedVaultImage(byte[] Bytes, string MimeType, string Extension, int Width, int Height, string Hash);

/// <summary>Why an image was refused. Never shown with the image's own words.</summary>
public enum VaultImageRefusal
{
    None,
    NotAnImage,
    WrongType,
    TooSmall,
    TooLarge,
    WrongShape,
    CouldNotRead,
}

/// <summary>What the artwork is for, which decides the shapes that make sense (Videos spec D31, D41).</summary>
public enum VaultImagePurpose
{
    /// <summary>A scene cover or thumbnail: landscape or close to square, up to a wide banner.</summary>
    Cover,

    /// <summary>A still frame cut from the video itself.</summary>
    Still,
}

/// <summary>
/// Checks artwork pulled from a stash-box before it is used (Videos spec D31): it must open as a real JPEG, PNG or WebP of a sensible
/// size and shape for its use. The image is decoded and written out again, so any embedded camera or location data is gone, the
/// long edge is capped at 2560 pixels (D41), and its hash lets a duplicate be dropped. A failed or unsuitable image gives a reason and
/// no bytes; the caller then leaves the generated still frame in place.
/// </summary>
public static class VaultArtworkValidator
{
    public const int MaxFileBytes = 8 * 1024 * 1024;
    public const int MinFileBytes = 1024;
    public const int MinEdge = 200;
    public const int MinStillEdge = 64;
    public const int MaxSourceEdge = 12000;
    public const long MaxSourcePixels = 50_000_000;
    public const int MaxStoredLongEdge = 2560;

    private const double CoverMinAspect = 0.5;
    private const double CoverMaxAspect = 2.4;
    private const double StillMinAspect = 0.4;
    private const double StillMaxAspect = 3.0;

    public static (ValidatedVaultImage? Image, VaultImageRefusal Refusal) Validate(byte[]? bytes, VaultImagePurpose purpose)
    {
        if (bytes is null || bytes.Length < MinFileBytes)
        {
            return (null, VaultImageRefusal.NotAnImage);
        }

        if (bytes.Length > MaxFileBytes)
        {
            return (null, VaultImageRefusal.TooLarge);
        }

        try
        {
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null)
            {
                return (null, VaultImageRefusal.NotAnImage);
            }

            var format = codec.EncodedFormat;
            if (format is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp))
            {
                return (null, VaultImageRefusal.WrongType);
            }

            var info = codec.Info;
            var minEdge = purpose == VaultImagePurpose.Still ? MinStillEdge : MinEdge;
            if (info.Width < minEdge || info.Height < minEdge)
            {
                return (null, VaultImageRefusal.TooSmall);
            }

            if (info.Width > MaxSourceEdge || info.Height > MaxSourceEdge || (long)info.Width * info.Height > MaxSourcePixels)
            {
                return (null, VaultImageRefusal.TooLarge);
            }

            var aspect = (double)info.Width / info.Height;
            var (minAspect, maxAspect) = purpose == VaultImagePurpose.Cover
                ? (CoverMinAspect, CoverMaxAspect)
                : (StillMinAspect, StillMaxAspect);
            if (aspect < minAspect || aspect > maxAspect)
            {
                return (null, VaultImageRefusal.WrongShape);
            }

            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap is null || bitmap.IsEmpty)
            {
                return (null, VaultImageRefusal.CouldNotRead);
            }

            var longEdge = Math.Max(bitmap.Width, bitmap.Height);
            var working = bitmap;
            SKBitmap? resized = null;
            try
            {
                if (longEdge > MaxStoredLongEdge)
                {
                    var scale = (double)MaxStoredLongEdge / longEdge;
                    var target = new SKImageInfo(
                        Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale)),
                        bitmap.ColorType, bitmap.AlphaType);
                    resized = bitmap.Resize(target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                    working = resized ?? bitmap;
                }

                // Writing the pixels out again is what removes embedded metadata (EXIF, GPS, text chunks).
                var (encoded, mime, extension) = format switch
                {
                    SKEncodedImageFormat.Png => (SKEncodedImageFormat.Png, "image/png", ".png"),
                    SKEncodedImageFormat.Webp => (SKEncodedImageFormat.Webp, "image/webp", ".webp"),
                    _ => (SKEncodedImageFormat.Jpeg, "image/jpeg", ".jpg"),
                };
                using var image = SKImage.FromBitmap(working);
                using var output = image.Encode(encoded, 90);
                if (output is null || output.Size == 0 || output.Size > MaxFileBytes)
                {
                    return (null, VaultImageRefusal.CouldNotRead);
                }

                var result = output.ToArray();
                return (new ValidatedVaultImage(
                    result, mime, extension, working.Width, working.Height, Convert.ToHexString(SHA256.HashData(result))), VaultImageRefusal.None);
            }
            finally
            {
                resized?.Dispose();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            // An image the decoder chokes on is simply refused. The reason is deliberately not the decoder's own words.
            return (null, VaultImageRefusal.CouldNotRead);
        }
    }
}
