using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaEngine.Api.Services.Matching;

/// <summary>What a file's name says about what it is.</summary>
/// <param name="Title">The title once the show, season, episode, year and release markers are taken out.</param>
/// <param name="Show">For TV, the show named by the file name or, failing that, by the folder above its season folder.</param>
/// <param name="Season">The season a TV file name carries, when it carries one.</param>
/// <param name="Episode">The episode number a TV file name carries, when it carries one.</param>
/// <param name="Year">A four-digit year in brackets or set off by dots or spaces.</param>
public sealed record ParsedFileName(string? Title, string? Show, int? Season, int? Episode, string? Year);

/// <summary>
/// Reads a file name the way the Match tool starts from it (universal editor v3, WP4): the show, season and episode of a TV file
/// (<c>Show.S02E05.Title.mkv</c>, <c>Show - 2x05 - Title.mp4</c>, <c>Show Season 2 Episode 5.mkv</c>), or the plain title of anything
/// else, with release tags (1080p, WEB-DL, x264 and the like), brackets and a year taken out. A read of text only: it never opens the file
/// and never decides a match, it only gives the search somewhere sensible to begin.
/// </summary>
public static class EditorFileNameParser
{
    private static readonly Regex SeasonEpisode = new(
        @"(?<![A-Za-z0-9])(?:S(?<s>\d{1,2})[ ._-]?E(?<e>\d{1,3})(?:[ ._-]?E?\d{1,3})?|(?<s2>\d{1,2})x(?<e2>\d{1,3})(?![0-9]))(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SeasonWordEpisodeWord = new(
        @"Season[ ._-]*(?<s>\d{1,2})[ ._-]*(?:Episode|Ep|E)[ ._-]*(?<e>\d{1,3})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Year = new(
        @"(?:[\(\[]|(?<=[ ._-]))(?<y>(?:19|20)\d{2})(?:[\)\]]|(?=[ ._-]|$))",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Brackets = new(@"[\[\(][^\]\)]*[\]\)]", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ReleaseTags = new(
        @"(?<![A-Za-z0-9])(?:2160p|1080p|1080i|720p|576p|480p|4k|uhd|hdr10?|dv|bluray|blu-ray|bdrip|brrip|web-?dl|web-?rip|webrip|hdtv|dvdrip|remux|x264|x265|h\.?264|h\.?265|hevc|avc|aac|ac3|dts|flac|mp3|atmos|proper|repack|extended|unrated|internal)(?![A-Za-z0-9]).*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SeasonFolder = new(
        @"^(?:season|series|staffel|saison|s)[ ._-]*\d{1,2}$|^specials?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Parses a file name (with or without its folders). <paramref name="isTv"/> turns on the show, season and episode reading.</summary>
    public static ParsedFileName Parse(string? filePath, bool isTv)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new ParsedFileName(null, null, null, null, null);
        }

        var segments = filePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var name = Path.GetFileNameWithoutExtension(segments[^1]);
        var folders = segments.Length > 1 ? segments[..^1] : [];

        string? year = null;
        if (Year.Match(name) is { Success: true } yearMatch)
        {
            year = yearMatch.Groups["y"].Value;
        }

        if (!isTv)
        {
            return new ParsedFileName(Clean(StripYear(StripTags(name))), null, null, null, year);
        }

        int? season = null;
        int? episode = null;
        string? show = null;
        string? title = null;
        var match = SeasonEpisode.Match(name);
        var numbered = match.Success ? match : SeasonWordEpisodeWord.Match(name);
        if (numbered.Success)
        {
            season = ReadNumber(numbered, "s", "s2");
            episode = ReadNumber(numbered, "e", "e2");
            show = Clean(StripYear(name[..numbered.Index]));
            title = Clean(StripTags(name[(numbered.Index + numbered.Length)..]));
        }
        else
        {
            title = Clean(StripYear(StripTags(name)));
        }

        if (string.IsNullOrWhiteSpace(show))
        {
            show = ShowFromFolders(folders, out var folderSeason);
            season ??= folderSeason;
        }
        else if (season is null)
        {
            _ = ShowFromFolders(folders, out var folderSeason);
            season = folderSeason;
        }

        return new ParsedFileName(string.IsNullOrWhiteSpace(title) ? null : title, string.IsNullOrWhiteSpace(show) ? null : show, season, episode, year);
    }

    private static int? ReadNumber(Match match, params string[] groups)
    {
        foreach (var group in groups)
        {
            if (match.Groups[group] is { Success: true } hit
                && int.TryParse(hit.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }
        }

        return null;
    }

    // The show is the folder above the season folder ("Show/Season 2/file"); a season folder also tells the season.
    private static string? ShowFromFolders(string[] folders, out int? season)
    {
        season = null;
        if (folders.Length == 0)
        {
            return null;
        }

        var last = folders[^1];
        if (SeasonFolder.IsMatch(last.Trim()))
        {
            var digits = new string(last.Where(char.IsAsciiDigit).ToArray());
            season = digits.Length > 0 && int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : last.StartsWith("special", StringComparison.OrdinalIgnoreCase) ? 0 : null;
            return folders.Length > 1 ? Clean(StripYear(StripTags(folders[^2]))) : null;
        }

        return Clean(StripYear(StripTags(last)));
    }

    private static string StripTags(string text) => ReleaseTags.Replace(Brackets.Replace(text, " "), string.Empty);

    private static string StripYear(string text) => Year.Replace(text, " ");

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var spaced = Regex.Replace(text.Replace('.', ' ').Replace('_', ' '), @"\s+", " ").Trim(' ', '-', '–', '—');
        return spaced.Length == 0 ? null : spaced;
    }
}
