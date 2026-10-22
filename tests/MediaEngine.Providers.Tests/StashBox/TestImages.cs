using SkiaSharp;

namespace MediaEngine.Providers.Tests.StashBox;

/// <summary>Real, decodable images made in memory for the artwork checks. Noise keeps them well above the minimum file size.</summary>
internal static class TestImages
{
    public static byte[] Jpeg(int width, int height, int seed = 1) => Encode(width, height, SKEncodedImageFormat.Jpeg, seed);

    public static byte[] Png(int width, int height, int seed = 1) => Encode(width, height, SKEncodedImageFormat.Png, seed);

    public static byte[] Webp(int width, int height, int seed = 1) => Encode(width, height, SKEncodedImageFormat.Webp, seed);

    /// <summary>A real 1x1 GIF padded past the minimum file size with a comment block (GIF is not an accepted artwork type).</summary>
    public static byte[] Gif()
    {
        byte[] head = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF,
            0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00];
        var padding = new List<byte> { 0x21, 0xFE };
        for (var block = 0; block < 6; block++)
        {
            padding.Add(255);
            padding.AddRange(Enumerable.Repeat((byte)'x', 255));
        }

        padding.Add(0);
        return [.. head, .. padding, 0x3B];
    }

    /// <summary>A JPEG with an EXIF segment (a made-up GPS note) right after the start marker.</summary>
    public static byte[] JpegWithExif(int width, int height)
    {
        var plain = Jpeg(width, height);
        var payload = System.Text.Encoding.ASCII.GetBytes("Exif\0\0GPSLatitude=48.8566;GPSLongitude=2.3522;Make=PrivateCamera");
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        var length = payload.Length + 2;
        segment[2] = (byte)(length >> 8);
        segment[3] = (byte)(length & 0xFF);
        payload.CopyTo(segment, 4);
        var result = new byte[plain.Length + segment.Length];
        Array.Copy(plain, 0, result, 0, 2);
        segment.CopyTo(result, 2);
        Array.Copy(plain, 2, result, 2 + segment.Length, plain.Length - 2);
        return result;
    }

    private static byte[] Encode(int width, int height, SKEncodedImageFormat format, int seed)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(40, 60, 90));
            var random = new Random(seed);
            using var paint = new SKPaint();
            for (var i = 0; i < 400; i++)
            {
                paint.Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                canvas.DrawRect(random.Next(width), random.Next(height), random.Next(4, 40), random.Next(4, 40), paint);
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }
}
