using MediaEngine.Contracts.Editing;
using SkiaSharp;

namespace MediaEngine.Api.Services.Matching;

/// <summary>
/// Reads the shape of a search result's picture from the small preview the Engine already made for it (universal editor v7, D7). Only a
/// square or wide picture is reported: that is the provider saying its own shape, which wins over the kind's usual one. A portrait,
/// a missing picture or one that cannot be read gives null and the Dashboard uses the kind's shape.
/// </summary>
internal static class EditorMatchImageShape
{
    private const string DataUrlMarker = ";base64,";

    // A ratio (width over height) within this band of 1 is square; anything clearly wider than tall is wide.
    private const double SquareLow = 0.9;
    private const double SquareHigh = 1.1;

    /// <summary>"square" or "wide" for a preview that is, otherwise null.</summary>
    internal static string? FromPreview(string? previewUrl)
    {
        if (string.IsNullOrWhiteSpace(previewUrl) || !previewUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var marker = previewUrl.IndexOf(DataUrlMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        var encoded = previewUrl.AsSpan(marker + DataUrlMarker.Length);
        var bytes = new byte[encoded.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64Chars(encoded, bytes, out var written))
        {
            return null;
        }

        using var data = SKData.CreateCopy(bytes.AsSpan(0, written));
        using var codec = SKCodec.Create(data);
        return codec is null ? null : FromSize(codec.Info.Width, codec.Info.Height);
    }

    /// <summary>"square" or "wide" for these pixel sizes, otherwise null.</summary>
    internal static string? FromSize(int width, int height)
    {
        if (width < 1 || height < 1)
        {
            return null;
        }

        var ratio = width / (double)height;
        if (ratio is >= SquareLow and <= SquareHigh)
        {
            return EditorMatchImageShapes.Square;
        }

        return ratio > SquareHigh ? EditorMatchImageShapes.Wide : null;
    }
}
