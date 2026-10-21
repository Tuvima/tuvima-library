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

    /// <summary>
    /// What is waiting in the bin. <paramref name="hiddenLibraryIds"/> are the libraries the caller must not know about: the Vault's
    /// entries are visible only inside NSFW mode (Videos spec D34), so a locked caller passes the Vault libraries here.
    /// </summary>
    public IReadOnlyList<RecycleBinEntry> List(IReadOnlySet<string>? hiddenLibraryIds = null)
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var entries = new List<RecycleBinEntry>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            if (TryRead(folder) is { } entry && !IsHidden(entry, hiddenLibraryIds))
            {
                entries.Add(entry);
            }
        }

        return entries.OrderByDescending(entry => entry.DeletedAt).ToList();
    }

    /// <summary>Puts a file back. An entry in a hidden library answers exactly like one that is not in the bin.</summary>
    public RecycleBinRestoreResult Restore(Guid id, IReadOnlySet<string>? hiddenLibraryIds = null)
    {
        var folder = FolderOf(id);
        if (TryRead(folder) is not { } entry || IsHidden(entry, hiddenLibraryIds) || !File.Exists(Path.Combine(folder, entry.FileName)))
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

    /// <summary>
    /// Removes every file the caller can see in the bin from the disk for good. Returns how many were removed. Entries in a hidden
    /// library stay: a locked caller can neither remove them nor learn from the count that they exist.
    /// </summary>
    public int Empty(IReadOnlySet<string>? hiddenLibraryIds = null)
    {
        if (!Directory.Exists(Root))
        {
            return 0;
        }

        var removed = 0;
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            if (hiddenLibraryIds is { Count: > 0 } && TryRead(folder) is { } hidden && IsHidden(hidden, hiddenLibraryIds))
            {
                continue;
            }

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

    /// <summary>True while the bin still holds the entry with this id.</summary>
    public bool Contains(Guid id) => Directory.Exists(FolderOf(id));

    /// <summary>
    /// Removes the named entries from the disk for good (the Vault's image bin empties only its own images this way). An entry that is
    /// already gone counts as removed. Returns false when any entry could not be removed; those stay in the bin.
    /// </summary>
    public bool Remove(IReadOnlyCollection<Guid> ids)
    {
        var allGone = true;
        foreach (var id in ids)
        {
            var folder = FolderOf(id);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not empty one recycle bin entry; it stays in the bin");
                allGone = false;
            }
        }

        return allGone;
    }

    private static bool IsHidden(RecycleBinEntry entry, IReadOnlySet<string>? hiddenLibraryIds) =>
        hiddenLibraryIds is { Count: > 0 } && entry.LibraryId is { } library && hiddenLibraryIds.Contains(library);

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
