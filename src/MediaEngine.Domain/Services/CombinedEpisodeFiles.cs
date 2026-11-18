using System.Globalization;

namespace MediaEngine.Domain.Services;

/// <summary>
/// The words and counts for one file that covers several TV episodes (S01E19-20). Each covered episode stays its own
/// Work, with the coverage rows recorded in <c>media_asset_coverage</c> (spec multi-episode-files D4); a list shows the
/// file as ONE entry, "E19-20: Title A + Title B" (review mock-ups 2026-11-18 D9), and a count says how many episodes
/// there are and how many files hold them ("20 episodes · 19 files").
/// </summary>
public static class CombinedEpisodeFiles
{
    /// <summary>"E19-20" for an unbroken run, "E19" for one episode, "E19, E21" for a run with a gap. Empty when no numbers are given.</summary>
    public static string NumberText(IEnumerable<int> numbers)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        var ordered = numbers.Distinct().OrderBy(number => number).ToList();
        if (ordered.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        var start = ordered[0];
        var previous = start;
        for (var index = 1; index <= ordered.Count; index++)
        {
            if (index < ordered.Count && ordered[index] == previous + 1)
            {
                previous = ordered[index];
                continue;
            }

            parts.Add(start == previous
                ? string.Create(CultureInfo.InvariantCulture, $"E{start}")
                : string.Create(CultureInfo.InvariantCulture, $"E{start}-{previous}"));
            if (index < ordered.Count)
            {
                start = previous = ordered[index];
            }
        }

        return string.Join(", ", parts);
    }

    /// <summary>The titles of the covered episodes in order, joined: "Title A + Title B". Blank titles are left out.</summary>
    public static string TitleText(IEnumerable<string?> titles)
    {
        ArgumentNullException.ThrowIfNull(titles);
        return string.Join(" + ", titles.Where(title => !string.IsNullOrWhiteSpace(title)).Select(title => title!.Trim()));
    }

    /// <summary>The whole entry: "E19-20: Title A + Title B" (a single episode reads "E19: Title A").</summary>
    public static string Label(IEnumerable<(int Number, string? Title)> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        var ordered = episodes.OrderBy(episode => episode.Number).ToList();
        var numbers = NumberText(ordered.Select(episode => episode.Number));
        var titles = TitleText(ordered.Select(episode => episode.Title));
        if (numbers.Length == 0)
        {
            return titles;
        }

        return titles.Length == 0 ? numbers : $"{numbers}: {titles}";
    }

    /// <summary>
    /// How many episodes there are and how many physical files hold them. An episode with no file key counts as its own
    /// file. "Episodes" counts every episode a file covers; "files" counts physical files.
    /// </summary>
    public static (int Episodes, int Files) Count(IEnumerable<(string EpisodeId, string? FileId)> episodes)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (episodeId, fileId) in episodes)
        {
            if (!seen.Add(episodeId))
            {
                continue;
            }

            files.Add(string.IsNullOrWhiteSpace(fileId) ? "episode:" + episodeId : "file:" + fileId);
        }

        return (seen.Count, files.Count);
    }

    /// <summary>"20 episodes · 19 files" when a file covers more than one episode, otherwise "20 episodes" (null when there are none).</summary>
    public static string? CountText(int episodes, int files, string episodeWord = "episode", string fileWord = "file")
    {
        if (episodes <= 0)
        {
            return null;
        }

        var text = string.Create(CultureInfo.InvariantCulture, $"{episodes} {episodeWord}{(episodes == 1 ? string.Empty : "s")}");
        return files > 0 && files < episodes
            ? string.Create(CultureInfo.InvariantCulture, $"{text} · {files} {fileWord}{(files == 1 ? string.Empty : "s")}")
            : text;
    }
}
