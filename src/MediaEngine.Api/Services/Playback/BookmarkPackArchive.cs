using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Playback;

/// <summary>A bookmark pack file could not be made or read; the message is safe to show to the person.</summary>
public sealed class BookmarkPackInvalidException(string message) : Exception(message);

/// <summary>What a bookmark pack file holds once it has been read and checked.</summary>
public sealed record BookmarkPackContent(
    string Name,
    string? Author,
    BookmarkPackTitle Title,
    IReadOnlyList<BookmarkPackBookmark> Bookmarks,
    string ContentHash);

/// <summary>
/// The bookmark pack file (Videos spec D46). It follows the library backup's conventions: a ZIP archive with a <c>manifest.json</c>
/// (schema version, creation time, <c>includes_secrets: false</c>) beside the payload, the same safe-entry-path and compression-ratio
/// checks on the way in, and no password, because a pack, like a backup, never holds anything secret. A pack holds only how to
/// recognise the title and, per bookmark, its time, optional end, chapter name, note and description: never a file path, a profile
/// name or what anyone watched.
/// </summary>
public static class BookmarkPackArchive
{
    public const string ManifestEntry = "manifest.json";
    public const string PayloadEntry = "pack/bookmarks.json";
    public const string Kind = "bookmark-pack";
    public const string SchemaVersion = "1.0";
    private const int PayloadFormatVersion = 1;
    private const long MaximumEntryBytes = 2L * 1024 * 1024;
    private const int MaximumCompressionRatio = 250;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static byte[] Write(BookmarkPackContent content, DateTimeOffset createdAt)
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
                    note = "Bookmark pack: how to recognise one title and some saved positions. It holds no file paths, profile names or watch history.",
                });
            }

            var payload = archive.CreateEntry(PayloadEntry, CompressionLevel.SmallestSize);
            using (var stream = payload.Open())
            {
                JsonSerializer.Serialize(stream, new PackPayload
                {
                    Format = Kind,
                    Version = PayloadFormatVersion,
                    Name = content.Name,
                    Author = content.Author,
                    Title = new PackTitle
                    {
                        Ids = content.Title.Ids.ToDictionary(pair => pair.Key, pair => pair.Value),
                        RunningTimeSeconds = content.Title.RunningTimeSeconds,
                        Edition = content.Title.Edition,
                    },
                    Bookmarks = content.Bookmarks.Select(bookmark => new PackItem
                    {
                        Time = bookmark.Time,
                        EndTime = bookmark.EndTime,
                        Chapter = bookmark.Chapter,
                        ChapterStart = bookmark.ChapterStart,
                        Note = bookmark.Note,
                        Description = bookmark.Description,
                    }).ToList(),
                }, WriteOptions);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Reads and checks a pack file. Throws <see cref="BookmarkPackInvalidException"/> with a safe message when it is not one.</summary>
    public static BookmarkPackContent Read(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > BookmarkPackLimits.MaximumFileBytes)
        {
            throw new BookmarkPackInvalidException("This is not a bookmark pack, or it is too large.");
        }

        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            if (archive.Entries.Count != 2)
            {
                throw new BookmarkPackInvalidException("This is not a bookmark pack.");
            }

            foreach (var entry in archive.Entries)
            {
                var normalized = entry.FullName.Replace('\\', '/');
                if (normalized is not (ManifestEntry or PayloadEntry))
                {
                    throw new BookmarkPackInvalidException("This is not a bookmark pack.");
                }

                if (entry.Length > MaximumEntryBytes
                    || entry.CompressedLength > 0 && entry.Length / Math.Max(1, entry.CompressedLength) > MaximumCompressionRatio)
                {
                    throw new BookmarkPackInvalidException("This bookmark pack is not safe to open.");
                }
            }

            if (archive.GetEntry(ManifestEntry) is null || archive.GetEntry(PayloadEntry) is null)
            {
                throw new BookmarkPackInvalidException("This is not a bookmark pack.");
            }

            using (var manifest = JsonDocument.Parse(ReadEntry(archive.GetEntry(ManifestEntry)!)))
            {
                var root = manifest.RootElement;
                var kind = root.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() : null;
                var version = root.TryGetProperty("schema_version", out var versionValue) ? versionValue.GetString() : null;
                if (kind != Kind || version != SchemaVersion
                    || root.TryGetProperty("includes_secrets", out var secrets) && secrets.ValueKind == JsonValueKind.True)
                {
                    throw new BookmarkPackInvalidException("This bookmark pack was made by a version that cannot be read here.");
                }
            }

            var payload = JsonSerializer.Deserialize<PackPayload>(ReadEntry(archive.GetEntry(PayloadEntry)!))
                ?? throw new BookmarkPackInvalidException("This is not a bookmark pack.");
            return Validate(payload);
        }
        catch (InvalidDataException)
        {
            throw new BookmarkPackInvalidException("This is not a bookmark pack.");
        }
        catch (JsonException)
        {
            throw new BookmarkPackInvalidException("This is not a bookmark pack.");
        }
    }

    /// <summary>A fingerprint of a pack's name, author and bookmarks, so the same pack is not imported twice on a title.</summary>
    public static string Fingerprint(string name, string? author, IReadOnlyList<BookmarkPackBookmark> bookmarks)
    {
        var text = new StringBuilder();
        text.Append(name).Append('\n').Append(author).Append('\n');
        foreach (var bookmark in bookmarks.OrderBy(item => item.Time).ThenBy(item => item.EndTime))
        {
            text.Append(bookmark.Time.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                .Append(bookmark.EndTime?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                .Append(bookmark.Chapter).Append('|').Append(bookmark.Note).Append('|').Append(bookmark.Description).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static BookmarkPackContent Validate(PackPayload payload)
    {
        if (payload.Format != Kind || payload.Version != PayloadFormatVersion)
        {
            throw new BookmarkPackInvalidException("This bookmark pack was made by a version that cannot be read here.");
        }

        var name = payload.Name?.Trim();
        var author = string.IsNullOrWhiteSpace(payload.Author) ? null : payload.Author.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > BookmarkPackLimits.MaximumNameLength
            || author is { Length: > BookmarkPackLimits.MaximumAuthorLength })
        {
            throw new BookmarkPackInvalidException("This bookmark pack has no valid name.");
        }

        if (payload.Bookmarks is not { Count: > 0 } items || items.Count > BookmarkPackLimits.MaximumBookmarks)
        {
            throw new BookmarkPackInvalidException("This bookmark pack has no bookmarks, or too many.");
        }

        var bookmarks = new List<BookmarkPackBookmark>(items.Count);
        foreach (var item in items)
        {
            if (!double.IsFinite(item.Time) || item.Time < 0
                || item.EndTime is double end && (!double.IsFinite(end) || end <= item.Time)
                || item.ChapterStart is double start && (!double.IsFinite(start) || start < 0)
                || !AudiobookBookmarkNotePolicy.TryNormalize(item.Note, out var note)
                || !AudiobookBookmarkNotePolicy.TryNormalizeDescription(item.Description, out var description))
            {
                throw new BookmarkPackInvalidException("This bookmark pack has a bookmark that is not valid.");
            }

            var chapter = string.IsNullOrWhiteSpace(item.Chapter) ? null : item.Chapter.Trim();
            if (chapter is { Length: > 300 })
            {
                throw new BookmarkPackInvalidException("This bookmark pack has a bookmark that is not valid.");
            }

            bookmarks.Add(new BookmarkPackBookmark(item.Time, item.EndTime, chapter, chapter is null ? null : item.ChapterStart, note, description));
        }

        var ids = (payload.Title?.Ids ?? [])
            .Where(pair => BookmarkPackMatcher.RecognitionIdKeys.Contains(pair.Key)
                && !string.IsNullOrWhiteSpace(pair.Value) && pair.Value.Length <= 100)
            .GroupBy(pair => pair.Key.Trim().ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Value.Trim(), StringComparer.Ordinal);
        double? runningTime = payload.Title?.RunningTimeSeconds is double seconds && double.IsFinite(seconds) && seconds > 0 ? seconds : null;
        var edition = string.IsNullOrWhiteSpace(payload.Title?.Edition) ? null : payload.Title!.Edition!.Trim();
        if (edition is { Length: > 200 })
        {
            edition = edition[..200];
        }

        return new BookmarkPackContent(name, author, new BookmarkPackTitle(ids, runningTime, edition), bookmarks, Fingerprint(name, author, bookmarks));
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
                throw new BookmarkPackInvalidException("This bookmark pack is not safe to open.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private sealed class PackPayload
    {
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("author")] public string? Author { get; set; }
        [JsonPropertyName("title")] public PackTitle? Title { get; set; }
        [JsonPropertyName("bookmarks")] public List<PackItem>? Bookmarks { get; set; }
    }

    private sealed class PackTitle
    {
        [JsonPropertyName("ids")] public Dictionary<string, string>? Ids { get; set; }
        [JsonPropertyName("running_time_seconds")] public double? RunningTimeSeconds { get; set; }
        [JsonPropertyName("edition")] public string? Edition { get; set; }
    }

    private sealed class PackItem
    {
        [JsonPropertyName("time")] public double Time { get; set; }
        [JsonPropertyName("end_time")] public double? EndTime { get; set; }
        [JsonPropertyName("chapter")] public string? Chapter { get; set; }
        [JsonPropertyName("chapter_start")] public double? ChapterStart { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
    }
}
