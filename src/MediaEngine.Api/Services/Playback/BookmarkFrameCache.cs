using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// The Library's own cache of video bookmark frames (Unified player v2 D21a): one folder per media asset, one 320 px wide JPEG per
/// bookmark inside it (<c>bookmark-frames/{assetId}/{bookmarkId}.jpg</c>). A frame is written whole to a temporary name and moved into
/// place, so a half-written picture is never offered. Everything here is a cache that can be rebuilt, so removing it is always safe.
/// </summary>
public sealed class BookmarkFrameCache(AssetPathService assetPaths, ILogger<BookmarkFrameCache>? logger = null)
{
    /// <summary>How wide a frame is (D21a: a 320 px wide still).</summary>
    public const int FrameWidth = 320;

    private const string Extension = ".jpg";
    private const string TemporarySuffix = ".tmp";

    public string Root => assetPaths.BookmarkFramesRoot;

    public string FolderFor(Guid assetId) => Path.Combine(Root, assetId.ToString("D"));

    public string PathFor(Guid assetId, Guid bookmarkId) => Path.Combine(FolderFor(assetId), bookmarkId.ToString("D") + Extension);

    public bool Exists(Guid assetId, Guid bookmarkId) => File.Exists(PathFor(assetId, bookmarkId));

    /// <summary>The file of a finished frame, or null when it has not been made yet.</summary>
    public string? Resolve(Guid assetId, Guid bookmarkId) => Exists(assetId, bookmarkId) ? PathFor(assetId, bookmarkId) : null;

    /// <summary>
    /// A number that changes whenever the frame is made again (the file's last-write time), for the address the list hands out so a
    /// re-taken frame is never served from a browser's cache. Null when there is no frame.
    /// </summary>
    public long? Version(Guid assetId, Guid bookmarkId)
    {
        var path = PathFor(assetId, bookmarkId);
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : null;
    }

    /// <summary>Writes a frame, replacing an earlier one for the same bookmark.</summary>
    public void Write(Guid assetId, Guid bookmarkId, byte[] jpeg)
    {
        var folder = FolderFor(assetId);
        Directory.CreateDirectory(folder);
        var final = PathFor(assetId, bookmarkId);
        var staging = final + "." + Guid.NewGuid().ToString("N") + TemporarySuffix;
        try
        {
            File.WriteAllBytes(staging, jpeg);
            File.Move(staging, final, overwrite: true);
        }
        finally
        {
            // After a successful move the temporary name no longer exists; after a failed write it is cleaned up here.
            DeleteFile(staging);
        }
    }

    /// <summary>Removes one bookmark's frame. True only when a frame existed and is now gone.</summary>
    public bool Delete(Guid assetId, Guid bookmarkId) => DeleteFile(PathFor(assetId, bookmarkId));

    /// <summary>Every finished frame in the cache, as (asset, bookmark) pairs, for the orphan sweep.</summary>
    public IReadOnlyList<(Guid AssetId, Guid BookmarkId, string Path)> ListFrames()
    {
        var found = new List<(Guid, Guid, string)>();
        if (!Directory.Exists(Root))
        {
            return found;
        }

        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            if (!Guid.TryParse(System.IO.Path.GetFileName(folder), out var assetId))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*" + Extension))
            {
                if (Guid.TryParse(System.IO.Path.GetFileNameWithoutExtension(file), out var bookmarkId))
                {
                    found.Add((assetId, bookmarkId, file));
                }
            }
        }

        return found;
    }

    /// <summary>Removes temporary files older than <paramref name="olderThan"/> and empty folders that a crash or the removal of frames left behind.</summary>
    public void RemoveLeftovers(TimeSpan olderThan)
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - olderThan;
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*" + TemporarySuffix))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    DeleteFile(file);
                }
            }

            if (!Directory.EnumerateFileSystemEntries(folder).Any())
            {
                TryDeleteEmptyFolder(folder);
            }
        }
    }

    private bool DeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A frame that cannot be removed now is removed by the next orphan sweep; it is only a cache.
            logger?.LogDebug(ex, "Could not remove the bookmark frame {File}", System.IO.Path.GetFileName(path));
            return false;
        }
    }

    private static void TryDeleteEmptyFolder(string folder)
    {
        try
        {
            Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Something wrote into the folder just now; it is kept.
        }
    }
}
