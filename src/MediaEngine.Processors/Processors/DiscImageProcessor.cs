using System.Text.RegularExpressions;
using MediaEngine.Domain.Enums;
using MediaEngine.Processors.Contracts;
using MediaEngine.Processors.Models;

namespace MediaEngine.Processors.Processors;

/// <summary>Recognizes ISO-9660 optical-disc images without pretending they are browser-playable files.</summary>
public sealed partial class DiscImageProcessor : IMediaProcessor
{
    public MediaType SupportedType => MediaType.Movies;
    public int Priority => 97;

    public bool CanProcess(string filePath)
        => File.Exists(filePath)
           && string.Equals(Path.GetExtension(filePath), ".iso", StringComparison.OrdinalIgnoreCase)
           && HasIso9660Signature(filePath);

    public Task<ProcessorResult> ProcessAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ct.ThrowIfCancellationRequested();
        if (!HasIso9660Signature(filePath))
        {
            return Task.FromResult(ProcessorResultFactory.Corrupt(filePath, MediaType.Movies, "The disc image does not contain an ISO-9660 volume descriptor."));
        }

        var stem = FileStemTitleCleaner.Clean(Path.GetFileNameWithoutExtension(filePath));
        var yearMatch = TrailingYear().Match(stem);
        var title = yearMatch.Success ? stem[..yearMatch.Index].Trim() : stem;
        var claims = new List<ExtractedClaim>
        {
            ProcessorClaimFactory.Create("title", title, 0.55),
            ProcessorClaimFactory.Create("container", "ISO", 1.0),
            ProcessorClaimFactory.Create("playback_support", "disc-image-requires-extraction", 1.0),
        };
        if (yearMatch.Success)
        {
            claims.Add(ProcessorClaimFactory.Create("year", yearMatch.Groups[1].Value, 0.75));
        }

        return Task.FromResult(new ProcessorResult { FilePath = filePath, DetectedType = MediaType.Movies, Claims = claims });
    }

    private static bool HasIso9660Signature(string path)
    {
        Span<byte> signature = stackalloc byte[5];
        return ProcessorHeaderReader.TryRead(path, signature, out var read, FileShare.ReadWrite, 0x8001)
            && read == signature.Length
            && signature.SequenceEqual("CD001"u8);
    }

    [GeneratedRegex(@"\s*[\[(]?(\d{4})[\])]?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingYear();
}
