using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Services;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Ingestion;

/// <summary>
/// Writes metadata back into audio files (MP3, M4B, M4A, FLAC, OGG, AAC, WAV, Opus,
/// WMA) using TagLibSharp. Handles ID3v2 (MP3), MP4 atoms (M4B/M4A), Vorbis comments
/// (FLAC/OGG/Opus), RIFF/ID3v2 (WAV), and ASF (WMA).
///
/// Safety: backup-before-modify pattern, implemented once by <see cref="BackedUpMetadataTagger"/>.
/// </summary>
public sealed class AudioMetadataTagger : BackedUpMetadataTagger, IMetadataTagger
{
    /// <summary>
    /// Bumped manually whenever this tagger gains a new write or changes the
    /// way an existing field is written. Combined with the per-media-type
    /// JSON slice from <c>writeback-fields.json</c> to compute the writeback
    /// hash that the auto re-tag sweep uses to detect stale files.
    /// </summary>
    public const int Version = 5;

    /// <summary>
    /// Union of <see cref="MediaType.Music"/> and <see cref="MediaType.Audiobooks"/>
    /// extensions from the config-backed <see cref="IMediaTypeExtensionCatalog"/> —
    /// this tagger writes both music and audiobook fields (album/artist and
    /// narrator/series respectively). Every format in the catalog's audio sets
    /// (including AAC and WAV, which the previous hardcoded list omitted) is
    /// already round-tripped through TagLibSharp elsewhere in ingestion (see
    /// <c>AudioProcessor</c>'s container detection), so widening to the catalog
    /// is safe.
    /// </summary>
    private readonly IReadOnlySet<string> _supportedExtensions;

    /// <summary>
    /// Identifier claim keys written as custom tag fields. For ID3v2 these become
    /// <c>TXXX:{KEY}</c> frames; for MP4 they become reverse-DNS
    /// <c>----:com.tuvima:{key}</c> atoms. Embedding these lets re-ingestion
    /// short-circuit the matching cascade.
    /// </summary>
    private static readonly string[] CustomIdKeys =
    [
        "isbn", "asin", "audible_id",
        "apple_books_id", "apple_music_id", "apple_music_collection_id",
        "apple_artist_id", "musicbrainz_id", "wikidata_qid",
    ];

    private static readonly string[] StandardKeys =
    [
        "title", "author", "artist", "album", "track_number", "narrator",
        "series", "series_position", "genre", "description", "year", "publisher",
    ];

    private static readonly HashSet<string> CustomTagExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".flac", ".ogg", ".opus",
    };

    // Proven against a reopened AppleTag on disposable M4A/M4B files. Other
    // writable fields remain attempted/unverified until their readers are tested.
    private static readonly HashSet<string> VerifiedAppleAudioKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "artist", "album", "track_number", "genre", "description", "year",
    };

    // FLAC Vorbis comments and Ogg Vorbis comments are verified through a
    // freshly reopened XiphComment. Opus has not been fixture-proven here.
    private static readonly HashSet<string> VerifiedXiphAudioKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "artist", "album", "track_number", "genre", "description", "year",
    };

    private static void WriteCustomId(TagLib.File file, string key, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3v2)
        {
            var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3v2, key.ToUpperInvariant(), true);
            frame.Text = [value];
            return;
        }

        if (file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag appleTag)
        {
            appleTag.SetDashBox("com.tuvima", key, value);
            return;
        }

        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
        {
            xiph.SetField("TUVIMA:" + key.ToUpperInvariant(), value);
        }
    }

    private readonly ILogger<AudioMetadataTagger> _logger;

    public AudioMetadataTagger(ILogger<AudioMetadataTagger> logger, IMediaTypeExtensionCatalog extensionCatalog)
        : base(logger, "AudioTagger")
    {
        _logger = logger;

        ArgumentNullException.ThrowIfNull(extensionCatalog);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        extensions.UnionWith(extensionCatalog.GetExtensionsFor(MediaType.Music));
        extensions.UnionWith(extensionCatalog.GetExtensionsFor(MediaType.Audiobooks));
        _supportedExtensions = extensions;
    }

    /// <inheritdoc/>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        return _supportedExtensions.Contains(Path.GetExtension(filePath));
    }

    /// <inheritdoc/>
    public MetadataTaggerCapabilities GetCapabilities(string filePath)
    {
        if (!CanHandle(filePath))
        {
            throw new NotSupportedException($"AudioTagger cannot handle {Path.GetExtension(filePath)}.");
        }
        var extension = Path.GetExtension(filePath);
        return new MetadataTaggerCapabilities(
            extension,
            CustomTagExtensions.Contains(extension) ? StandardKeys.Concat(CustomIdKeys) : StandardKeys,
            ArtworkEmbeddingSupport.CanEmbed(filePath), Version,
            unsignedFields: ["year", "track_number", "series_position"]);
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
            throw new FileNotFoundException("Audio metadata write-back source is unavailable.", filePath);
        }

        GetCapabilities(filePath).ValidateTags(tags);
        ValidateAliasedValues(tags);

        WithBackup(
            filePath,
            () =>
        {
            using (var file = TagLib.File.Create(filePath))
            {

            if (CustomIdKeys.Any(tags.ContainsKey)
                && file.GetTag(TagLib.TagTypes.Id3v2, false) is not TagLib.Id3v2.Tag
                && file.GetTag(TagLib.TagTypes.Apple, false) is not TagLib.Mpeg4.AppleTag
                && file.GetTag(TagLib.TagTypes.Xiph, false) is not TagLib.Ogg.XiphComment)
            {
                throw new NotSupportedException(
                    $"This audio container has no supported Tuvima identifier tag for {Path.GetExtension(filePath)}.");
            }

            if (tags.TryGetValue("title", out var title))
            {
                file.Tag.Title = title;
            }

            if (tags.TryGetValue("author", out var author))
            {
                file.Tag.Performers = [author];
            }

            if (tags.TryGetValue("artist", out var artist))
            {
                file.Tag.Performers = [artist];
            }

            if (tags.TryGetValue("album", out var albumName))
            {
                file.Tag.Album = albumName;
            }

            if (tags.TryGetValue("track_number", out var trackStr) && uint.TryParse(trackStr, out var trackNo))
            {
                file.Tag.Track = trackNo;
            }

            if (tags.TryGetValue("narrator", out var narrator))
            {
                // Write narrator to TXXX:NARRATOR — the same custom frame that
                // AudioProcessor reads as its primary narrator source.
                if (file.TagTypes.HasFlag(TagLib.TagTypes.Id3v2) &&
                    file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2)
                {
                    var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3v2, "NARRATOR", true);
                    frame.Text = [narrator];
                }
                else
                {
                    // Non-ID3 formats (M4A, FLAC, OGG): use Composers as fallback
                    // since AudioProcessor checks Composers for narrator on these formats.
                    file.Tag.Composers = [narrator];
                }
            }

            if (tags.TryGetValue("series", out var series))
            {
                file.Tag.Album = series;
            }

            if (tags.TryGetValue("series_position", out var pos) && uint.TryParse(pos, out var trackNum))
            {
                file.Tag.Track = trackNum;
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

            if (tags.TryGetValue("publisher", out var publisher))
            {
                // TagLib doesn't have a dedicated publisher property;
                // store in the first available custom field.
                file.Tag.Publisher = publisher;
            }

            // Custom identifier fields — round-trippable on re-ingest.
            foreach (var key in CustomIdKeys)
            {
                if (tags.TryGetValue(key, out var idValue))
                {
                    WriteCustomId(file, key, idValue);
                }
            }

            file.Save();
            }

            if (CanVerifyAllRequestedTags(filePath, tags))
            {
                var readback = VerifyTagsAsync(filePath, tags, ct).GetAwaiter().GetResult();
                if (!readback.IsVerified)
                {
                    throw new InvalidDataException(readback.Reason ?? "Audio metadata read-back failed.");
                }
            }

            // Backup cleanup — success.
            var backupPath = filePath + BackupSuffix;
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            _logger.LogInformation("AudioTagger: wrote {Count} tags to {Path}",
                tags.Count, filePath);
        },
            onFailure: ex => _logger.LogError(ex, "AudioTagger: failed to write tags to {Path} — restoring backup", filePath));

        return Task.CompletedTask;
    }

    private static void ValidateAliasedValues(IReadOnlyDictionary<string, string> tags)
    {
        CheckTextAlias("author", "artist");
        CheckTextAlias("album", "series");
        if (tags.TryGetValue("track_number", out var track)
            && tags.TryGetValue("series_position", out var position)
            && (!uint.TryParse(track, out var trackNumber)
                || !uint.TryParse(position, out var positionNumber)
                || trackNumber != positionNumber))
        {
            throw new NotSupportedException("track_number and series_position share one audio tag and must agree.");
        }

        void CheckTextAlias(string first, string second)
        {
            if (tags.TryGetValue(first, out var firstValue)
                && tags.TryGetValue(second, out var secondValue)
                && !string.Equals(NormalizeValue(firstValue), NormalizeValue(secondValue), StringComparison.Ordinal))
            {
                throw new NotSupportedException($"{first} and {second} share one audio tag and must agree.");
            }
        }
    }

    /// <inheritdoc/>
    public Task<MetadataTagReadbackResult> VerifyTagsAsync(
        string filePath,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(filePath);
        var mp3 = extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase);
        var appleAudio = extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".m4b", StringComparison.OrdinalIgnoreCase);
        var xiphAudio = extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase);
        if (!mp3 && !appleAudio && !xiphAudio)
        {
            return Task.FromResult(MetadataTagReadbackResult.Unverified(
                    "Metadata read-back is not proven for this audio format."));
        }
        if (appleAudio && tags.Keys.Any(key => !VerifiedAppleAudioKeys.Contains(key)))
        {
            return Task.FromResult(MetadataTagReadbackResult.Unverified(
                    "One or more requested Apple audio fields has no proven read-back."));
        }
        if (xiphAudio && tags.Keys.Any(key => !VerifiedXiphAudioKeys.Contains(key)))
        {
            return Task.FromResult(MetadataTagReadbackResult.Unverified(
                    "One or more requested Xiph audio fields has no proven read-back."));
        }

        try
        {
            // This is a fresh file handle after WriteTagsAsync has saved and
            // disposed its writer. Inspect the physical tag family directly.
            using var file = TagLib.File.Create(filePath);
            if (xiphAudio && !IsProvenXiphContainer(file, extension))
            {
                return Task.FromResult(MetadataTagReadbackResult.Unverified(
                        "This FLAC or OGG codec has no proven metadata read-back."));
            }
            var id3 = mp3 ? file.GetTag(TagLib.TagTypes.Id3v2, false) as TagLib.Id3v2.Tag : null;
            var apple = appleAudio ? file.GetTag(TagLib.TagTypes.Apple, false) as TagLib.Mpeg4.AppleTag : null;
            var xiph = xiphAudio ? file.GetTag(TagLib.TagTypes.Xiph, false) as TagLib.Ogg.XiphComment : null;
            if (mp3 && id3 is null)
            {
                return Task.FromResult(MetadataTagReadbackResult.Unverified("ID3v2 metadata is absent."));
            }
            if (appleAudio && apple is null)
            {
                return Task.FromResult(MetadataTagReadbackResult.Unverified("Apple audio metadata is absent."));
            }
            if (xiphAudio && xiph is null)
            {
                return Task.FromResult(MetadataTagReadbackResult.Unverified("Xiph audio metadata is absent."));
            }

            var mismatches = new List<string>();
            foreach (var (key, expected) in tags)
            {
                ct.ThrowIfCancellationRequested();
                string? actual = xiphAudio ? key switch
                {
                    "title" => xiph!.Title,
                    "artist" => xiph!.Performers?.FirstOrDefault(),
                    "album" => xiph!.Album,
                    "track_number" => xiph!.Track.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "genre" => xiph!.Genres?.FirstOrDefault(),
                    "description" => xiph!.Comment,
                    "year" => xiph!.Year.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => null,
                } : appleAudio ? key switch
                {
                    "title" => apple!.Title,
                    "artist" => apple!.Performers?.FirstOrDefault(),
                    "album" => apple!.Album,
                    "track_number" => apple!.Track.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "genre" => apple!.Genres?.FirstOrDefault(),
                    "description" => apple!.Comment,
                    "year" => apple!.Year.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => null,
                } : key switch
                {
                    "title" => id3!.Title,
                    "author" or "artist" => id3!.Performers?.FirstOrDefault(),
                    "album" or "series" => id3!.Album,
                    "track_number" or "series_position" => id3!.Track.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "narrator" => ReadUserText(id3!, "NARRATOR"),
                    "genre" => id3!.Genres?.FirstOrDefault(),
                    "description" => id3!.Comment,
                    "year" => id3!.Year.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "publisher" => id3!.Publisher,
                    _ when CustomIdKeys.Contains(key, StringComparer.OrdinalIgnoreCase) =>
                        ReadUserText(id3!, key.ToUpperInvariant()),
                    _ => null,
                };
                var equal = key is "track_number" or "series_position" or "year"
                    ? uint.TryParse(expected, out var number)
                      && uint.TryParse(actual, out var savedNumber) && number == savedNumber
                    : string.Equals(NormalizeValue(expected), NormalizeValue(actual), StringComparison.Ordinal);
                if (!equal)
                {
                    mismatches.Add(key);
                }
            }

            return Task.FromResult(mismatches.Count == 0
                ? MetadataTagReadbackResult.Verified()
                : MetadataTagReadbackResult.Unverified(
                    $"Audio tag read-back did not match: {string.Join(", ", mismatches)}."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(MetadataTagReadbackResult.Unverified(
                $"Audio tag read-back failed: {ex.GetType().Name}."));
        }
    }

    private static bool CanVerifyAllRequestedTags(string filePath, IReadOnlyDictionary<string, string> tags)
    {
        var extension = Path.GetExtension(filePath);
        return extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || (extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".m4b", StringComparison.OrdinalIgnoreCase))
                && tags.Keys.All(VerifiedAppleAudioKeys.Contains)
            || (extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
                && tags.Keys.All(VerifiedXiphAudioKeys.Contains)
                && IsProvenXiphContainer(filePath, extension);
    }

    private static bool IsProvenXiphContainer(string filePath, string extension)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            return IsProvenXiphContainer(file, extension);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProvenXiphContainer(TagLib.File file, string extension) =>
        extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            ? file is TagLib.Flac.File
            : file is TagLib.Ogg.File
              && file.Properties.Codecs.Any(codec => codec is TagLib.Ogg.Codecs.Vorbis);

    private static string? ReadUserText(TagLib.Id3v2.Tag id3, string description) =>
        TagLib.Id3v2.UserTextInformationFrame.Get(id3, description, false)?.Text?.FirstOrDefault();

    private static string? NormalizeValue(string? value) =>
        value?.Trim().Normalize(System.Text.NormalizationForm.FormC);

    /// <inheritdoc/>
    public Task WriteCoverArtAsync(
        string filePath,
        byte[] imageData,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Audio artwork write-back source is unavailable.", filePath);
        }

        if (!GetCapabilities(filePath).CanWriteArtwork)
        {
            throw new NotSupportedException($"AudioTagger cannot verify artwork embedding in {Path.GetExtension(filePath)}.");
        }

        if (imageData.Length == 0)
        {
            throw new ArgumentException("Artwork bytes cannot be empty.", nameof(imageData));
        }

        WithBackup(
            filePath,
            () =>
        {
            using (var file = TagLib.File.Create(filePath))
            {
                // Preserve unrelated embedded pictures such as back covers and
                // artist portraits. Only the effective front cover is replaced.
                var retainedPictures = file.Tag.Pictures
                    .Where(picture => picture.Type != TagLib.PictureType.FrontCover)
                    .ToList();
                retainedPictures.Add(new TagLib.Picture(new TagLib.ByteVector(imageData))
                {
                    Type        = TagLib.PictureType.FrontCover,
                    MimeType    = imageData.Length >= 8 && imageData.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                        ? "image/png" : "image/jpeg",
                    Description = "Cover",
                });
                file.Tag.Pictures = retainedPictures.ToArray();
                file.Save();
            }

            if (!ArtworkEmbeddingSupport.VerifyFrontCover(filePath, imageData))
            {
                throw new InvalidDataException("Audio front cover could not be verified after writing.");
            }

            var backupPath = filePath + BackupSuffix;
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            _logger.LogInformation("AudioTagger: wrote cover art ({Size} bytes) to {Path}",
                imageData.Length, filePath);
        },
            onFailure: ex => _logger.LogError(ex, "AudioTagger: failed to write cover art to {Path} — restoring backup", filePath));

        return Task.CompletedTask;
    }
}
