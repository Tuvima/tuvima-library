using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Ingestion.Contracts;
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
    public const int Version = 1;

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
    public Task WriteTagsAsync(
        string filePath,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!File.Exists(filePath))
        {
            _logger.LogWarning("AudioTagger: file not found — {Path}", filePath);
            return Task.CompletedTask;
        }

        WithBackup(
            filePath,
            () =>
        {
            using var file = TagLib.File.Create(filePath);

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

    /// <inheritdoc/>
    public Task WriteCoverArtAsync(
        string filePath,
        byte[] imageData,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!File.Exists(filePath) || imageData.Length == 0)
        {
            return Task.CompletedTask;
        }

        WithBackup(
            filePath,
            () =>
        {
            using var file = TagLib.File.Create(filePath);
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
