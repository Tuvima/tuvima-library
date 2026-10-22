using SkiaSharp;

namespace MediaEngine.Providers.Services.Vault;

/// <summary>
/// Web-sized copies of stored Vault artwork (Videos spec D41): scene 640 and 1280 wide, performer 400 and 800, studio 300. The stored
/// original (already checked and capped at 2560 pixels by <see cref="VaultArtworkValidator"/>) is never changed; a copy is made the
/// first time a size is asked for, written next to the original under a name that carries the width, and reused from then on, so a
/// card's picture is the same file after a restart. A copy is never larger than the original: when the original is not wider than the
/// asked width the original is the answer. Copies live and die with the original (<see cref="DeleteCopies"/>).
/// </summary>
public static class VaultArtworkSizer
{
    /// <summary>The only widths the Engine will make, so a request can never fill the disk with odd sizes.</summary>
    public static readonly IReadOnlyList<int> AllowedWidths = [300, 400, 640, 800, 1280];

    private const string CopyMarker = "-w";
    private const int Quality = 85;

    public static bool IsAllowedWidth(int width) => AllowedWidths.Contains(width);

    /// <summary>
    /// The file to serve for <paramref name="width"/>: the cached copy (made now if it does not exist yet), or the original when it is
    /// not wider than that. Null when the original cannot be read or the copy cannot be written; the caller then serves the original.
    /// </summary>
    public static async Task<string?> GetOrCreateCopyAsync(string originalPath, int width, CancellationToken ct = default)
    {
        if (!IsAllowedWidth(width) || !File.Exists(originalPath))
        {
            return null;
        }

        var copyPath = CopyPath(originalPath, width);
        if (File.Exists(copyPath))
        {
            return copyPath;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(originalPath, ct).ConfigureAwait(false);
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec is null)
            {
                return null;
            }

            if (codec.Info.Width <= width)
            {
                return originalPath;
            }

            var format = codec.EncodedFormat;
            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap is null || bitmap.IsEmpty)
            {
                return null;
            }

            var height = Math.Max(1, (int)Math.Round(bitmap.Height * (double)width / bitmap.Width));
            using var resized = bitmap.Resize(
                new SKImageInfo(width, height, bitmap.ColorType, bitmap.AlphaType), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (resized is null)
            {
                return null;
            }

            using var image = SKImage.FromBitmap(resized);
            using var encoded = image.Encode(format is SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp ? format : SKEncodedImageFormat.Jpeg, Quality);
            if (encoded is null || encoded.Size == 0)
            {
                return null;
            }

            var temporary = copyPath + ".tmp";
            await File.WriteAllBytesAsync(temporary, encoded.ToArray(), ct).ConfigureAwait(false);
            File.Move(temporary, copyPath, overwrite: true);
            return copyPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OutOfMemoryException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            // The caller serves the original instead and notes the failure by its kind only.
            return null;
        }
    }

    /// <summary>Removes the web-sized copies that sit beside an original, when the original is replaced or removed. Never throws for a file in use.</summary>
    public static void DeleteCopies(string originalPath)
    {
        var directory = Path.GetDirectoryName(originalPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var width in AllowedWidths)
        {
            var copy = CopyPath(originalPath, width);
            if (!File.Exists(copy))
            {
                continue;
            }

            try
            {
                File.Delete(copy);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A copy that cannot be removed now is simply left; it is never served once its original is gone.
                continue;
            }
        }
    }

    internal static string CopyPath(string originalPath, int width)
    {
        var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(originalPath)}{CopyMarker}{width}{Path.GetExtension(originalPath)}");
    }
}
