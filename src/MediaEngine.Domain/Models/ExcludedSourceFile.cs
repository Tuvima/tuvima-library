namespace MediaEngine.Domain.Models;

/// <summary>Why a media file stayed on disk when its title was deleted from the library.</summary>
public static class ExcludedSourceFileReasons
{
    /// <summary>The file is in an as-is folder, which Tuvima only reads.</summary>
    public const string AsIs = "as_is";

    /// <summary>The safety check refused to delete the file (for example, the original of a folder merge).</summary>
    public const string Protected = "protected";

    /// <summary>Tuvima tried to delete the file and could not (in use, or no permission).</summary>
    public const string DeleteFailed = "delete_failed";
}

/// <summary>
/// A media file that stayed on disk when its title was deleted, remembered so scans skip it and the title does not come back.
/// <see cref="RelativePath"/> is the file's place inside its source folder, written with forward slashes
/// (see <see cref="ExcludedSourceFilePaths.RelativePathOf"/>). The title, the person and the file facts are snapshots for the
/// Settings list and for noticing that a different file now sits at the same place.
/// </summary>
public sealed record ExcludedSourceFile(
    Guid Id,
    string LibraryId,
    string SourceId,
    string RelativePath,
    string? ContentHash,
    long? FileSize,
    DateTimeOffset? LastWriteUtc,
    string? Title,
    DateTimeOffset ExcludedAt,
    Guid? ExcludedByAccountId,
    string? ExcludedByName,
    string? Reason);

/// <summary>The one way the place of a file inside its source folder is written, so every writer and reader agrees.</summary>
public static class ExcludedSourceFilePaths
{
    /// <summary>The path of <paramref name="fullPath"/> inside <paramref name="sourceRoot"/>, relative, with forward slashes; null when it is not inside the root.</summary>
    public static string? RelativePathOf(string sourceRoot, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || string.IsNullOrWhiteSpace(fullPath))
        {
            return null;
        }

        var relative = Path.GetRelativePath(sourceRoot, fullPath);
        if (relative is "." or "" || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        return relative.Replace('\\', '/');
    }
}
