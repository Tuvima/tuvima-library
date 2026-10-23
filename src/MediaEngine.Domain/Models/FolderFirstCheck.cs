namespace MediaEngine.Domain.Models;

/// <summary>The states of a new folder's first check (Library setup spec D35, D61).</summary>
public static class FolderFirstCheckStates
{
    /// <summary>The first files are being identified. Nothing in the folder is renamed, moved or tagged.</summary>
    public const string Checking = "checking";

    /// <summary>The check passed: the held moves and tags are released to the next run.</summary>
    public const string Passed = "passed";

    /// <summary>Most of the tried files are Unmatched: the folder's remaining identity jobs are not claimed and nothing is touched until an administrator chooses.</summary>
    public const string Paused = "paused";

    /// <summary>An administrator chose Keep going on a paused folder: identification resumes, Unmatched files stay Unmatched.</summary>
    public const string KeptGoing = "kept_going";

    /// <summary>True for the states in which the folder is held: nothing in it is renamed, moved or tagged.</summary>
    public static bool IsHeld(string state) => state is Checking or Paused;
}

/// <summary>The outcome of one file that has reached a final identification result, as the first check counts it.</summary>
public static class FolderFirstCheckOutcomes
{
    public const string Matched = "matched";
    public const string Unmatched = "unmatched";
    public const string NeedsReview = "needs_review";
}

/// <summary>A new folder's first check as stored: the folder, the state, and the counts the decision was (or is being) made on.</summary>
public sealed record FolderFirstCheck(
    string SourceId,
    string LibraryId,
    string Path,
    string State,
    int Tried,
    int Unmatched,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

/// <summary>
/// A folder's first check with what is known now: files tried and Unmatched among the first files, and every file of the folder the Engine
/// knows about. While the check is still running the counts are live; once it has ended they are the counts it ended on.
/// </summary>
public sealed record FolderFirstCheckSnapshot(FolderFirstCheck Check, int TotalFiles);

/// <summary>What the first check has seen so far, used to decide whether it can end.</summary>
/// <param name="Tried">Files among the first <c>first_check_files</c> that reached a final outcome.</param>
/// <param name="Unmatched">Of those, the Unmatched ones (a YouTube file with no ID counts).</param>
/// <param name="Waiting">Known files of the folder that have no outcome yet and are still waiting for their lookup.</param>
/// <param name="IngestionSettled">True when no file of the folder is still being taken in.</param>
/// <param name="TotalFiles">Every file of the folder the Engine knows about.</param>
public sealed record FolderFirstCheckProgress(int Tried, int Unmatched, int Waiting, bool IngestionSettled, int TotalFiles);

/// <summary>How a folder's path is compared with a file's path in the first check.</summary>
public static class FolderFirstCheckPaths
{
    /// <summary>
    /// The folder as a prefix: every separator as <c>/</c> and exactly one <c>/</c> at the end, so a file is in the folder when its path, written
    /// the same way, starts with the prefix. A root (<c>/</c>, <c>D:\</c>) keeps its single separator and never becomes an empty prefix that
    /// would match every path. The letter case is kept: outside Windows paths differ by case.
    /// </summary>
    public static string Prefix(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.Replace('\\', '/').TrimEnd('/') + "/";
    }

    /// <summary>The prefix with ASCII letters lower-cased (the data store lower-cases ASCII only), for the case-insensitive range the index serves.</summary>
    public static string Folded(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var chars = prefix.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'A' and <= 'Z')
            {
                chars[i] = (char)(chars[i] + 32);
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// The first string after every string that starts with <paramref name="prefix"/>: the prefix with its closing <c>/</c> replaced by the next
    /// character, <c>0</c>. A path is under the prefix when it is at or above the prefix and below this end, which an index can answer.
    /// </summary>
    public static string RangeEnd(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        return prefix[..^1] + "0";
    }

    /// <summary>
    /// True when <paramref name="filePath"/> is inside the folder whose <see cref="Prefix"/> is <paramref name="prefix"/>: the same answer the
    /// data store gives (<c>FolderPathSql.InFolder</c>) for a file path held in <c>media_assets.file_path_root</c>. Separators are compared as
    /// <c>/</c>, so <c>/media/kids2/a.mkv</c> is not in <c>/media/kids</c> and a trailing separator on the folder changes nothing. Outside
    /// Windows letter case matters; on Windows ASCII letters are compared without case.
    /// </summary>
    public static bool IsInFolder(string prefix, string? filePath) =>
        IsInFolder(prefix, filePath, OperatingSystem.IsWindows());

    /// <summary><see cref="IsInFolder(string, string?)"/> with the platform's case rule given, so both rules can be tested anywhere.</summary>
    public static bool IsInFolder(string prefix, string? filePath, bool ignoreCase)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var path = filePath.Replace('\\', '/');
        return ignoreCase
            ? Folded(path).StartsWith(Folded(prefix), StringComparison.Ordinal)
            : path.StartsWith(prefix, StringComparison.Ordinal);
    }
}
