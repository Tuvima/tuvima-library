using System.Text.RegularExpressions;

namespace MediaEngine.Api.Services.Settings;

/// <summary>
/// A cheap, read-only guess at what kind of media a folder holds, used only to warn when the folder someone picked looks like a
/// different library than the one they are adding to (a TV folder chosen for Movies). It looks at the folder's own name, then its
/// parent's, and, for a Movies library, a handful of entries inside for season folders and episode-style file names. It never opens a
/// file and never blocks a choice: the answer is a hint, not a rule.
/// </summary>
public static partial class FolderKindHint
{
    private const int MaxFoldersLooked = 12;
    private const int MaxFilesLooked = 60;

    /// <summary>
    /// The format (<c>Movies</c>, <c>TV</c>, <c>Music</c>, <c>Audiobooks</c>, <c>Books</c> or <c>Comics</c>) the folder looks like when it differs from
    /// <paramref name="kindFormat"/>; null when it looks right, or when nothing about it says otherwise.
    /// </summary>
    public static string? LooksLikeOtherFormat(string path, string? kindFormat)
    {
        if (string.IsNullOrWhiteSpace(path) || kindFormat is not ("Movies" or "TV" or "Music" or "Audiobooks" or "Books" or "Comics"))
        {
            return null;
        }

        var named = FormatNamedBy(Path.GetFileName(path.TrimEnd('\\', '/')))
            ?? FormatNamedBy(Path.GetFileName(Path.GetDirectoryName(path.TrimEnd('\\', '/'))?.TrimEnd('\\', '/')));
        if (named is not null)
        {
            return named == kindFormat ? null : named;
        }

        return kindFormat == "Movies" && HoldsEpisodes(path) ? "TV" : null;
    }

    /// <summary>The words people use for a kind of media in a folder name, or null when the name says nothing.</summary>
    internal static string? FormatNamedBy(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        var words = WordSplit().Split(folderName.ToLowerInvariant()).Where(word => word.Length > 0).ToHashSet();
        var joined = string.Concat(words);
        if (words.Overlaps(["tv", "television"]) || joined is "tvshows" or "shows")
        {
            return "TV";
        }

        if (words.Overlaps(["movies", "movie", "films", "film"]))
        {
            return "Movies";
        }

        if (words.Overlaps(["audiobooks", "audiobook"]))
        {
            return "Audiobooks";
        }

        if (words.Overlaps(["music", "albums", "songs"]))
        {
            return "Music";
        }

        if (words.Overlaps(["comics", "comic", "manga"]))
        {
            return "Comics";
        }

        return words.Overlaps(["books", "book", "ebooks", "ebook"]) ? "Books" : null;
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

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex WordSplit();

    [GeneratedRegex(@"^(season|series)[ ._-]*\d{1,2}$|^s\d{1,2}$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolder();

    [GeneratedRegex(@"\bS\d{1,2}[ ._-]?E\d{1,3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeName();
}
