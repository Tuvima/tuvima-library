using System.Text.Json;
using MediaEngine.Api.Services.LocalAssets;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Services.View;

/// <summary>Moves one file from one place to another on the same volume (a rename). A seam so tests can make a move fail in ways a real disk rarely does.</summary>
public interface IViewTrashFileMover
{
    void Move(string source, string destination);
}

/// <summary>The real thing: <see cref="File.Move(string, string)"/>, which never overwrites.</summary>
public sealed class FileSystemViewTrashFileMover : IViewTrashFileMover
{
    public void Move(string source, string destination) => File.Move(source, destination);
}

/// <summary>One file parked in an item's staging folder: where it came from, and its name inside the folder (a name only, never a path).</summary>
public sealed record ViewTrashManifestEntry(string Original, string Staged);

/// <summary>What an item's staging folder holds and where each file belongs if the delete does not go through.</summary>
public sealed record ViewTrashManifest(Guid ItemId, IReadOnlyList<ViewTrashManifestEntry> Files);

/// <summary>
/// The hidden folder where Empty Trash parks an item's files before deleting them, so a failure part-way can put every file back.
/// The folder sits directly inside the owner's profile folder (same volume, so parking and putting back are renames) and holds one
/// folder per item with a manifest. The manifest is read back after a crash, so it is never trusted: staged names are checked to
/// be plain file names and every original is shown to lie inside the owner's profile folder before anything is moved.
/// </summary>
internal static class ViewTrashStaging
{
    public const string ManifestFileName = "manifest.json";
    private const string ManifestTempName = "manifest.json.tmp";

    /// <summary>
    /// Empty Trash and the recovery sweep never run at the same time. One lock for the whole Engine: emptying a Trash is rare, and
    /// two of them racing over the same folders is exactly what must not happen.
    /// </summary>
    public static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static string StagingRoot(string profileRoot) => Path.Combine(profileRoot, ViewStorageService.StagingFolderName);

    public static string ItemFolder(string profileRoot, Guid itemId) =>
        Path.Combine(StagingRoot(profileRoot), itemId.ToString("D"));

    /// <summary>Creates the item's staging folder, and the hidden staging folder above it when it is new (marked Hidden on Windows).</summary>
    public static void CreateItemFolder(string itemFolder, ILogger logger)
    {
        var stagingRoot = Path.GetDirectoryName(itemFolder)!;
        var isNew = !Directory.Exists(stagingRoot);
        Directory.CreateDirectory(itemFolder);
        if (isNew && OperatingSystem.IsWindows())
        {
            try
            {
                File.SetAttributes(stagingRoot, File.GetAttributes(stagingRoot) | FileAttributes.Hidden);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Hiding the folder is a courtesy; the folder works the same visible.
                logger.LogDebug(exception, "The Trash staging folder could not be marked hidden");
            }
        }
    }

    /// <summary>Writes the manifest (to a temporary name, flushed to disk, then renamed) so a crash never leaves half of one.</summary>
    public static void WriteManifest(string itemFolder, ViewTrashManifest manifest)
    {
        var temporary = Path.Combine(itemFolder, ManifestTempName);
        // CreateNew and no overwrite: a manifest that is already there belongs to an earlier, unfinished removal and is never replaced.
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, manifest, Json);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, Path.Combine(itemFolder, ManifestFileName), overwrite: false);
    }

    /// <summary>Reads a manifest, or null (with the reason logged) when it is missing or not understood.</summary>
    public static ViewTrashManifest? ReadManifest(string itemFolder, ILogger logger)
    {
        var path = Path.Combine(itemFolder, ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<ViewTrashManifest>(stream, Json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "A Trash staging manifest could not be read; its folder was left alone");
            return null;
        }
    }

    /// <summary>The path a staged file has inside <paramref name="itemFolder"/>, or null when the name is anything but a plain file name.</summary>
    public static string? StagedPathOf(string itemFolder, ViewTrashManifestEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Staged)
            || !string.Equals(Path.GetFileName(entry.Staged), entry.Staged, StringComparison.Ordinal)
            || entry.Staged is ManifestFileName or ManifestTempName)
        {
            return null;
        }

        return Path.Combine(itemFolder, entry.Staged);
    }

    /// <summary>
    /// Moves every parked file back where it came from, in reverse order. A file is never put over something that is already there,
    /// and an original that is not inside <paramref name="profileRoot"/> is never written to. Returns true when no parked file is left.
    /// </summary>
    public static bool MoveBack(string profileRoot, string itemFolder, ViewTrashManifest manifest, ILogger logger)
    {
        var allBack = true;
        foreach (var entry in manifest.Files.Reverse())
        {
            var staged = StagedPathOf(itemFolder, entry);
            if (staged is null)
            {
                logger.LogWarning("A Trash staging manifest names a staged file that is not a plain file name; it was left alone");
                allBack = false;
                continue;
            }

            if (!File.Exists(staged))
            {
                // Already back (or never moved): nothing to do for this one.
                continue;
            }

            if (!ViewStorageService.TryResolveContainedPath(profileRoot, entry.Original, out var resolved, out _))
            {
                logger.LogWarning("A parked file's original location is not inside the owner's profile folder; it was left in staging");
                allBack = false;
                continue;
            }

            try
            {
                if (File.Exists(resolved.Path) || Directory.Exists(resolved.Path))
                {
                    logger.LogWarning("Something is already at a parked file's original location; the parked file was left in staging");
                    allBack = false;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(resolved.Path)!);
                File.Move(staged, resolved.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogError(exception, "A parked file could not be put back; it stays in staging and the recovery sweep will try again");
                allBack = false;
            }
        }

        return allBack;
    }

    /// <summary>
    /// Deletes the parked files and then the manifest and the item's own staging folder. A file that cannot be deleted now stays (and the
    /// folder with it) for the recovery sweep. Returns true when the folder is gone.
    /// </summary>
    public static bool DeleteStaged(string itemFolder, ViewTrashManifest manifest, ILogger logger)
    {
        var allGone = true;
        foreach (var entry in manifest.Files)
        {
            var staged = StagedPathOf(itemFolder, entry);
            if (staged is null)
            {
                allGone = false;
                continue;
            }

            try
            {
                if (File.Exists(staged))
                {
                    File.Delete(staged);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "A parked file could not be deleted yet; the recovery sweep will remove it later");
                allGone = false;
            }
        }

        return allGone && RemoveItemFolder(itemFolder, logger);
    }

    /// <summary>Removes the manifest and the item's staging folder, but only when nothing else is left in it.</summary>
    public static bool RemoveItemFolder(string itemFolder, ILogger logger)
    {
        try
        {
            if (!Directory.Exists(itemFolder))
            {
                return true;
            }

            // The manifest goes only when the folder is about to go; while parked files remain it is what puts them back.
            var others = Directory.EnumerateFileSystemEntries(itemFolder)
                .Where(entry => Path.GetFileName(entry) is not (ManifestFileName or ManifestTempName));
            if (others.Any())
            {
                logger.LogWarning("A Trash staging folder still holds files, so it was kept");
                return false;
            }

            DeleteIfPresent(Path.Combine(itemFolder, ManifestFileName));
            DeleteIfPresent(Path.Combine(itemFolder, ManifestTempName));

            Directory.Delete(itemFolder, recursive: false);
            RemoveIfEmpty(Path.GetDirectoryName(itemFolder));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "A Trash staging folder could not be removed yet; the recovery sweep will try again");
            return false;
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    // The staging root itself is tidied away when the last item folder leaves, so an empty hidden folder is not left behind.
    private static void RemoveIfEmpty(string? folder)
    {
        if (folder is not null
            && string.Equals(Path.GetFileName(folder), ViewStorageService.StagingFolderName, StringComparison.Ordinal)
            && Directory.Exists(folder)
            && !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            Directory.Delete(folder, recursive: false);
        }
    }
}
