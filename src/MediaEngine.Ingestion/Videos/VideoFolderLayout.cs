using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Ingestion.Videos;

/// <summary>Where one video sits in its Videos folder.</summary>
/// <param name="IsStandalone">A loose file, or the only video in its sub-folder.</param>
/// <param name="SeriesKey">The series folder relative to the library folder; the stable key of the series.</param>
/// <param name="SeriesTitle">The series folder name, cleaned.</param>
/// <param name="SectionTitle">The sub-folder the video sits in inside its series, or <see langword="null"/>.</param>
/// <param name="SectionKey">The section folder's own name in lower case; the stable key of the section (titles can repeat).</param>
/// <param name="SectionIndex">The section's position inside the series.</param>
/// <param name="EpisodeIndex">The video's position inside its section (or the series when it has no section).</param>
public sealed record VideoPlacement(
    bool IsStandalone,
    string? SeriesKey,
    string? SeriesTitle,
    string? SectionTitle,
    string? SectionKey,
    int? SectionIndex,
    int EpisodeIndex);

/// <summary>
/// Reads the shape of a Videos folder: a file directly in the folder is a standalone video; a sub-folder holding
/// several videos is a series; sub-folders inside a series are sections; a sub-folder holding one video stays
/// standalone. Order is the number in the file name, otherwise natural order (2 before 10).
/// </summary>
public static partial class VideoFolderLayout
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".mpeg", ".mpg", ".m2ts",
    };

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRegex();

    public static bool IsVideoFile(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static VideoPlacement Locate(string libraryFolder, string filePath, VideoNamingConfiguration naming)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryFolder));
        var file = Path.GetFullPath(filePath);
        var relative = Path.GetRelativePath(root, file);
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1 || Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal) || parts.Any(IsHiddenName))
        {
            return Standalone();
        }

        var seriesFolder = Path.Combine(root, parts[0]);
        var videos = EnumerateVideos(seriesFolder).ToList();
        if (videos.Count < 2)
        {
            return Standalone();
        }

        var rules = naming.TitleCleaning;
        var seriesKey = parts[0].Trim().ToLowerInvariant();
        var seriesTitle = VideoTitleCleaner.Clean(parts[0], rules, stripLeadingNumber: false).Title;

        // Group the series' videos by section (the first folder below the series), in one pass.
        string? SectionOf(string path)
        {
            var below = Path.GetRelativePath(seriesFolder, path).Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            return below.Length > 1 ? below[0] : null;
        }

        var ownSection = SectionOf(file);
        var sections = videos.Select(SectionOf).Where(s => s is not null).Select(s => s!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int? sectionIndex = null;
        if (ownSection is not null)
        {
            sectionIndex = IndexOf(sections, ownSection, name => VideoTitleCleaner.LeadingNumber(name, rules));
        }

        var group = videos.Where(path => string.Equals(SectionOf(path), ownSection, StringComparison.OrdinalIgnoreCase)).ToList();
        var episodeIndex = IndexOf(
            group.Select(Path.GetFileNameWithoutExtension).Select(n => n ?? string.Empty).ToList(),
            Path.GetFileNameWithoutExtension(file) ?? string.Empty,
            name => VideoTitleCleaner.LeadingNumber(name, rules));

        return new VideoPlacement(
            false,
            seriesKey,
            seriesTitle,
            ownSection is null ? null : VideoTitleCleaner.Clean(ownSection, rules, stripLeadingNumber: true).Title,
            ownSection?.Trim().ToLowerInvariant(),
            sectionIndex,
            episodeIndex);

        static VideoPlacement Standalone() => new(true, null, null, null, null, null, 1);
    }

    /// <summary>
    /// A name's position among its siblings: its own number when every sibling has a different number, otherwise
    /// its place in natural order. Positions start at 1.
    /// </summary>
    internal static int IndexOf(IReadOnlyList<string> siblings, string name, Func<string, int?> numberOf)
    {
        var numbers = siblings.Select(numberOf).ToList();
        var own = numberOf(name);
        if (own is not null && numbers.All(n => n is not null) && numbers.Distinct().Count() == numbers.Count)
        {
            return own.Value;
        }

        var ordered = siblings.OrderBy(NaturalKey, StringComparer.OrdinalIgnoreCase).ToList();
        return Math.Max(0, ordered.FindIndex(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase))) + 1;
    }

    internal static string NaturalKey(string value) => DigitsRegex().Replace(value, m => m.Value.PadLeft(12, '0'));

    private static bool IsHiddenName(string name) => name.StartsWith('.');

    private static IEnumerable<string> EnumerateVideos(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.EnumerateFiles(folder, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System,
            IgnoreInaccessible = true,
        }).Where(IsVideoFile).Where(path => !Path.GetRelativePath(folder, path)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]).Any(IsHiddenName));
    }
}
