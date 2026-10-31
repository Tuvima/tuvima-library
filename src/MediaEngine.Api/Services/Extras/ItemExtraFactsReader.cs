using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using UglyToad.PdfPig;

namespace MediaEngine.Api.Services.Extras;

/// <summary>Reads the length of a clip and the page count of a PDF for an Extras card. A file it cannot read simply shows no fact.</summary>
public sealed class ItemExtraFactsReader(IFFmpegService? ffmpeg, ILogger<ItemExtraFactsReader> logger) : IItemExtraFactsReader
{
    public async Task<ItemExtraFacts> ReadAsync(string filePath, string kind, CancellationToken ct = default)
    {
        try
        {
            if (kind == ItemExtraKinds.Clip && ffmpeg is not null)
            {
                var probe = await ffmpeg.ProbeAsync(filePath, ct).ConfigureAwait(false);
                return new ItemExtraFacts(probe is { Duration.TotalSeconds: > 0 } ? probe.Duration.TotalSeconds : null, null);
            }

            if (kind == ItemExtraKinds.Document && Path.GetExtension(filePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return await Task.Run(() => ReadPdfPages(filePath), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read the facts of an Extra ({Extension})", Path.GetExtension(filePath));
        }

        return new ItemExtraFacts(null, null);
    }

    private ItemExtraFacts ReadPdfPages(string filePath)
    {
        try
        {
            using var document = PdfDocument.Open(filePath, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
            return new ItemExtraFacts(null, document.NumberOfPages);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Password-protected, damaged or empty PDFs (PdfPig raises several kinds of error for them) simply show no page count.
            logger.LogDebug(ex, "Could not count the pages of an Extra");
            return new ItemExtraFacts(null, null);
        }
    }
}
