using System.IO.Compression;
using System.Xml.Linq;
using MediaEngine.Ingestion.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion;

/// <summary>
/// Writes metadata back into CBZ comic archives by creating or updating a
/// <c>ComicInfo.xml</c> file inside the ZIP container.
///
/// CBR (RAR) archives are read-only — this tagger does not support them.
/// Uses <see cref="System.IO.Compression"/> (BCL) — no new dependency.
///
/// Safety: backup-before-modify pattern, implemented once by <see cref="BackedUpMetadataTagger"/>.
/// </summary>
public sealed class ComicMetadataTagger : BackedUpMetadataTagger, IMetadataTagger
{
    /// <summary>
    /// Bumped manually whenever this tagger gains a new write or changes the
    /// way an existing field is written. Combined with the per-media-type
    /// JSON slice from <c>writeback-fields.json</c> to compute the writeback
    /// hash that the auto re-tag sweep uses to detect stale files.
    /// </summary>
    public const int Version = 3;

    private const string ComicInfoEntry = "ComicInfo.xml";

    /// <summary>
    /// Identifier claim keys written as custom <c>&lt;Tuvima_{key}&gt;</c>
    /// elements alongside the standard ComicInfo.xml fields.
    /// </summary>
    private static readonly string[] CustomIdKeys =
    [
        "wikidata_qid",
    ];

    private static readonly string[] WritableKeys =
    [
        "title", "author", "genre", "description", "series", "series_position",
        "year", "publisher", "illustrator", "page_count", "wikidata_qid",
    ];

    private static readonly IReadOnlyDictionary<string, string> VerifiedElements =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = "Title",
            ["author"] = "Writer",
            ["genre"] = "Genre",
            ["description"] = "Summary",
            ["series"] = "Series",
            ["series_position"] = "Number",
            ["year"] = "Year",
            ["publisher"] = "Publisher",
            ["illustrator"] = "Penciller",
            ["page_count"] = "PageCount",
        };

    private readonly ILogger<ComicMetadataTagger> _logger;

    public ComicMetadataTagger(ILogger<ComicMetadataTagger> logger)
        : base(logger, "ComicTagger")
    {
        _logger = logger;
    }

    // Deliberately narrower than IMediaTypeExtensionCatalog: this tagger writes
    // ComicInfo.xml via System.IO.Compression.ZipFile, so it only works on true ZIP
    // containers. The catalog's Comics extension set also includes .cbr (RAR) and
    // .cb7 (7-Zip), neither of which ZipFile can open or update.
    /// <inheritdoc/>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }
        // Only CBZ (ZIP-based). CBR (RAR) is not supported for write-back.
        return Path.GetExtension(filePath).Equals(".cbz", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public MetadataTaggerCapabilities GetCapabilities(string filePath)
    {
        if (!CanHandle(filePath))
        {
            throw new NotSupportedException($"ComicTagger cannot handle {Path.GetExtension(filePath)}.");
        }
        return new MetadataTaggerCapabilities(".cbz", WritableKeys, canWriteArtwork: false, Version,
            integerFields: ["year", "page_count"]);
    }

    /// <inheritdoc/>
    public async Task WriteTagsAsync(
        string filePath,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Comic metadata write-back source is unavailable.", filePath);
        }

        GetCapabilities(filePath).ValidateTags(tags);

        await WithBackupAsync(
            filePath,
            async () =>
        {
            using (var zip = ZipFile.Open(filePath, ZipArchiveMode.Update))
            {

                // Load or create ComicInfo.xml.
                var entry = zip.GetEntry(ComicInfoEntry);
                XDocument doc;
                if (entry is not null)
                {
                    using var stream = entry.Open();
                    doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
                }
                else
                {
                    doc = new XDocument(new XElement("ComicInfo"));
                }

                var root = doc.Root!;

                SetElement(root, "Title", tags, "title");
                SetElement(root, "Writer", tags, "author");
                SetElement(root, "Genre", tags, "genre");
                SetElement(root, "Summary", tags, "description");
                SetElement(root, "Series", tags, "series");
                SetElement(root, "Number", tags, "series_position");

                if (tags.TryGetValue("year", out var yearStr) && int.TryParse(yearStr, out _))
                {
                    SetElementDirect(root, "Year", yearStr);
                }

                if (tags.TryGetValue("publisher", out var pub))
                {
                    SetElementDirect(root, "Publisher", pub);
                }

                if (tags.TryGetValue("illustrator", out var illustrator))
                {
                    SetElementDirect(root, "Penciller", illustrator);
                }

                if (tags.TryGetValue("page_count", out var pages) && int.TryParse(pages, out _))
                {
                    SetElementDirect(root, "PageCount", pages);
                }

                // Custom identifier fields — written as <Tuvima_{Key}> elements so
                // re-ingestion can short-circuit the matching cascade.
                foreach (var key in CustomIdKeys)
                {
                    if (tags.TryGetValue(key, out var idValue) && !string.IsNullOrWhiteSpace(idValue))
                    {
                        SetElementDirect(root, "Tuvima_" + key, idValue);
                    }
                }

                // Remove existing entry and re-add with updated content.
                entry?.Delete();
                var newEntry = zip.CreateEntry(ComicInfoEntry, CompressionLevel.Optimal);
                using (var outStream = newEntry.Open())
                {
                    await doc.SaveAsync(outStream, SaveOptions.None, ct);
                }
            }

            if (tags.Keys.All(VerifiedElements.ContainsKey))
            {
                var readback = await VerifyTagsAsync(filePath, tags, ct);
                if (!readback.IsVerified)
                {
                    throw new InvalidDataException(readback.Reason ?? "CBZ metadata read-back failed.");
                }
            }

            // Backup cleanup — success.
            var backupPath = filePath + BackupSuffix;
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            _logger.LogInformation("ComicTagger: wrote ComicInfo.xml with {Count} tags to {Path}",
                tags.Count, filePath);
        },
            onFailure: ex => _logger.LogError(ex, "ComicTagger: failed to write tags to {Path} — restoring backup", filePath));
    }

    /// <inheritdoc/>
    public async Task<MetadataTagReadbackResult> VerifyTagsAsync(
        string filePath,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (tags.Keys.Any(key => !VerifiedElements.ContainsKey(key)))
        {
            return MetadataTagReadbackResult.Unverified("One or more requested CBZ fields has no proven read-back.");
        }

        try
        {
            using var zip = ZipFile.OpenRead(filePath);
            var entry = zip.GetEntry(ComicInfoEntry);
            if (entry is null)
            {
                return MetadataTagReadbackResult.Unverified("ComicInfo.xml is absent.");
            }

            XDocument doc;
            await using (var stream = entry.Open())
            {
                doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
            }
            if (doc.Root is null)
            {
                return MetadataTagReadbackResult.Unverified("ComicInfo.xml has no root.");
            }

            var mismatches = new List<string>();
            foreach (var (key, expected) in tags)
            {
                ct.ThrowIfCancellationRequested();
                var actual = doc.Root.Element(VerifiedElements[key])?.Value;
                if (!string.Equals(Normalize(expected), Normalize(actual), StringComparison.Ordinal))
                {
                    mismatches.Add(key);
                }
            }

            return mismatches.Count == 0
                ? MetadataTagReadbackResult.Verified()
                : MetadataTagReadbackResult.Unverified(
                    $"CBZ metadata read-back did not match: {string.Join(", ", mismatches)}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return MetadataTagReadbackResult.Unverified($"CBZ metadata read-back failed: {ex.GetType().Name}.");
        }
    }

    private static string? Normalize(string? value) =>
        value?.Trim().Normalize(System.Text.NormalizationForm.FormC);

    /// <inheritdoc/>
    public Task WriteCoverArtAsync(
        string filePath,
        byte[] imageData,
        CancellationToken ct = default)
    {
        // CBZ cover art is the first image file in the archive (by sort order).
        // Modifying the image order is destructive — skip cover art write-back for comics.
        throw new NotSupportedException("ComicTagger cannot write CBZ cover art without changing page order.");
    }

    private static void SetElement(XElement root, string elementName,
        IReadOnlyDictionary<string, string> tags, string tagKey)
    {
        if (tags.TryGetValue(tagKey, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            SetElementDirect(root, elementName, value);
        }
    }

    private static void SetElementDirect(XElement root, string elementName, string value)
    {
        var el = root.Element(elementName);
        if (el is not null)
        {
            el.Value = value;
        }
        else
        {
            root.Add(new XElement(elementName, value));
        }
    }
}
