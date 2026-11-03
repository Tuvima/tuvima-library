using System.Text.RegularExpressions;

namespace MediaEngine.Api.Services.Settings;

/// <summary>
/// A cheap, read-only guess at whether the folder someone picked holds a different kind of media than the library they are adding to (season
/// folders and episode-style file names inside a Movies folder). It looks only at what is inside, never at what the folder is called: names are
/// unreliable, and the first-run setup rework removed the name-based warnings. It never opens a file and never blocks a choice: the answer is a hint.
/// </summary>
public static partial class FolderKindHint
{
    private const int MaxFoldersLooked = 12;
    private const int MaxFilesLooked = 60;

    /// <summary>"TV" when a Movies folder holds season folders or episode-style files; null when nothing inside says otherwise.</summary>
    public static string? LooksLikeOtherFormat(string path, string? kindFormat) =>
        !string.IsNullOrWhiteSpace(path) && kindFormat == "Movies" && HoldsEpisodes(path) ? "TV" : null;

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
