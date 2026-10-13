namespace MediaEngine.Api.Services.LocalAssets.Ocr;

/// <summary>File types the text recognition job reads.</summary>
public static class OcrFormats
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".webp", ".bmp", ".heic", ".heif",
    };

    public static IReadOnlyCollection<string> Images => ImageExtensions;

    public static bool IsImage(string extension) => ImageExtensions.Contains(extension);

    public static bool IsPdf(string extension) => string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for files the OCR job can read at all (scanned PDFs and photos of paper).</summary>
    public static bool IsCandidate(string extension) => IsPdf(extension) || IsImage(extension);
}
