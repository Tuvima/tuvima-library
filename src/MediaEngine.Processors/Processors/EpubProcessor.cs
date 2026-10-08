using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using MediaEngine.Domain;
using MediaEngine.Domain.Enums;
using MediaEngine.Processors.Contracts;
using MediaEngine.Processors.Models;
using VersOne.Epub;

namespace MediaEngine.Processors.Processors;

/// <summary>
/// Identifies and extracts metadata from EPUB 2 and EPUB 3 files.
///
/// ──────────────────────────────────────────────────────────────────
/// Format identification (spec: Phase 5 – Magic-Byte Detection)
/// ──────────────────────────────────────────────────────────────────
///  Detection is two-stage:
///   1. ZIP magic bytes: bytes 0-3 must be 50 4B 03 04.
///   2. EPUB MIME entry: the ZIP must contain a file called "mimetype"
///      whose content (trimmed) is "application/epub+zip".
///  This is the canonical EPUB 3 detection algorithm from the IDPF spec.
///  Stage 2 is skipped if stage 1 fails, keeping the hot path fast.
///
/// ──────────────────────────────────────────────────────────────────
/// Metadata extraction (spec: Phase 5 – Metadata Extraction)
/// ──────────────────────────────────────────────────────────────────
///  All claims are extracted from the OPF package document via
///  <see cref="EpubReader"/>; embedded cover image is returned as raw bytes.
///
///  Extracted claims:
///   • title        (confidence 0.95 — trustworthy OPF element)
///   • author       (one claim per author, confidence 0.95)
///   • publisher    (confidence 0.95)
///   • language     (confidence 0.95)
///   • description  (confidence 0.95)
///   • date         (confidence 0.95)
///   \u2022 isbn         (confidence 1.0 \u2014 dc:identifier with ISBN scheme)
///   \u2022 word_count   (confidence 1.0 \u2014 counted from reading-order content)
///
///  Cover image: MIME type is sniffed from the first 4 bytes of the image
///  (JPEG, PNG, GIF, WebP); falls back to "image/jpeg" for unknown formats.
///
/// Spec: Phase 5 – Media Processor Architecture § EPUB Processor.
/// </summary>
public sealed class EpubProcessor : IMediaProcessor
{
    // ZIP local-file-header signature: PK\x03\x04
    private static ReadOnlySpan<byte> ZipMagic => [0x50, 0x4B, 0x03, 0x04];

    private const string EpubMimeType = "application/epub+zip";
    private const string MimeTypeEntryName = "mimetype";

    /// <inheritdoc/>
    public MediaType SupportedType => MediaType.Books;

    /// <inheritdoc/>
    /// <remarks>
    /// 100 — high-fidelity EPUB handler.
    /// Placed above any future PDF/Comic processors that also use ZIP
    /// (e.g. CBZ) so the EPUB mimetype check disambiguates them.
    /// </remarks>
    public int Priority => 100;

    // -------------------------------------------------------------------------
    // IMediaProcessor
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    /// <remarks>
    /// Reads ≤ 4 bytes for the ZIP magic check and then opens the ZIP archive
    /// to read only the "mimetype" entry (typically &lt; 30 bytes).
    /// Total I/O: roughly 2–4 KB for the central directory + mimetype.
    /// </remarks>
    public bool CanProcess(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        // Stage 1: ZIP magic bytes.
        if (!HasZipMagic(filePath))
        {
            return false;
        }

        // Stage 2: EPUB mimetype entry.
        return HasEpubMimeType(filePath);
    }

    /// <inheritdoc/>
    public async Task<ProcessorResult> ProcessAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        // Re-check magic so ProcessAsync is safe to call without a prior CanProcess.
        if (!HasZipMagic(filePath))
        {
            return Corrupt(filePath, "File does not begin with ZIP magic bytes (not an EPUB).");
        }

        EpubBook book;
        try
        {
            book = await EpubReader.ReadBookAsync(filePath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Corrupt(filePath, $"EpubReader failed to parse file: {ex.Message}");
        }

        ct.ThrowIfCancellationRequested();

        var claims = BuildClaims(book);
        if (BookCompanionMetadata.Read(filePath) is { } companion)
        {
            BookCompanionMetadata.AddIdentifiers(companion, claims);
        }
        AddWordCount(book, claims);
        var (coverBytes, coverMime) = ExtractCover(book);

        return new ProcessorResult
        {
            FilePath = filePath,
            DetectedType = MediaType.Books,
            Claims = claims,
            CoverImage = coverBytes,
            CoverImageMimeType = coverMime,
        };
    }

    // -------------------------------------------------------------------------
    // Magic-byte helpers
    // -------------------------------------------------------------------------

    private static bool HasZipMagic(string filePath)
    {
        Span<byte> header = stackalloc byte[4];
        return ProcessorHeaderReader.TryRead(filePath, header, out var read)
            && read == 4
            && header.SequenceEqual(ZipMagic);
    }

    private static bool HasEpubMimeType(string filePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(filePath);
            var entry = zip.GetEntry(MimeTypeEntryName);
            if (entry is null)
            {
                return false;
            }

            using var reader = new StreamReader(entry.Open(), Encoding.ASCII, detectEncodingFromByteOrderMarks: false);
            var content = reader.ReadToEnd().Trim();
            return string.Equals(content, EpubMimeType, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidDataException) { return false; } // Not a valid ZIP
        catch (IOException) { return false; }
    }

    // -------------------------------------------------------------------------
    // Metadata extraction
    // -------------------------------------------------------------------------

    private static List<ExtractedClaim> BuildClaims(EpubBook book)
    {
        var claims = new List<ExtractedClaim>();

        // Title
        if (!string.IsNullOrWhiteSpace(book.Title))
        {
            claims.Add(Claim(MetadataFieldConstants.Title, book.Title));
        }

        // Authors (one claim per author)
        if (book.AuthorList is { Count: > 0 })
        {
            foreach (var author in book.AuthorList)
            {
                if (!string.IsNullOrWhiteSpace(author))
                {
                    claims.Add(Claim(MetadataFieldConstants.Author, author));
                }
            }
        }

        // Schema package metadata (VersOne exposes OPF metadata via Schema)
        // VersOne.Epub 3.1.0 API:
        //   Publishers / Languages / Subjects / Sources are List<string>
        //   Description is a single string (not a list)
        //   Dates is List<EpubMetadataDate>  — value is in .Date property
        //   Identifiers is List<EpubMetadataIdentifier> — value is in .Identifier property
        var meta = book.Schema?.Package?.Metadata;
        if (meta is not null)
        {
            // Publisher
            var publisher = meta.Publishers?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(publisher))
            {
                claims.Add(Claim(MetadataFieldConstants.PublisherField, publisher));
            }

            // Language
            var language = meta.Languages?.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(language))
            {
                claims.Add(Claim(MetadataFieldConstants.Language, language));
            }

            // Description (single string in 3.1.0)
            if (!string.IsNullOrWhiteSpace(meta.Description))
            {
                claims.Add(Claim(MetadataFieldConstants.Description, meta.Description));
            }

            // Date
            var date = meta.Dates?.FirstOrDefault()?.Date;
            if (!string.IsNullOrWhiteSpace(date))
            {
                claims.Add(Claim("date", date));
            }

            // Series — look for calibre:series and calibre:series_index in <meta> elements.
            // EPUB 2: <meta name="calibre:series" content="Dune Chronicles"/>
            // EPUB 3: <meta property="belongs-to-collection">Dune Chronicles</meta>
            if (meta.MetaItems is { Count: > 0 })
            {
                foreach (var metaItem in meta.MetaItems)
                {
                    // EPUB 2 calibre:series
                    if (string.Equals(metaItem.Name, "calibre:series", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(metaItem.Content))
                    {
                        claims.Add(Claim(MetadataFieldConstants.Series, metaItem.Content));
                    }
                    else if (string.Equals(metaItem.Name, "calibre:series_index", StringComparison.OrdinalIgnoreCase)
                             && !string.IsNullOrWhiteSpace(metaItem.Content))
                    {
                        claims.Add(Claim(MetadataFieldConstants.SeriesPosition, metaItem.Content));
                    }
                    // EPUB 3 belongs-to-collection
                    else if (string.Equals(metaItem.Property, "belongs-to-collection", StringComparison.OrdinalIgnoreCase)
                             && !string.IsNullOrWhiteSpace(metaItem.Content))
                    {
                        // Only add if we haven't already found a calibre:series
                        if (!claims.Any(c => c.Key == MetadataFieldConstants.Series))
                        {
                            claims.Add(Claim(MetadataFieldConstants.Series, metaItem.Content));
                        }
                    }
                    else if (string.Equals(metaItem.Property, "group-position", StringComparison.OrdinalIgnoreCase)
                             && !string.IsNullOrWhiteSpace(metaItem.Content))
                    {
                        if (!claims.Any(c => c.Key == MetadataFieldConstants.SeriesPosition))
                        {
                            claims.Add(Claim(MetadataFieldConstants.SeriesPosition, metaItem.Content));
                        }
                    }
                }
            }

            // ISBN — look for dc:identifier with ISBN scheme
            if (meta.Identifiers is { Count: > 0 })
            {
                foreach (var id in meta.Identifiers)
                {
                    var scheme = id.Scheme;
                    var text = id.Identifier;  // VersOne 3.1.0: .Identifier not .Text
                    if (!string.IsNullOrWhiteSpace(text) &&
                        (string.Equals(scheme, "ISBN", StringComparison.OrdinalIgnoreCase) ||
                         text.StartsWith("isbn:", StringComparison.OrdinalIgnoreCase) ||
                         text.StartsWith("urn:isbn:", StringComparison.OrdinalIgnoreCase)))
                    {
                        var cleanIsbn = StripIsbnPrefix(text);
                        if (!string.IsNullOrEmpty(cleanIsbn))
                        {
                            claims.Add(Claim("isbn", cleanIsbn));
                        }
                    }
                }

                // Fallback: detect bare ISBNs without scheme or prefix.
                // Many EPUBs embed ISBNs as plain <dc:identifier>9781234567890</dc:identifier>.
                if (!claims.Any(c => c.Key == "isbn"))
                {
                    foreach (var id in meta.Identifiers)
                    {
                        var raw = MediaEngine.Domain.Services.IsbnValidation.NormalizeValid(id.Identifier);
                        if (raw is null)
                        {
                            continue;
                        }

                        // ISBN-13 (978/979 prefix, 13 digits) or ISBN-10 (10 chars, last may be X)
                        if (raw is not null)
                        {
                            claims.Add(Claim("isbn", raw));
                            break;
                        }
                    }
                }
            }
        }

        return claims;
    }


    /// <summary>
    /// Counts words across the EPUB reading order and appends a word_count claim.
    /// Words are counted by stripping HTML tags and splitting on whitespace.
    /// Average adult reading speed is ~250 wpm; the Dashboard converts this to
    /// a human-readable reading-time estimate.
    /// </summary>
    private static void AddWordCount(EpubBook book, List<ExtractedClaim> claims)
    {
        if (book.ReadingOrder is not { Count: > 0 })
        {
            return;
        }

        var total = 0;
        foreach (var chapterContent in book.ReadingOrder)
        {
            if (string.IsNullOrWhiteSpace(chapterContent.Content))
            {
                continue;
            }

            // Strip HTML tags, then split on whitespace.
            var text = Regex.Replace(chapterContent.Content, "<[^>]+>", " ");
            total += text.Split(new char[] { ' ', (char)9, (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        if (total > 0)
        {
            claims.Add(Claim("word_count", total.ToString()));
        }
    }
    // -------------------------------------------------------------------------
    // Cover image extraction
    // -------------------------------------------------------------------------

    private static (byte[]? bytes, string? mime) ExtractCover(EpubBook book)
    {
        byte[]? coverBytes = book.CoverImage;
        if (coverBytes is null || coverBytes.Length == 0)
        {
            return (null, null);
        }

        return (coverBytes, SniffMimeType(coverBytes));
    }

    /// <summary>
    /// Sniffs the IANA MIME type from the leading bytes of an image blob.
    /// Falls back to <c>"image/jpeg"</c> for unrecognised formats since JPEG
    /// is the dominant cover format in practice.
    /// </summary>
    private static string SniffMimeType(byte[] data)
    {
        if (data.Length < 4)
        {
            return "image/jpeg";
        }

        // JPEG: FF D8 FF
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        // PNG: 89 50 4E 47
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return "image/png";
        }

        // GIF: 47 49 46 38 ("GIF8")
        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38)
        {
            return "image/gif";
        }

        // WebP: RIFF…WEBP (bytes 0-3 = "RIFF", bytes 8-11 = "WEBP")
        if (data.Length >= 12 &&
            data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
            data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            return "image/webp";
        }

        return "image/jpeg";
    }

    // -------------------------------------------------------------------------
    // Factory helpers
    // -------------------------------------------------------------------------

    private static ExtractedClaim Claim(string key, string value) =>
        // File-embedded metadata is trustworthy but NOT authoritative. Confidence
        // 0.95 ensures Wikidata display-language titles (0.98) can override when
        // the file is in a foreign language. 1.0 is reserved for user locks only.
        ProcessorClaimFactory.Create(key, value, 0.95, trimValue: true);

    /// <summary>
    /// Strips URI prefixes (urn:isbn:, isbn:) and formatting (dashes, spaces) from an ISBN string.
    /// Returns a bare 10 or 13 digit ISBN, or null if the result is not a valid ISBN.
    /// </summary>
    private static string? StripIsbnPrefix(string raw)
    {
        return MediaEngine.Domain.Services.IsbnValidation.NormalizeValid(raw);
    }

    private static ProcessorResult Corrupt(string filePath, string reason) =>
        ProcessorResultFactory.Corrupt(filePath, MediaType.Books, reason);
}
