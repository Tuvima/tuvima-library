using MediaEngine.Domain.Enums;
using MediaEngine.Processors.Contracts;
using MediaEngine.Processors.Models;

namespace MediaEngine.Processors.Processors;

/// <summary>
/// Minimal PDF processor for ingestion MVP. It validates the PDF header and
/// emits filename-based metadata so sparse PDFs become visible or reviewable.
/// </summary>
public sealed class PdfProcessor : IMediaProcessor
{
    public MediaType SupportedType => MediaType.Books;

    public int Priority => 98;

    public bool CanProcess(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        return string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase)
            && HasPdfHeader(filePath);
    }

    public Task<ProcessorResult> ProcessAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ct.ThrowIfCancellationRequested();

        if (!HasPdfHeader(filePath))
        {
            return Task.FromResult(ProcessorResultFactory.Corrupt(
                filePath,
                MediaType.Books,
                "The file has a .pdf extension but does not start with a PDF header."));
        }

        var claims = new List<ExtractedClaim>();
        var title = FileStemTitleCleaner.Clean(Path.GetFileNameWithoutExtension(filePath));

        if (!string.IsNullOrWhiteSpace(title))
        {
            claims.Add(ProcessorClaimFactory.Create("title", title, 0.50));
        }

        claims.Add(ProcessorClaimFactory.Create("container", "PDF", 1.0));

        return Task.FromResult(new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Books,
            Claims = claims,
        });
    }

    private static bool HasPdfHeader(string filePath)
    {
        Span<byte> header = stackalloc byte[5];
        return ProcessorHeaderReader.TryRead(
                filePath,
                header,
                out var read,
                FileShare.ReadWrite)
            && read == 5
            && header[0] == 0x25
            && header[1] == 0x50
            && header[2] == 0x44
            && header[3] == 0x46
            && header[4] == 0x2D;
    }
}
