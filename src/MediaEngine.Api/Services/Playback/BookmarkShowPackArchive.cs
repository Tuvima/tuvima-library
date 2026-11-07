using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Playback;

/// <summary>One episode of a show file: its season and episode number, and its bookmarks.</summary>
public sealed record BookmarkShowPackEpisode(
    int SeasonNumber, int EpisodeNumber, string? Title, double? RunningTimeSeconds, IReadOnlyList<BookmarkPackBookmark> Bookmarks);

/// <summary>What a show file holds once it has been read and checked.</summary>
public sealed record BookmarkShowPackContent(string Name, IReadOnlyList<BookmarkShowPackEpisode> Episodes);

/// <summary>
/// The show bookmark file (universal editor v5 D37, screen Q2): one file with the Everyone's bookmarks of every episode of a show, each
/// episode named by its season and episode number, so it can be read by any library that has the show whatever its files are called. It follows
/// the single-title bookmark file's conventions (<see cref="BookmarkPackArchive"/>): a ZIP with <c>manifest.json</c> (schema version, creation
/// time, <c>includes_secrets: false</c>) beside the payload, the same safe-entry-path and compression-ratio checks on the way in, and nothing
/// personal: no file path, no profile name, no watch history.
/// </summary>
public static class BookmarkShowPackArchive
{
    public const string ManifestEntry = "manifest.json";
    public const string PayloadEntry = "pack/episodes.json";
    public const string Kind = "bookmark-show-pack";
    public const string SchemaVersion = "1.0";
    private const int PayloadFormatVersion = 1;
    private const long MaximumEntryBytes = 12L * 1024 * 1024;
    private const int MaximumCompressionRatio = 250;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static byte[] Write(BookmarkShowPackContent content, DateTimeOffset createdAt)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry(ManifestEntry, CompressionLevel.SmallestSize);
            using (var stream = manifest.Open())
            {
                JsonSerializer.Serialize(stream, new
                {
                    schema_version = SchemaVersion,
                    created_at = createdAt,
                    kind = Kind,
                    includes_secrets = false,
                    note = "Show bookmark file: each episode's bookmarks, named by season and episode number. It holds no file paths, profile names or watch history.",
                });
            }

            var payload = archive.CreateEntry(PayloadEntry, CompressionLevel.SmallestSize);
            using (var stream = payload.Open())
            {
                JsonSerializer.Serialize(stream, new Payload
                {
                    Format = Kind,
                    Version = PayloadFormatVersion,
                    Name = content.Name,
                    Episodes = content.Episodes.Select(episode => new PackEpisode
                    {
                        Season = episode.SeasonNumber,
                        Episode = episode.EpisodeNumber,
                        Title = episode.Title,
                        RunningTimeSeconds = episode.RunningTimeSeconds,
                        Bookmarks = episode.Bookmarks.Select(bookmark => new PackItem
                        {
                            Time = bookmark.Time,
                            EndTime = bookmark.EndTime,
                            Chapter = bookmark.Chapter,
                            Note = bookmark.Note,
                            Description = bookmark.Description,
                        }).ToList(),
                    }).ToList(),
                }, WriteOptions);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Reads and checks a show file. Throws <see cref="BookmarkPackInvalidException"/> with a safe message when it is not one.</summary>
    public static BookmarkShowPackContent Read(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > EditorBookmarkLimits.MaximumShowFileBytes)
        {
            throw new BookmarkPackInvalidException("This is not a show bookmark file, or it is too large.");
        }

        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            if (archive.Entries.Count != 2)
            {
                throw new BookmarkPackInvalidException("This is not a show bookmark file.");
            }

            foreach (var entry in archive.Entries)
            {
                var normalized = entry.FullName.Replace('\\', '/');
                if (normalized is not (ManifestEntry or PayloadEntry))
                {
                    throw new BookmarkPackInvalidException("This is not a show bookmark file.");
                }

                if (entry.Length > MaximumEntryBytes
                    || entry.CompressedLength > 0 && entry.Length / Math.Max(1, entry.CompressedLength) > MaximumCompressionRatio)
                {
                    throw new BookmarkPackInvalidException("This show bookmark file is not safe to open.");
                }
            }

            if (archive.GetEntry(ManifestEntry) is null || archive.GetEntry(PayloadEntry) is null)
            {
                throw new BookmarkPackInvalidException("This is not a show bookmark file.");
            }

            using (var manifest = JsonDocument.Parse(ReadEntry(archive.GetEntry(ManifestEntry)!)))
            {
                var root = manifest.RootElement;
                var kind = root.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : null;
                var version = root.TryGetProperty("schema_version", out var versionValue) ? versionValue.GetString() : null;
                if (kind != Kind || version != SchemaVersion
                    || root.TryGetProperty("includes_secrets", out var secrets) && secrets.ValueKind == JsonValueKind.True)
                {
                    throw new BookmarkPackInvalidException("This show bookmark file was made by a version that cannot be read here.");
                }
            }

            var payload = JsonSerializer.Deserialize<Payload>(ReadEntry(archive.GetEntry(PayloadEntry)!))
                ?? throw new BookmarkPackInvalidException("This is not a show bookmark file.");
            return Validate(payload);
        }
        catch (InvalidDataException)
        {
            throw new BookmarkPackInvalidException("This is not a show bookmark file.");
        }
        catch (JsonException)
        {
            throw new BookmarkPackInvalidException("This is not a show bookmark file.");
        }
    }

    private static BookmarkShowPackContent Validate(Payload payload)
    {
        if (payload.Format != Kind || payload.Version != PayloadFormatVersion)
        {
            throw new BookmarkPackInvalidException("This show bookmark file was made by a version that cannot be read here.");
        }

        var name = payload.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > BookmarkPackLimits.MaximumNameLength)
        {
            throw new BookmarkPackInvalidException("This show bookmark file has no valid name.");
        }

        if (payload.Episodes is not { Count: > 0 } episodes || episodes.Count > EditorBookmarkLimits.MaximumShowEpisodes)
        {
            throw new BookmarkPackInvalidException("This show bookmark file has no episodes, or too many.");
        }

        var total = 0;
        var result = new List<BookmarkShowPackEpisode>(episodes.Count);
        var seen = new HashSet<(int, int)>();
        foreach (var episode in episodes)
        {
            if (episode.Season < 0 || episode.Episode < 0 || episode.Season > 9999 || episode.Episode > 9999 || !seen.Add((episode.Season, episode.Episode))
                || episode.Bookmarks is not { Count: > 0 and <= BookmarkPackLimits.MaximumBookmarks } items)
            {
                throw new BookmarkPackInvalidException("This show bookmark file has an episode that is not valid.");
            }

            total += items.Count;
            if (total > EditorBookmarkLimits.MaximumShowBookmarks)
            {
                throw new BookmarkPackInvalidException("This show bookmark file has too many bookmarks.");
            }

            var bookmarks = new List<BookmarkPackBookmark>(items.Count);
            foreach (var item in items)
            {
                if (!double.IsFinite(item.Time) || item.Time < 0
                    || item.EndTime is double end && (!double.IsFinite(end) || end <= item.Time)
                    || !AudiobookBookmarkNotePolicy.TryNormalize(item.Note, out var note)
                    || !AudiobookBookmarkNotePolicy.TryNormalizeDescription(item.Description, out var description))
                {
                    throw new BookmarkPackInvalidException("This show bookmark file has a bookmark that is not valid.");
                }

                var chapter = string.IsNullOrWhiteSpace(item.Chapter) ? null : item.Chapter.Trim();
                if (chapter is { Length: > 300 })
                {
                    throw new BookmarkPackInvalidException("This show bookmark file has a bookmark that is not valid.");
                }

                bookmarks.Add(new BookmarkPackBookmark(item.Time, item.EndTime, chapter, null, note, description));
            }

            double? runningTime = episode.RunningTimeSeconds is double seconds && double.IsFinite(seconds) && seconds > 0 ? seconds : null;
            var title = string.IsNullOrWhiteSpace(episode.Title) ? null : episode.Title.Trim();
            result.Add(new BookmarkShowPackEpisode(episode.Season, episode.Episode, title is { Length: > 300 } ? title[..300] : title, runningTime, bookmarks));
        }

        return new BookmarkShowPackContent(name, result);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > MaximumEntryBytes)
            {
                throw new BookmarkPackInvalidException("This show bookmark file is not safe to open.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private sealed class Payload
    {
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("episodes")] public List<PackEpisode>? Episodes { get; set; }
    }

    private sealed class PackEpisode
    {
        [JsonPropertyName("season")] public int Season { get; set; }
        [JsonPropertyName("episode")] public int Episode { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("running_time_seconds")] public double? RunningTimeSeconds { get; set; }
        [JsonPropertyName("bookmarks")] public List<PackItem>? Bookmarks { get; set; }
    }

    private sealed class PackItem
    {
        [JsonPropertyName("time")] public double Time { get; set; }
        [JsonPropertyName("end_time")] public double? EndTime { get; set; }
        [JsonPropertyName("chapter")] public string? Chapter { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }
}
