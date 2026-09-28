using System.Xml;
using System.Xml.Linq;
using MediaEngine.Domain.Enums;
using MediaEngine.Processors.Contracts;
using MediaEngine.Processors.Models;

namespace MediaEngine.Processors.Processors;

/// <summary>Indexes Kindle AZW3/MOBI books and reads Calibre OPF companions when present.</summary>
public sealed class AzW3Processor : IMediaProcessor
{
    public MediaType SupportedType => MediaType.Books;
    public int Priority => 99;

    public bool CanProcess(string filePath)
        => File.Exists(filePath)
           && string.Equals(Path.GetExtension(filePath), ".azw3", StringComparison.OrdinalIgnoreCase)
           && HasBookMobiHeader(filePath);

    public Task<ProcessorResult> ProcessAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ct.ThrowIfCancellationRequested();
        if (!HasBookMobiHeader(filePath))
        {
            return Task.FromResult(ProcessorResultFactory.Corrupt(filePath, MediaType.Books, "The AZW3 file does not contain a BOOKMOBI header."));
        }

        var claims = new List<ExtractedClaim>
        {
            ProcessorClaimFactory.Create("title", Path.GetFileNameWithoutExtension(filePath), 0.5),
            ProcessorClaimFactory.Create("container", "AZW3", 1.0),
            ProcessorClaimFactory.Create("reader_support", "external", 1.0),
        };

        var directory = Path.GetDirectoryName(filePath)!;
        var companion = BookCompanionMetadata.Read(filePath);
        if (companion is not null)
        {
            ReadOpf(companion, claims);
            BookCompanionMetadata.AddIdentifiers(companion, claims);
        }

        var coverPath = Directory.EnumerateFiles(directory)
            .FirstOrDefault(path => Path.GetFileNameWithoutExtension(path).Equals("cover", StringComparison.OrdinalIgnoreCase)
                                 && Path.GetExtension(path) is ".jpg" or ".jpeg" or ".png");

        byte[]? cover = coverPath is null ? null : File.ReadAllBytes(coverPath);
        var mime = coverPath is null ? null
            : Path.GetExtension(coverPath).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";

        return Task.FromResult(new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Books,
            Claims = claims,
            CoverImage = cover,
            CoverImageMimeType = mime,
        });
    }

    private static bool HasBookMobiHeader(string filePath)
    {
        Span<byte> header = stackalloc byte[68];
        if (!ProcessorHeaderReader.TryRead(filePath, header, out var read) || read < header.Length)
        {
            return false;
        }

        return header[60..68].SequenceEqual("BOOKMOBI"u8);
    }

    private static void ReadOpf(XDocument document, List<ExtractedClaim> claims)
    {
        try
        {
            Add("title", "title", 0.98);
            Add("creator", "author", 0.98);
            Add("publisher", "publisher", 0.95);
            Add("language", "language", 0.95);
            Add("description", "description", 0.95);
            Add("date", "published_date", 0.9);

            void Add(string elementName, string claimKey, double confidence)
            {
                var value = document.Descendants().FirstOrDefault(element => element.Name.LocalName == elementName)?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    claims.Add(ProcessorClaimFactory.Create(claimKey, value, confidence));
                }
            }
        }
        catch (XmlException)
        {
            // Optional Calibre metadata must never invalidate an otherwise valid book.
        }
    }
}
