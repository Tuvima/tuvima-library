using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>One file waiting in the recycle bin.</summary>
public sealed record RecycleBinEntry(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("library_id")] string? LibraryId,
    [property: JsonPropertyName("original_path")] string OriginalPath,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("deleted_at")] DateTimeOffset DeletedAt);

/// <summary>What happened when a file was put back.</summary>
public enum RecycleBinRestoreResult
{
    Restored,
    NotFound,
    /// <summary>Something is already at the file's old place; nothing was overwritten.</summary>
    PlaceTaken,
    Failed,
}

/// <summary>
/// The recycle bin for library media. A deleted file is moved into the Engine's own data folder with a small note of where it
/// came from, and leaves the disk only when the bin is emptied. Putting a file back never overwrites anything. Files are
/// held by the Engine's data folder, not inside a library folder, so a scan never sees them.
/// </summary>
public sealed class RecycleBinService(string dataRoot, ILogger logger)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private string Root => Path.Combine(dataRoot, "recycle-bin");

    /// <summary>Moves the file into the bin. Returns the entry, or throws the file system's own exception when it cannot be moved.</summary>
    public RecycleBinEntry MoveIn(string path, string? title, string? libraryId)
    {
        var info = new FileInfo(path);
        var size = info.Length;
        var id = Guid.NewGuid();
        var folder = Directory.CreateDirectory(Path.Combine(Root, id.ToString("N"))).FullName;
        var stored = Path.Combine(folder, info.Name);
        File.Move(path, stored);
        var entry = new RecycleBinEntry(id, info.Name, title, libraryId, path, size, DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(folder, "entry.json"), JsonSerializer.Serialize(entry, Json));
        return entry;
    }

    public IReadOnlyList<RecycleBinEntry> List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var entries = new List<RecycleBinEntry>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            if (TryRead(folder) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries.OrderByDescending(entry => entry.DeletedAt).ToList();
    }

    public RecycleBinRestoreResult Restore(Guid id)
    {
        var folder = FolderOf(id);
        if (TryRead(folder) is not { } entry || !File.Exists(Path.Combine(folder, entry.FileName)))
        {
            return RecycleBinRestoreResult.NotFound;
        }

        if (File.Exists(entry.OriginalPath) || Directory.Exists(entry.OriginalPath))
        {
            return RecycleBinRestoreResult.PlaceTaken;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(entry.OriginalPath)!);
            File.Move(Path.Combine(folder, entry.FileName), entry.OriginalPath);
            Directory.Delete(folder, recursive: true);
            return RecycleBinRestoreResult.Restored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not put {FileName} back from the recycle bin", entry.FileName);
            return RecycleBinRestoreResult.Failed;
        }
    }

    /// <summary>Removes every file in the bin from the disk for good. Returns how many were removed.</summary>
    public int Empty()
    {
        if (!Directory.Exists(Root))
        {
            return 0;
        }

        var removed = 0;
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not empty one recycle bin entry; it stays in the bin");
            }
        }

        return removed;
    }

    private string FolderOf(Guid id) => Path.Combine(Root, id.ToString("N"));

    private RecycleBinEntry? TryRead(string folder)
    {
        var note = Path.Combine(folder, "entry.json");
        try
        {
            return File.Exists(note) ? JsonSerializer.Deserialize<RecycleBinEntry>(File.ReadAllText(note), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "Skipped an unreadable recycle bin note");
            return null;
        }
    }
}
