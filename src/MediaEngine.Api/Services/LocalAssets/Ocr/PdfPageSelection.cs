using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace MediaEngine.Api.Services.LocalAssets.Ocr;

/// <summary>The text layer of one PDF page. <see cref="Number"/> is 1-based.</summary>
public sealed record PdfPageText(int Number, string Text, int NonWhitespaceCharacters);

/// <summary>What the text layers of a PDF look like, page by page.</summary>
public sealed record PdfTextLayerReport(int PageCount, IReadOnlyList<PdfPageText> Pages, bool Encrypted);

/// <summary>
/// Decides which PDF pages need text recognition: a page whose own text layer holds fewer
/// non-whitespace characters than the threshold is treated as a scan. Pages with a text layer
/// are never re-read.
/// </summary>
public static class PdfPageSelection
{
    public static int CountNonWhitespace(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>1-based numbers of the pages that need OCR, in page order.</summary>
    public static IReadOnlyList<int> PagesNeedingOcr(IEnumerable<PdfPageText> pages, int minimumCharacters) =>
        pages.Where(page => page.NonWhitespaceCharacters < minimumCharacters)
            .Select(page => page.Number)
            .ToList();

    /// <summary>Reads each page's own text layer without rendering anything.</summary>
    public static PdfTextLayerReport ReadTextLayers(string path, int maxPages, CancellationToken ct)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920, FileOptions.SequentialScan);
            using var document = PdfDocument.Open(stream, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            var total = document.NumberOfPages;
            var limit = Math.Min(total, Math.Max(1, maxPages));
            var pages = new List<PdfPageText>(limit);
            for (var number = 1; number <= limit; number++)
            {
                ct.ThrowIfCancellationRequested();
                var text = document.GetPage(number).Text ?? string.Empty;
                pages.Add(new PdfPageText(number, text, CountNonWhitespace(text)));
            }

            return new PdfTextLayerReport(total, pages, Encrypted: false);
        }
        catch (PdfDocumentEncryptedException)
        {
            return new PdfTextLayerReport(0, [], Encrypted: true);
        }
    }
}
