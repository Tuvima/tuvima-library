using MediaEngine.Ingestion.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion;

/// <summary>
/// Attempts generic TagLibSharp fields for advertised video containers and
/// MP4-specific TV atoms. Rich Matroska element/attachment writing is not
/// available through this adapter.
///
/// Safety: backup-before-modify pattern, implemented once by <see cref="BackedUpMetadataTagger"/>.
/// </summary>
public sealed class VideoMetadataTagger : BackedUpMetadataTagger, IMetadataTagger
{
    /// <summary>
    /// Bumped manually whenever this tagger gains a new write or changes the
    /// way an existing field is written. Combined with the per-media-type
    /// JSON slice from <c>writeback-fields.json</c> to compute the writeback
    /// hash that the auto re-tag sweep uses to detect stale files.
    /// </summary>
    public const int Version = 3;

    // Deliberately narrower than IMediaTypeExtensionCatalog: this tagger only writes
    // formats TagLib supports. The catalog's Movies/TV extension set also includes
    // .m4v, .wmv, .ts, .mpeg, .mpg, and .m2ts — MPEG transport/program-stream
    // containers (.ts/.mpeg/.mpg/.m2ts) have no TagLibSharp-recognized tag format,
    // so TagLib.File.Create would fail for them. Widen this set only after
    // confirming TagLibSharp can open and save each additional extension.
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".webm", ".mov",
    };

    /// <summary>
    /// Identifier claim keys written as iTunes reverse-DNS atoms
    /// (<c>----:com.tuvima:{key}</c>). Embedding these in the file lets
    /// re-ingestion short-circuit the matching cascade.
    /// </summary>
    private static readonly string[] CustomIdKeys =
    [
        "imdb_id", "tmdb_id", "tvdb_id", "apple_itunes_id",
        "wikidata_qid", "show_wikidata_qid",
    ];

    private static readonly HashSet<string> GenericKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "director", "author", "genre", "description", "year",
    };

    private static readonly HashSet<string> Mp4Keys = new(GenericKeys, StringComparer.OrdinalIgnoreCase)
    {
        "show_name", "episode_title", "season_number", "episode_number", "network",
        "imdb_id", "tmdb_id", "tvdb_id", "apple_itunes_id", "wikidata_qid", "show_wikidata_qid",
    };

    // Proven through a freshly reopened AppleTag on a disposable MP4 video.
    // TV atoms and reverse-DNS IDs remain attempted/unverified in this tranche.
    private static readonly HashSet<string> VerifiedMp4Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "genre", "description", "year",
    };

    private static void SetAppleText(TagLib.Mpeg4.AppleTag appleTag, string fourCc, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var box = TagLib.ByteVector.FromString(fourCc, TagLib.StringType.Latin1);
        appleTag.SetText(box, value);
    }

    private readonly ILogger<VideoMetadataTagger> _logger;

    public VideoMetadataTagger(ILogger<VideoMetadataTagger> logger)
        : base(logger, "VideoTagger")
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        return SupportedExtensions.Contains(Path.GetExtension(filePath));
    }

    /// <inheritdoc/>
    public MetadataTaggerCapabilities GetCapabilities(string filePath)
    {
        if (!CanHandle(filePath))
            throw new NotSupportedException($"VideoTagger cannot handle {Path.GetExtension(filePath)}.");
        var isMp4 = Path.GetExtension(filePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
        return new MetadataTaggerCapabilities(Path.GetExtension(filePath), isMp4 ? Mp4Keys : GenericKeys,
            canWriteArtwork: isMp4, Version,
            unsignedFields: isMp4 ? ["year", "season_number", "episode_number"] : ["year"]);
    }

    /// <inheritdoc/>
    public Task WriteTagsAsync(
        string filePath,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Video metadata write-back source is unavailable.", filePath);
        }

        GetCapabilities(filePath).ValidateTags(tags);

        var allowedKeys = Path.GetExtension(filePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            ? Mp4Keys : GenericKeys;
        var unsupportedKeys = tags.Keys.Where(key => !allowedKeys.Contains(key)).ToArray();
        if (unsupportedKeys.Length > 0)
        {
            throw new NotSupportedException(
                $"This video adapter cannot write {string.Join(", ", unsupportedKeys)} to {Path.GetExtension(filePath)}.");
        }

        // Validate TagLib support before copying a potentially very large video.
        // Unsupported or malformed containers fail before the physical write.
        using (var probe = CreateTagFileOrSkip(filePath))
        {
            if (probe is null)
            {
                throw new InvalidDataException("Video metadata could not be parsed safely; no tags were written.");
            }
            if (tags.Keys.Any(Mp4OnlyKeyRequested) &&
                probe.GetTag(TagLib.TagTypes.Apple, true) is not TagLib.Mpeg4.AppleTag)
                throw new NotSupportedException("This video has no Apple tag for MP4-specific metadata.");
        }

        WithBackup(
            filePath,
            () =>
        {
            using (var file = CreateTagFileOrSkip(filePath))
            {
            if (file is null)
            {
                throw new InvalidDataException("Video metadata could not be parsed safely; no tags were written.");
            }

            if (tags.TryGetValue("title", out var title))
            {
                file.Tag.Title = title;
            }

            if (tags.TryGetValue("director", out var director))
            {
                file.Tag.Performers = [director];
            }

            if (tags.TryGetValue("author", out var author) && file.Tag.Performers.Length == 0)
            {
                file.Tag.Performers = [author];
            }

            if (tags.TryGetValue("genre", out var genre))
            {
                file.Tag.Genres = [genre];
            }

            if (tags.TryGetValue("description", out var desc))
            {
                file.Tag.Comment = desc;
            }

            if (tags.TryGetValue("year", out var yearStr) && uint.TryParse(yearStr, out var year))
            {
                file.Tag.Year = year;
            }

            // MP4-specific TV atoms and custom identifiers via the iTunes AppleTag.
            // Matroska files only get the standard Tag fields above; rich custom
            // tagging on MKV is deferred until we add a SimpleTag writer.
            var appleTag = file.GetTag(TagLib.TagTypes.Apple, true) as TagLib.Mpeg4.AppleTag;
            if (appleTag is not null)
            {
                if (tags.TryGetValue("show_name", out var showName))
                {
                    SetAppleText(appleTag, "tvsh", showName);
                }

                if (tags.TryGetValue("episode_title", out var episodeTitle))
                {
                    SetAppleText(appleTag, "tven", episodeTitle);
                }

                if (tags.TryGetValue("season_number", out var seasonText) &&
                    uint.TryParse(seasonText, out var seasonNumber))
                {
                    appleTag.SetData(TagLib.ByteVector.FromString("tvsn", TagLib.StringType.Latin1),
                        TagLib.ByteVector.FromUInt(seasonNumber),
                        (uint)TagLib.Mpeg4.AppleDataBox.FlagType.ContainsData);
                }

                if (tags.TryGetValue("episode_number", out var episodeText) &&
                    uint.TryParse(episodeText, out var episodeNumber))
                {
                    appleTag.SetData(TagLib.ByteVector.FromString("tves", TagLib.StringType.Latin1),
                        TagLib.ByteVector.FromUInt(episodeNumber),
                        (uint)TagLib.Mpeg4.AppleDataBox.FlagType.ContainsData);
                }

                if (tags.TryGetValue("network", out var network))
                {
                    SetAppleText(appleTag, "tvnn", network);
                }

                // Custom identifier atoms (reverse-DNS) — round-trippable on re-ingest.
                foreach (var key in CustomIdKeys)
                {
                    if (tags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                    {
                        appleTag.SetDashBox("com.tuvima", key, value);
                    }
                }
            }

            try
            {
                file.Save();
            }
            catch (ArgumentException argEx) when (IsNanDurationMetadata(argEx))
            {
                throw new InvalidDataException("Video metadata save failed because the file contains an invalid duration; no write was verified.", argEx);
            }
            }

            if (Path.GetExtension(filePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                && tags.Keys.All(VerifiedMp4Keys.Contains))
            {
                var readback = VerifyTagsAsync(filePath, tags, ct).GetAwaiter().GetResult();
                if (!readback.IsVerified)
                    throw new InvalidDataException(readback.Reason ?? "MP4 metadata read-back failed.");
            }

            var backupPath = filePath + BackupSuffix;
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            _logger.LogInformation("VideoTagger: wrote {Count} tags to {Path}",
                tags.Count, filePath);
        },
            onFailure: ex => _logger.LogError(ex, "VideoTagger: failed to write tags to {Path} — restoring backup", filePath));

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task WriteCoverArtAsync(
        string filePath,
        byte[] imageData,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Video artwork write-back source is unavailable.", filePath);
        }

        if (!GetCapabilities(filePath).CanWriteArtwork)
            throw new NotSupportedException($"VideoTagger cannot embed cover art in {Path.GetExtension(filePath)}.");

        if (imageData.Length == 0)
        {
            throw new ArgumentException("Artwork bytes cannot be empty.", nameof(imageData));
        }

        var mimeType = imageData.Length >= 3 && imageData[0] == 0xFF && imageData[1] == 0xD8 && imageData[2] == 0xFF
            ? "image/jpeg"
            : imageData.Length >= 8 && imageData.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                ? "image/png"
                : throw new InvalidDataException("Video artwork must be a verified JPEG or PNG image.");

        WithBackup(filePath, () =>
        {
            using var file = CreateTagFileOrSkip(filePath)
                ?? throw new InvalidDataException("Video metadata could not be parsed safely; artwork was not written.");
            file.Tag.Pictures =
            [
                new TagLib.Picture(new TagLib.ByteVector(imageData))
                {
                    Type        = TagLib.PictureType.FrontCover,
                    MimeType    = mimeType,
                    Description = "Cover",
                },
            ];
            file.Save();

            var backupPath = filePath + BackupSuffix;
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            _logger.LogInformation("VideoTagger: wrote cover art ({Size} bytes) to {Path}",
                imageData.Length, filePath);
        }, onFailure: ex => _logger.LogError(ex, "VideoTagger: failed to write cover art to {Path} — restoring backup", filePath));

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<MetadataTagReadbackResult> VerifyTagsAsync(
        string filePath, IReadOnlyDictionary<string, string> tags, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Path.GetExtension(filePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || tags.Keys.Any(key => !VerifiedMp4Keys.Contains(key)))
            return Task.FromResult(MetadataTagReadbackResult.Unverified(
                "One or more requested video fields has no proven MP4 read-back."));

        try
        {
            using var file = TagLib.File.Create(filePath);
            if (file.GetTag(TagLib.TagTypes.Apple, false) is not TagLib.Mpeg4.AppleTag apple)
                return Task.FromResult(MetadataTagReadbackResult.Unverified("Apple MP4 metadata is absent."));
            var mismatches = new List<string>();
            foreach (var (key, expected) in tags)
            {
                ct.ThrowIfCancellationRequested();
                var actual = key switch
                {
                    "title" => apple.Title,
                    "genre" => apple.Genres?.FirstOrDefault(),
                    "description" => apple.Comment,
                    "year" => apple.Year.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => null,
                };
                var equal = key == "year"
                    ? uint.TryParse(expected, out var number)
                      && uint.TryParse(actual, out var savedNumber) && number == savedNumber
                    : string.Equals(NormalizeValue(expected), NormalizeValue(actual), StringComparison.Ordinal);
                if (!equal) mismatches.Add(key);
            }
            return Task.FromResult(mismatches.Count == 0
                ? MetadataTagReadbackResult.Verified()
                : MetadataTagReadbackResult.Unverified(
                    $"MP4 read-back did not match: {string.Join(", ", mismatches)}."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(MetadataTagReadbackResult.Unverified(
                $"MP4 read-back failed: {ex.GetType().Name}."));
        }
    }

    private static string? NormalizeValue(string? value) =>
        value?.Trim().Normalize(System.Text.NormalizationForm.FormC);

    private TagLib.File? CreateTagFileOrSkip(string filePath)
    {
        try
        {
            return TagLib.File.Create(filePath);
        }
        catch (ArgumentException argEx) when (IsNanDurationMetadata(argEx))
        {
            _logger.LogWarning("VideoTagger: skipping {Path} — file contains NaN duration metadata", filePath);
            return null;
        }
    }

    private static bool IsNanDurationMetadata(ArgumentException ex)
        => ex.Message.Contains("Not-a-Number", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("NaN", StringComparison.OrdinalIgnoreCase);

    private static bool Mp4OnlyKeyRequested(string key) => Mp4Keys.Contains(key) && !GenericKeys.Contains(key);
}
