using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using MediaEngine.Ingestion.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion;

/// <summary>
/// Writes metadata back into EPUB files by patching the OPF package document
/// embedded inside the ZIP container.
///
/// ──────────────────────────────────────────────────────────────────
/// Supported tags (spec: Phase 7 – Metadata Write-Back § EPUB)
/// ──────────────────────────────────────────────────────────────────
///  Tag keys consumed from the <c>tags</c> dictionary:
///   "title"     → dc:title
///   "author"    → dc:creator
///   "publisher" → dc:publisher
///   "year"      → dc:date (4-digit year)
///   "tuvima_id" → meta name="tuvima:id" (custom property for Collection linkage)
///
///  All other keys are written as OPF <meta name="…" content="…"/> elements.
///
/// ──────────────────────────────────────────────────────────────────
/// Safety: backup-before-modify
/// ──────────────────────────────────────────────────────────────────
///  1. Copy the original file to <c>&lt;path&gt;.tuvima.bak</c>.
///  2. Patch the ZIP entry in-place by rewriting the archive.
///  3. On any exception, restore from backup and rethrow.
///  4. Delete the backup on success.
///  Implemented once, shared with every other tagger, by
///  <see cref="BackedUpMetadataTagger"/>.
///  Spec: "If a metadata write-back operation fails, the system MUST attempt
///         to restore the file from a temporary backup."
///
/// Spec: Phase 7 – Extension Points § Metadata Taggers § EpubMetadataTagger.
/// </summary>
public sealed class EpubMetadataTagger : BackedUpMetadataTagger, IMetadataTagger
{
    /// <summary>
    /// Bumped manually whenever this tagger gains a new write or changes the
    /// way an existing field is written. Combined with the per-media-type
    /// JSON slice from <c>writeback-fields.json</c> to compute the writeback
    /// hash that the auto re-tag sweep uses to detect stale files.
    /// </summary>
    public const int Version = 3;

    private static readonly XNamespace DcNs = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace OpfNs = "http://www.idpf.org/2007/opf";

    // OPF MIME type as stored in EPUB container.xml.
    private const string OpfMediaType = "application/oebps-package+xml";

    private static readonly HashSet<string> VerifiedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "author", "publisher", "year",
    };

    private readonly ILogger<EpubMetadataTagger> _logger;

    public EpubMetadataTagger(ILogger<EpubMetadataTagger> logger)
        : base(logger, "EpubTagger")
    {
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // IMetadataTagger
    // -------------------------------------------------------------------------

    // Deliberately narrower than IMediaTypeExtensionCatalog: this tagger patches the
    // EPUB-specific OPF/ZIP structure directly (see PatchOpfAsync/PatchCoverAsync
    // below). The catalog's Books extension set also includes .pdf, which is a
    // completely different binary/metadata format this tagger cannot write.
    /// <inheritdoc/>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string ext = Path.GetExtension(filePath);
        return ext.Equals(".epub", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    public MetadataTaggerCapabilities GetCapabilities(string filePath)
    {
        if (!CanHandle(filePath))
            throw new NotSupportedException($"EpubTagger cannot handle {Path.GetExtension(filePath)}.");
        return new MetadataTaggerCapabilities(".epub", ["title", "author", "publisher", "year"],
            canWriteArtwork: true, Version, acceptsCustomOpfFields: true);
    }

    /// <inheritdoc/>
    public async Task WriteTagsAsync(
        string filePath,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(tags);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("EPUB metadata write-back source is unavailable.", filePath);
        }

        GetCapabilities(filePath).ValidateTags(tags);

        await WithBackupAsync(
            filePath,
            async () =>
        {
            // Patch OPF inside the ZIP.
            await PatchOpfAsync(filePath, tags, ct).ConfigureAwait(false);

            if (tags.Keys.All(VerifiedKeys.Contains))
            {
                var readback = await VerifyTagsAsync(filePath, tags, ct).ConfigureAwait(false);
                if (!readback.IsVerified)
                    throw new InvalidDataException(readback.Reason ?? "EPUB metadata read-back failed.");
            }

            // Remove backup on success.
            var backup = filePath + BackupSuffix;
            File.Delete(backup);

            _logger.LogInformation("Wrote {Count} tag(s) to EPUB: {Path}", tags.Count, filePath);
        },
            onFailure: ex => _logger.LogError(ex, "WriteTagsAsync failed for {Path}; restoring backup.", filePath));
    }

    /// <inheritdoc/>
    public async Task<MetadataTagReadbackResult> VerifyTagsAsync(
        string filePath,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (tags.Keys.Any(key => !VerifiedKeys.Contains(key)))
            return MetadataTagReadbackResult.Unverified("One or more requested EPUB fields has no proven read-back.");

        try
        {
            using var zip = ZipFile.OpenRead(filePath);
            var opfEntry = zip.GetEntry(FindOpfEntryName(zip));
            if (opfEntry is null)
                return MetadataTagReadbackResult.Unverified("EPUB package document is absent.");

            XDocument opf;
            await using (var stream = opfEntry.Open())
                opf = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);

            var metadata = opf.Descendants(OpfNs + "metadata").FirstOrDefault()
                ?? opf.Descendants("metadata").FirstOrDefault();
            if (metadata is null)
                return MetadataTagReadbackResult.Unverified("EPUB metadata element is absent.");

            var mismatches = new List<string>();
            foreach (var (key, expected) in tags)
            {
                ct.ThrowIfCancellationRequested();
                var name = key.ToLowerInvariant() switch
                {
                    "title" => DcNs + "title",
                    "author" => DcNs + "creator",
                    "publisher" => DcNs + "publisher",
                    "year" => DcNs + "date",
                    _ => throw new InvalidOperationException("Unverified EPUB field entered the reader."),
                };
                var actual = metadata.Element(name)?.Value;
                if (!string.Equals(Normalize(expected), Normalize(actual), StringComparison.Ordinal))
                    mismatches.Add(key);
            }

            return mismatches.Count == 0
                ? MetadataTagReadbackResult.Verified()
                : MetadataTagReadbackResult.Unverified(
                    $"EPUB metadata read-back did not match: {string.Join(", ", mismatches)}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return MetadataTagReadbackResult.Unverified($"EPUB metadata read-back failed: {ex.GetType().Name}.");
        }
    }

    private static string? Normalize(string? value) =>
        value?.Trim().Normalize(NormalizationForm.FormC);

    /// <inheritdoc/>
    public async Task WriteCoverArtAsync(
        string filePath,
        byte[] imageData,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(imageData);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("EPUB artwork write-back source is unavailable.", filePath);
        }

        var mime = SniffImageMime(imageData);

        await WithBackupAsync(
            filePath,
            async () =>
        {
            await PatchCoverAsync(filePath, imageData, mime, ct).ConfigureAwait(false);

            if (!await VerifyCoverAsync(filePath, imageData, mime, ct).ConfigureAwait(false))
                throw new InvalidDataException("EPUB cover manifest or image did not match after writing.");

            var backup = filePath + BackupSuffix;
            File.Delete(backup);

            _logger.LogInformation("Wrote cover art ({Bytes} bytes) to EPUB: {Path}",
                imageData.Length, filePath);
        },
            onFailure: ex => _logger.LogError(ex, "WriteCoverArtAsync failed for {Path}; restoring backup.", filePath));
    }

    // -------------------------------------------------------------------------
    // OPF patching
    // -------------------------------------------------------------------------

    private static async Task PatchOpfAsync(
        string epubPath,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct)
    {
        // We work on a temp copy to avoid partial-write corruption.
        string temp = epubPath + ".tmp";

        try
        {
            // Copy all entries to the temp file, replacing the OPF entry.
            using (var srcZip = ZipFile.OpenRead(epubPath))
            using (var destZip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                string opfEntryName = FindOpfEntryName(srcZip);

                foreach (var entry in srcZip.Entries)
                {
                    ct.ThrowIfCancellationRequested();

                    if (entry.FullName.Equals(opfEntryName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Read, patch, re-write OPF XML.
                        XDocument opf;
                        await using (var stream = entry.Open())
                        {
                            opf = await XDocument.LoadAsync(stream, LoadOptions.None, ct)
                                                  .ConfigureAwait(false);
                        }

                        ApplyTagsToOpf(opf, tags);

                        var newEntry = destZip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                        newEntry.LastWriteTime = DateTimeOffset.UtcNow;
                        await using var writer = newEntry.Open();
                        await writer.WriteAsync(
                            Encoding.UTF8.GetBytes(opf.ToString()), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // Copy entry verbatim.
                        var newEntry = destZip.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                        newEntry.LastWriteTime = entry.LastWriteTime;
                        await using var src = entry.Open();
                        await using var dest = newEntry.Open();
                        await src.CopyToAsync(dest, ct).ConfigureAwait(false);
                    }
                }
            }

            // Atomically replace the original with the patched temp.
            File.Move(temp, epubPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static async Task PatchCoverAsync(
        string epubPath,
        byte[] imageData,
        string mime,
        CancellationToken ct)
    {
        string temp = epubPath + ".tmp";

        try
        {
            using (var srcZip = ZipFile.OpenRead(epubPath))
            using (var destZip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                string opfEntryName = FindOpfEntryName(srcZip);
                string coverEntryName = FindCoverEntryName(srcZip, opfEntryName) ?? "OEBPS/cover.jpg";
                string ext = mime switch
                {
                    "image/png" => "png",
                    "image/gif" => "gif",
                    _ => "jpg",
                };

                // Normalise the cover entry name to use the correct extension.
                string finalCoverName = Path.ChangeExtension(coverEntryName, ext);

                XDocument? opf = null;

                foreach (var entry in srcZip.Entries)
                {
                    ct.ThrowIfCancellationRequested();

                    if (entry.FullName.Equals(opfEntryName, StringComparison.OrdinalIgnoreCase))
                    {
                        await using (var stream = entry.Open())
                        {
                            opf = await XDocument.LoadAsync(stream, LoadOptions.None, ct)
                                                  .ConfigureAwait(false);
                        }

                        // Update cover item href in OPF manifest.
                        if (opf is not null)
                        {
                            UpdateCoverManifestEntry(opf, GetOpfRelativeHref(opfEntryName, finalCoverName), mime);
                        }

                        var newEntry = destZip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                        newEntry.LastWriteTime = DateTimeOffset.UtcNow;
                        await using var writer = newEntry.Open();
                        await writer.WriteAsync(
                            Encoding.UTF8.GetBytes(opf!.ToString()), ct).ConfigureAwait(false);
                    }
                    else if (entry.FullName.Equals(coverEntryName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Replace the old cover image.
                        var newEntry = destZip.CreateEntry(finalCoverName, CompressionLevel.NoCompression);
                        newEntry.LastWriteTime = DateTimeOffset.UtcNow;
                        await using var writer = newEntry.Open();
                        await writer.WriteAsync(imageData, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var newEntry = destZip.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                        newEntry.LastWriteTime = entry.LastWriteTime;
                        await using var src = entry.Open();
                        await using var dest = newEntry.Open();
                        await src.CopyToAsync(dest, ct).ConfigureAwait(false);
                    }
                }

                // If no existing cover entry was found, add a new one.
                if (!srcZip.Entries.Any(e =>
                        e.FullName.Equals(coverEntryName, StringComparison.OrdinalIgnoreCase)))
                {
                    var coverEntry = destZip.CreateEntry(finalCoverName, CompressionLevel.NoCompression);
                    coverEntry.LastWriteTime = DateTimeOffset.UtcNow;
                    await using var writer = coverEntry.Open();
                    await writer.WriteAsync(imageData, ct).ConfigureAwait(false);
                }
            }

            File.Move(temp, epubPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static async Task<bool> VerifyCoverAsync(
        string epubPath, byte[] expected, string mime, CancellationToken ct)
    {
        try
        {
            using var zip = ZipFile.OpenRead(epubPath);
            var opfName = FindOpfEntryName(zip);
            var opfEntry = zip.GetEntry(opfName);
            if (opfEntry is null) return false;

            XDocument opf;
            await using (var stream = opfEntry.Open())
                opf = await XDocument.LoadAsync(stream, LoadOptions.None, ct).ConfigureAwait(false);

            var manifest = opf.Descendants(OpfNs + "manifest").FirstOrDefault()
                ?? opf.Descendants("manifest").FirstOrDefault();
            var cover = manifest?.Elements().FirstOrDefault(e =>
                (e.Attribute("id")?.Value ?? string.Empty).Contains("cover", StringComparison.OrdinalIgnoreCase));
            if (cover is null || !string.Equals(cover.Attribute("media-type")?.Value, mime, StringComparison.Ordinal))
                return false;
            var href = cover.Attribute("href")?.Value;
            if (string.IsNullOrWhiteSpace(href)) return false;
            var entryName = ResolveOpfHref(opfName, href);
            var imageEntry = zip.GetEntry(entryName);
            if (imageEntry is null) return false;
            await using var imageStream = imageEntry.Open();
            using var buffer = new MemoryStream();
            await imageStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
            return buffer.ToArray().AsSpan().SequenceEqual(expected);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // OPF XML helpers
    // -------------------------------------------------------------------------

    private static void ApplyTagsToOpf(XDocument opf, IReadOnlyDictionary<string, string> tags)
    {
        var metadata = opf.Descendants(OpfNs + "metadata").FirstOrDefault()
                    ?? opf.Descendants("metadata").FirstOrDefault();

        if (metadata is null)
        {
            throw new InvalidDataException("EPUB OPF has no metadata element; no fields were written.");
        }

        foreach (var (key, value) in tags)
        {
            switch (key.ToLowerInvariant())
            {
                case "title":
                    SetOrCreate(metadata, DcNs + "title", value);
                    break;
                case "author":
                    SetOrCreate(metadata, DcNs + "creator", value);
                    break;
                case "publisher":
                    SetOrCreate(metadata, DcNs + "publisher", value);
                    break;
                case "year":
                    SetOrCreate(metadata, DcNs + "date", value);
                    break;
                default:
                    // Write as generic OPF <meta name="…" content="…"/>
                    var existing = metadata
                        .Elements(OpfNs + "meta")
                        .Concat(metadata.Elements("meta"))
                        .FirstOrDefault(e =>
                            (e.Attribute("name")?.Value ?? string.Empty)
                                .Equals($"tuvima:{key}", StringComparison.OrdinalIgnoreCase));

                    if (existing is not null)
                    {
                        existing.SetAttributeValue("content", value);
                    }
                    else
                    {
                        metadata.Add(new XElement(OpfNs + "meta",
                            new XAttribute("name", $"tuvima:{key}"),
                            new XAttribute("content", value)));
                    }

                    break;
            }
        }
    }

    private static void SetOrCreate(XElement parent, XName elementName, string value)
    {
        var el = parent.Element(elementName);
        if (el is not null)
        {
            el.SetValue(value);
        }
        else
        {
            parent.Add(new XElement(elementName, value));
        }
    }

    private static void UpdateCoverManifestEntry(XDocument opf, string newHref, string mime)
    {
        var manifest = opf.Descendants(OpfNs + "manifest").FirstOrDefault()
                    ?? opf.Descendants("manifest").FirstOrDefault();

        if (manifest is null)
        {
            return;
        }

        var coverItem = manifest.Elements()
            .FirstOrDefault(e =>
                (e.Attribute("id")?.Value ?? string.Empty)
                    .Contains("cover", StringComparison.OrdinalIgnoreCase));

        if (coverItem is not null)
        {
            coverItem.SetAttributeValue("href", newHref);
            coverItem.SetAttributeValue("media-type", mime);
        }
    }

    private static string GetOpfRelativeHref(string opfEntryName, string coverEntryName)
    {
        var opfDir = Path.GetDirectoryName(opfEntryName.Replace('\\', '/')) ?? string.Empty;
        return Path.GetRelativePath(string.IsNullOrEmpty(opfDir) ? "." : opfDir, coverEntryName)
            .Replace('\\', '/');
    }

    private static string ResolveOpfHref(string opfEntryName, string href)
    {
        var opfDir = Path.GetDirectoryName(opfEntryName.Replace('\\', '/')) ?? string.Empty;
        var root = Path.Combine(Path.GetTempPath(), "tuvima-epub-entry-root");
        var combined = Path.GetFullPath(Path.Combine(root, opfDir, href.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EPUB cover href leaves the archive root.");
        return Path.GetRelativePath(root, combined).Replace('\\', '/');
    }

    // -------------------------------------------------------------------------
    // EPUB structure helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Locates the OPF entry name by reading META-INF/container.xml.
    /// Falls back to the first .opf entry.
    /// </summary>
    private static string FindOpfEntryName(ZipArchive zip)
    {
        var containerEntry = zip.GetEntry("META-INF/container.xml");
        if (containerEntry is not null)
        {
            using var stream = containerEntry.Open();
            var doc = XDocument.Load(stream);
            var rootfile = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "rootfile");
            if (rootfile?.Attribute("full-path")?.Value is { } path)
            {
                return path;
            }
        }

        // Fallback: first .opf entry.
        return zip.Entries
            .FirstOrDefault(e => e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))
            ?.FullName ?? "OEBPS/content.opf";
    }

    /// <summary>
    /// Tries to find the cover image entry name from the OPF manifest.
    /// Returns <see langword="null"/> if not determinable.
    /// </summary>
    private static string? FindCoverEntryName(ZipArchive zip, string opfEntryName)
    {
        var opfEntry = zip.GetEntry(opfEntryName);
        if (opfEntry is null)
        {
            return null;
        }

        using var stream = opfEntry.Open();
        var opf = XDocument.Load(stream);

        var manifest = opf.Descendants(OpfNs + "manifest").FirstOrDefault()
                    ?? opf.Descendants("manifest").FirstOrDefault();

        var coverItem = manifest?.Elements()
            .FirstOrDefault(e =>
                (e.Attribute("id")?.Value ?? string.Empty)
                    .Contains("cover", StringComparison.OrdinalIgnoreCase) ||
                (e.Attribute("media-type")?.Value ?? string.Empty)
                    .StartsWith("image/", StringComparison.OrdinalIgnoreCase));

        if (coverItem?.Attribute("href")?.Value is not { } href)
        {
            return null;
        }

        // OPF href is relative to the OPF file's directory.
        string opfDir = Path.GetDirectoryName(opfEntryName.Replace('\\', '/')) ?? string.Empty;
        return string.IsNullOrEmpty(opfDir)
            ? href
            : $"{opfDir}/{href}".TrimStart('/');
    }

    // -------------------------------------------------------------------------
    // Image MIME sniffing
    // -------------------------------------------------------------------------

    private static string SniffImageMime(byte[] data)
    {
        if (data.Length >= 4 &&
            data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return "image/png";
        }

        if (data.Length >= 3 &&
            data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (data.Length >= 4 &&
            data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38)
        {
            return "image/gif";
        }

        throw new NotSupportedException("EPUB cover must be JPEG, PNG, or GIF image data.");
    }
}
