using SkiaSharp;

namespace MediaEngine.Api.Services.Faces;

/// <summary>
/// Reads a photo upright, the way a person sees it. Face boxes are saved as fractions of this upright picture, so the finder, the face
/// pictures and the box drawn in the viewer all agree even when the camera stored the photo turned.
/// </summary>
public static class OrientedImage
{
    /// <summary>The largest side kept in memory; a face scan does not need more.</summary>
    public const int MaxSide = 1600;

    /// <summary>Loads the file upright and scaled so its longest side is at most <paramref name="maxSide"/>. Null when it cannot be read.</summary>
    public static SKBitmap? Load(string path, int maxSide = MaxSide)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null)
        {
            return null;
        }

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
        {
            return null;
        }

        using var upright = Orient(decoded, codec.EncodedOrigin);
        var longest = Math.Max(upright.Width, upright.Height);
        if (longest <= maxSide)
        {
            return upright.Copy();
        }

        var scale = (double)maxSide / longest;
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(upright.Width * scale)), Math.Max(1, (int)Math.Round(upright.Height * scale)), SKColorType.Rgba8888, SKAlphaType.Premul);
        return upright.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    /// <summary>Cuts a square around a face box (with a little room around it) and scales it to <paramref name="size"/> pixels, as a JPEG.</summary>
    public static byte[]? CropFace(SKBitmap picture, double x, double y, double width, double height, int size)
    {
        var centreX = (x + width / 2) * picture.Width;
        var centreY = (y + height / 2) * picture.Height;
        var side = Math.Max(width * picture.Width, height * picture.Height) * 1.5;
        var left = (int)Math.Round(Math.Clamp(centreX - side / 2, 0, Math.Max(0, picture.Width - 1)));
        var top = (int)Math.Round(Math.Clamp(centreY - side / 2, 0, Math.Max(0, picture.Height - 1)));
        var cropWidth = (int)Math.Min(side, picture.Width - left);
        var cropHeight = (int)Math.Min(side, picture.Height - top);
        if (cropWidth < 1 || cropHeight < 1)
        {
            return null;
        }

        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        // Keep the face's own proportions: fit the cut inside the square instead of stretching it.
        var scale = Math.Min((double)size / cropWidth, (double)size / cropHeight);
        var drawWidth = (float)(cropWidth * scale);
        var drawHeight = (float)(cropHeight * scale);
        var destination = new SKRect((size - drawWidth) / 2, (size - drawHeight) / 2, (size + drawWidth) / 2, (size + drawHeight) / 2);
        using var image = SKImage.FromBitmap(picture);
        canvas.DrawImage(image, new SKRect(left, top, left + cropWidth, top + cropHeight), destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data?.ToArray();
    }

    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return source.Copy();
        }

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = swap ? source.Height : source.Width;
        var height = swap ? source.Width : source.Height;
        var result = new SKBitmap(width, height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, source.Width, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, source.Width, 0, -1, source.Height, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, source.Height, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, source.Height, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, source.Height, -1, 0, source.Width, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, source.Width, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        canvas.SetMatrix(matrix);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), null);
        return result;
    }
}
