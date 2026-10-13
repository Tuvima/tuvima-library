using PDFtoImage;
using SkiaSharp;

// PDFtoImage marks its calls as unsupported on a few mobile and browser targets. The Engine only runs on
// Windows, Linux and macOS hosts, where PDFium ships with the package.
#pragma warning disable CA1416

namespace MediaEngine.Api.Services.LocalAssets.Ocr;

/// <summary>
/// Draws PDF pages as grayscale images with PDFium (through PDFtoImage). Used by text recognition
/// and available to anything else that needs a picture of a page, such as first-page thumbnails.
/// The caller owns and disposes every returned bitmap.
/// </summary>
public static class PdfPageRenderer
{
    private const float PointsPerInch = 72f;

    /// <summary>
    /// Draws one page. <paramref name="pageIndex"/> is zero-based. The page is drawn at
    /// <paramref name="dpi"/> but never larger than <paramref name="maxSidePixels"/> on its longest side.
    /// </summary>
    public static SKBitmap RenderPage(string pdfPath, int pageIndex, int dpi, int maxSidePixels)
    {
        using var stream = OpenRead(pdfPath);
        var size = Conversion.GetPageSize(stream, pageIndex, leaveOpen: true);
        return Conversion.ToImage(
            stream, pageIndex, leaveOpen: true, options: OptionsFor(size.Width, size.Height, dpi, maxSidePixels));
    }

    /// <summary>
    /// Draws the given zero-based pages one at a time, in the order given. Each bitmap is created when
    /// the caller asks for it, so a long document never holds every page in memory.
    /// </summary>
    public static IEnumerable<(int PageIndex, SKBitmap Bitmap)> RenderPages(
        string pdfPath, IReadOnlyList<int> pageIndexes, int dpi, int maxSidePixels)
    {
        if (pageIndexes.Count == 0)
        {
            yield break;
        }

        using var stream = OpenRead(pdfPath);
        var sizes = Conversion.GetPageSizes(stream, leaveOpen: true);
        foreach (var pageIndex in pageIndexes)
        {
            if (pageIndex < 0 || pageIndex >= sizes.Count)
            {
                continue;
            }

            var size = sizes[pageIndex];
            yield return (pageIndex, Conversion.ToImage(
                stream, pageIndex, leaveOpen: true, options: OptionsFor(size.Width, size.Height, dpi, maxSidePixels)));
        }
    }

    private static FileStream OpenRead(string path) => new(
        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 81920);

    internal static int EffectiveDpi(float widthPoints, float heightPoints, int dpi, int maxSidePixels)
    {
        var longest = Math.Max(widthPoints, heightPoints);
        if (longest <= 0 || maxSidePixels <= 0)
        {
            return Math.Max(1, dpi);
        }

        var cap = (int)Math.Floor(maxSidePixels * PointsPerInch / longest);
        return Math.Max(1, Math.Min(dpi, cap));
    }

    private static RenderOptions OptionsFor(float widthPoints, float heightPoints, int dpi, int maxSidePixels) =>
        new(Dpi: EffectiveDpi(widthPoints, heightPoints, dpi, maxSidePixels))
        {
            Grayscale = true,
            BackgroundColor = SKColors.White,
        };
}
