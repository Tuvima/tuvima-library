using SkiaSharp;

namespace MediaEngine.Api.Services.LocalAssets.Ocr;

/// <summary>
/// Light preparation before text recognition: grayscale, correct the camera's own rotation,
/// optionally turn the page, and shrink very large images. No filtering beyond that.
/// </summary>
public static class OcrImagePreparer
{
    /// <summary>Decodes a photo or scan and applies its embedded orientation. Null when the format cannot be decoded.</summary>
    public static SKBitmap? Decode(string path)
    {
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
        {
            return null;
        }

        var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null)
        {
            return null;
        }

        var oriented = ApplyOrigin(bitmap, codec.EncodedOrigin);
        if (!ReferenceEquals(oriented, bitmap))
        {
            bitmap.Dispose();
        }

        return oriented;
    }

    /// <summary>Returns a grayscale copy, turned clockwise by <paramref name="rotationDegrees"/> and limited to <paramref name="maxSidePixels"/>.</summary>
    public static SKBitmap Prepare(SKBitmap source, int rotationDegrees, int maxSidePixels)
    {
        var turned = Rotate(source, ((rotationDegrees % 360) + 360) % 360);
        try
        {
            var longest = Math.Max(turned.Width, turned.Height);
            var scale = maxSidePixels > 0 && longest > maxSidePixels ? (float)maxSidePixels / longest : 1f;
            var width = Math.Max(1, (int)Math.Round(turned.Width * scale));
            var height = Math.Max(1, (int)Math.Round(turned.Height * scale));
            var info = new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
            var gray = new SKBitmap(info);
            using var canvas = new SKCanvas(gray);
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(turned, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), null);
            return gray;
        }
        finally
        {
            if (!ReferenceEquals(turned, source))
            {
                turned.Dispose();
            }
        }
    }

    public static void SavePng(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(path);
        data.SaveTo(output);
    }

    private static SKBitmap Rotate(SKBitmap source, int degrees)
    {
        if (degrees == 0)
        {
            return source;
        }

        var swap = degrees is 90 or 270;
        var turned = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height);
        using var canvas = new SKCanvas(turned);
        canvas.Translate(turned.Width / 2f, turned.Height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0, SKSamplingOptions.Default, null);
        return turned;
    }

    private static SKBitmap ApplyOrigin(SKBitmap source, SKEncodedOrigin origin)
    {
        switch (origin)
        {
            case SKEncodedOrigin.BottomRight:
                return Rotate(source, 180);
            case SKEncodedOrigin.RightTop:
                return Rotate(source, 90);
            case SKEncodedOrigin.LeftBottom:
                return Rotate(source, 270);
            default:
                // Mirrored origins and an upright image are left as they are: mirrored scans are rare
                // and the text engine reads the common cases correctly after the rotation above.
                return source;
        }
    }
}
