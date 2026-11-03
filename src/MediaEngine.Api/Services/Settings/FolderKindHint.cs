using System.Text.RegularExpressions;

namespace MediaEngine.Api.Services.Settings;

/// <summary>
/// A cheap, read-only guess at what kind of media a folder holds, used only after a folder has been added, to warn when its contents look
/// like a different library than the one it was added to (a TV folder added to Movies). Folder names are never read as a hint, because
/// people keep all their media under one root. For a Movies library it looks at a handful of entries inside for season folders and
/// episode-style file names. It never opens a file and never blocks a choice: the answer is a hint, not a rule.
/// </summary>
public static partial class FolderKindHint
{
    private const int MaxFoldersLooked = 12;
    private const int MaxFilesLooked = 60;

    /// <summary>
    /// The format (<c>TV</c>) the folder's contents look like when it differs from <paramref name="kindFormat"/> (<c>Movies</c>);
    /// null when it looks right, or when nothing about it says otherwise.
    /// </summary>
    public static string? LooksLikeOtherFormat(string path, string? kindFormat)
    {
        if (string.IsNullOrWhiteSpace(path) || kindFormat is not ("Movies" or "TV" or "Music" or "Audiobooks" or "Books" or "Comics"))
        {
            return null;
        }

        return kindFormat == "Movies" && HoldsEpisodes(path) ? "TV" : null;
    }

    private static bool HoldsEpisodes(string path)
    {
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false };
            var folders = Directory.EnumerateDirectories(path, "*", options).Take(MaxFoldersLooked).ToList();
            if (folders.Count(folder => SeasonFolder().IsMatch(Path.GetFileName(folder))) >= 1)
            {
                return true;
            }

            // A TV root holds one folder per show and each show holds its season folders, so one level down is enough.
            foreach (var show in folders.Take(5))
            {
                if (Directory.EnumerateDirectories(show, "*", options).Take(MaxFoldersLooked)
                    .Any(season => SeasonFolder().IsMatch(Path.GetFileName(season))))
                {
                    return true;
                }
            }

            return folders.Prepend(path).Take(6).Any(folder =>
                Directory.EnumerateFiles(folder, "*", options).Take(MaxFilesLooked).Any(file => EpisodeName().IsMatch(Path.GetFileName(file))));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be looked into simply gives no hint.
            _ = exception;
            return false;
        }
    }

    [GeneratedRegex(@"^(season|series)[ ._-]*\d{1,2}$|^s\d{1,2}$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolder();

    [GeneratedRegex(@"\bS\d{1,2}[ ._-]?E\d{1,3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeName();
}
