using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Storage.Contracts;
using SkiaSharp;

namespace MediaEngine.Api.Services.LocalAssets.Intake;

/// <summary>
/// A quick, local check that a photo or video was copied in full and is not damaged (Photo and video filing spec D6). It reads only the
/// start and end of a file and, for the MP4 family, the list of boxes, so a card of thousands of files is checked without reading every
/// byte. A file of a kind it has no check for passes; the decoder catches anything it can't read later and the file is listed then.
/// </summary>
public static class ViewFileIntegrity
{
    /// <summary>The reason code the file is refused with (<c>damaged</c> or <c>empty</c>), or null when it looks complete.</summary>
    public static string? Check(string path, string mediaKind)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            if (info.Length == 0)
            {
                return MediaEngine.Domain.PersonalMedia.ViewIntakeReasons.Empty;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var intact = mediaKind switch
            {
                LocalAssetMediaKinds.Image => ImageIsIntact(stream, extension),
                LocalAssetMediaKinds.Video => VideoIsIntact(stream, extension),
                _ => true,
            };
            return intact ? null : MediaEngine.Domain.PersonalMedia.ViewIntakeReasons.Damaged;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file that can't be opened right now is not called damaged: the caller leaves it for the next pass.
            _ = exception;
            return null;
        }
    }

    private static bool ImageIsIntact(FileStream stream, string extension)
    {
        var head = Read(stream, 0, 16);
        switch (extension)
        {
            case ".jpg" or ".jpeg":
                // Starts with the JPEG marker and ends with the end-of-image marker (a camera or card may pad with zeros).
                return head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8
                    && (EndsWith(stream, [0xFF, 0xD9], allowZeroPadding: true) || JpegDecodesInFull(stream));
            case ".png":
                return head.Length >= 8 && head.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
                    && EndsWith(stream, [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82], allowZeroPadding: false);
            case ".gif":
                return head.Length >= 6 && head[0] == 'G' && head[1] == 'I' && head[2] == 'F' && EndsWith(stream, [0x3B], allowZeroPadding: true);
            case ".bmp":
                return head.Length >= 6 && head[0] == 'B' && head[1] == 'M' && BitConverter.ToUInt32(head, 2) <= stream.Length;
            case ".webp":
                return head.Length >= 12 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
                    && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P'
                    && (long)BitConverter.ToUInt32(head, 4) + 8 <= stream.Length;
            case ".heic" or ".heif" or ".avif" or ".cr3":
                return BoxesAreComplete(stream, requireMovie: false);
            default:
                return true;
        }
    }

    /// <summary>
    /// A JPEG that does not end with the end-of-image marker may still be whole (a motion photo carries a video after it). Decoding it at
    /// one eighth of its size reads every scan, so a file that was cut short reports incomplete input.
    /// </summary>
    private static bool JpegDecodesInFull(FileStream stream)
    {
        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            using var codec = SKCodec.Create(stream);
            if (codec is null)
            {
                return false;
            }

            var size = codec.GetScaledDimensions(1f / 8f);
            var info = new SKImageInfo(Math.Max(1, size.Width), Math.Max(1, size.Height), SKColorType.Rgba8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            return codec.GetPixels(info, bitmap.GetPixels()) == SKCodecResult.Success;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
        {
            // A decoder that cannot read the file says nothing about the file being cut short; the strict marker check already failed.
            _ = exception;
            return false;
        }
    }

    private static bool VideoIsIntact(FileStream stream, string extension)
    {
        var head = Read(stream, 0, 16);
        switch (extension)
        {
            case ".mp4" or ".m4v" or ".mov":
                return BoxesAreComplete(stream, requireMovie: true);
            case ".mkv" or ".webm":
                return head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3;
            case ".avi":
                return head.Length >= 12 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
                    && head[8] == 'A' && head[9] == 'V' && head[10] == 'I' && (long)BitConverter.ToUInt32(head, 4) + 8 <= stream.Length;
            default:
                return true;
        }
    }

    /// <summary>
    /// The MP4 family is a list of boxes, each starting with its own length. A file copied in full ends exactly at the end of its last
    /// box; one cut short ends in the middle of a box. A movie needs its <c>moov</c> box (a recording that was stopped early often has
    /// none).
    /// </summary>
    private static bool BoxesAreComplete(FileStream stream, bool requireMovie)
    {
        long position = 0;
        var sawMovie = false;
        var sawFileType = false;
        var guard = 0;
        while (position < stream.Length && guard++ < 100_000)
        {
            var header = Read(stream, position, 16);
            if (header.Length < 8)
            {
                return false;
            }

            long size = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            if (size == 1)
            {
                if (header.Length < 16)
                {
                    return false;
                }

                size = 0;
                for (var index = 8; index < 16; index++)
                {
                    size = (size << 8) | header[index];
                }
            }
            else if (size == 0)
            {
                // A box that runs to the end of the file.
                size = stream.Length - position;
            }

            if (size < 8 || position + size > stream.Length)
            {
                return false;
            }

            sawFileType |= type == "ftyp";
            sawMovie |= type == "moov";
            position += size;
        }

        return position == stream.Length && sawFileType && (!requireMovie || sawMovie);
    }

    private static byte[] Read(FileStream stream, long position, int count)
    {
        if (position >= stream.Length)
        {
            return [];
        }

        var buffer = new byte[(int)Math.Min(count, stream.Length - position)];
        stream.Seek(position, SeekOrigin.Begin);
        var read = 0;
        while (read < buffer.Length)
        {
            var step = stream.Read(buffer, read, buffer.Length - read);
            if (step == 0)
            {
                break;
            }

            read += step;
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }

    private static bool EndsWith(FileStream stream, byte[] tail, bool allowZeroPadding)
    {
        // Read the last bytes; ignore trailing zero padding when allowed.
        var window = (int)Math.Min(stream.Length, allowZeroPadding ? 4096 : tail.Length);
        var buffer = Read(stream, stream.Length - window, window);
        var end = buffer.Length;
        if (allowZeroPadding)
        {
            while (end > 0 && buffer[end - 1] == 0)
            {
                end--;
            }
        }

        return end >= tail.Length && buffer.AsSpan(end - tail.Length, tail.Length).SequenceEqual(tail);
    }
}
