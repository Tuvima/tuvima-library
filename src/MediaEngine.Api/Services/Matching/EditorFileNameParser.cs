using System.Globalization;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Matching;

/// <summary>What a file's name says about what it is.</summary>
/// <param name="Title">The title once the show, season, episode, year and release markers are taken out.</param>
/// <param name="Show">For TV, the show named by the file name or, failing that, by the folder above its season folder.</param>
/// <param name="Season">The season a TV file name carries, when it carries one.</param>
/// <param name="Episode">The episode number a TV file name carries, when it carries one.</param>
/// <param name="Year">A four-digit year in brackets or set off by dots or spaces.</param>
/// <param name="Edition">An edition marker the name carries (Extended, Director's Cut, Remastered, Live), when it carries one.</param>
/// <param name="Track">For music, the track number at the start of the name.</param>
/// <param name="Disc">For music, the disc number, when the name carries one.</param>
/// <param name="Absolute">For TV, an absolute episode number (a show numbered straight through, as anime is), when the name carries one and no season-and-episode pair.</param>
public sealed record ParsedFileName(
    string? Title, string? Show, int? Season, int? Episode, string? Year, string? Edition = null, int? Track = null, int? Disc = null, int? Absolute = null);

/// <summary>
/// Reads a file name the way the Match tool starts from it (universal editor v3, WP4): the show, season and episode of a TV file
/// (<c>Show.S02E05.Title.mkv</c>, <c>Show - 2x05 - Title.mp4</c>, <c>Show Season 2 Episode 5.mkv</c>), or the plain title of anything
/// else, with release tags (1080p, WEB-DL, x264 and the like), brackets and a year taken out. A read of text only: it never opens the file
/// and never decides a match, it only gives the search somewhere sensible to begin. Every list it reads (the noise words, the separators,
/// the year rule, the season-and-episode, track and disc patterns, the edition markers) comes from <c>config/filename_cleaning.json</c>
/// (universal editor v5, D33), shared with the Videos title cleaner and the scan.
/// </summary>
public static class EditorFileNameParser
{
    /// <summary>Parses a file name (with or without its folders). <paramref name="isTv"/> turns on the show, season and episode reading.</summary>
    public static ParsedFileName Parse(string? filePath, bool isTv) => Parse(filePath, isTv, FilenameCleaner.Rules);

    /// <summary>Parses a file name with the rules given.</summary>
    public static ParsedFileName Parse(string? filePath, bool isTv, FilenameCleaningRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new ParsedFileName(null, null, null, null, null);
        }

        var segments = filePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var name = Path.GetFileNameWithoutExtension(segments[^1]);
        var folders = segments.Length > 1 ? segments[..^1] : [];

        var year = rules.FindYear(name);
        if (!isTv)
        {
            var plain = rules.StripTags(name, out var plainEdition);
            return new ParsedFileName(Clean(rules, rules.StripYear(plain)), null, null, null, year, plainEdition);
        }

        int? season = null;
        int? episode = null;
        int? absolute = null;
        string? show = null;
        string? title = null;
        string? edition = null;
        var numbered = rules.SeasonEpisode.Select(pattern => pattern.Match(name)).FirstOrDefault(match => match.Success);
        if (numbered is not null)
        {
            season = ReadNumber(numbered, "s");
            episode = ReadNumber(numbered, "e");
            show = Clean(rules, rules.StripYear(name[..numbered.Index]));
            title = Clean(rules, rules.StripTags(name[(numbered.Index + numbered.Length)..], out edition));
        }
        else if (rules.AbsoluteEpisode.Select(pattern => pattern.Match(name)).FirstOrDefault(match => match.Success && !IsYearOnly(rules, match)) is { } counted)
        {
            absolute = ReadNumber(counted, "e");
            show = Clean(rules, rules.StripYear(name[..counted.Index]));
            title = Clean(rules, rules.StripTags(name[(counted.Index + counted.Length)..], out edition));
        }
        else
        {
            title = Clean(rules, rules.StripYear(rules.StripTags(name, out edition)));
        }

        if (string.IsNullOrWhiteSpace(show))
        {
            show = ShowFromFolders(rules, folders, out var folderSeason);
            season ??= folderSeason;
        }
        else if (season is null)
        {
            _ = ShowFromFolders(rules, folders, out var folderSeason);
            season = folderSeason;
        }

        return new ParsedFileName(string.IsNullOrWhiteSpace(title) ? null : title, string.IsNullOrWhiteSpace(show) ? null : show, season, episode, year, edition, Absolute: absolute);
    }

    // "Show - 2024" is a year, not the 2024th episode.
    private static bool IsYearOnly(FilenameCleaningRules rules, Match match) =>
        match.Groups["e"].Value.Length == 4
        && Regex.IsMatch(match.Groups["e"].Value, $"^(?:{rules.Configuration.Year.Pattern})$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Reads a music file name: the disc and track number at its start and the song title after them ("1-05 Karma Police.flac",
    /// "03 - Paranoid Android.mp3"). The artist and album come from the file's tags or its folders, never from the name.
    /// </summary>
    public static ParsedFileName ParseMusic(string? filePath) => ParseMusic(filePath, FilenameCleaner.Rules);

    /// <summary>Reads a music file name with the rules given.</summary>
    public static ParsedFileName ParseMusic(string? filePath, FilenameCleaningRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new ParsedFileName(null, null, null, null, null);
        }

        var name = Path.GetFileNameWithoutExtension(filePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)[^1]);
        var (disc, track, rest) = rules.ReadTrack(name);
        var tidy = Clean(rules, rules.StripYear(rules.StripTags(rest, out var edition)));
        return new ParsedFileName(tidy, null, null, null, rules.FindYear(name), edition, track, disc);
    }

    private static int? ReadNumber(Match match, string group) =>
        match.Groups[group] is { Success: true } hit
        && int.TryParse(hit.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    // The show is the folder above the season folder ("Show/Season 2/file"); a season folder also tells the season.
    private static string? ShowFromFolders(FilenameCleaningRules rules, string[] folders, out int? season)
    {
        season = null;
        if (folders.Length == 0)
        {
            return null;
        }

        var last = folders[^1];
        if (rules.SeasonFolder.IsMatch(last.Trim()))
        {
            var digits = new string(last.Where(char.IsAsciiDigit).ToArray());
            season = digits.Length > 0 && int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : last.StartsWith("special", StringComparison.OrdinalIgnoreCase) ? 0 : null;
            return folders.Length > 1 ? Clean(rules, rules.StripYear(rules.StripTags(folders[^2]))) : null;
        }

        return Clean(rules, rules.StripYear(rules.StripTags(last)));
    }

    private static string? Clean(FilenameCleaningRules rules, string? text) => rules.Tidy(text);

    /// <summary>A name taken from a tag or a folder with release noise, brackets and a year taken out; null when nothing is left.</summary>
    public static string? CleanTitle(string? text, FilenameCleaningRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        return string.IsNullOrWhiteSpace(text) ? null : Clean(rules, rules.StripYear(rules.StripTags(text)));
    }

    /// <summary>
    /// A comic's cleaned name read as its series and issue number ("Batman 012" is Batman, issue 12). A name that is only a number, or
    /// carries none, is all series.
    /// </summary>
    public static (string? Series, int? Issue) ReadIssue(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, null);
        }

        var match = IssueNumber.Match(name.Trim());
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups["series"].Value)
            || !int.TryParse(match.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var issue))
        {
            return (name.Trim(), null);
        }

        return (match.Groups["series"].Value.Trim(), issue);
    }

    private static readonly Regex IssueNumber = new(
        @"^(?<series>.*?)[\s._-]*(?:#|no\.?\s*|issue\s*)?0*(?<n>\d{1,4})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));
}
