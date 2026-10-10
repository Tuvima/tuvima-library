using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaEngine.Domain.Services;

/// <summary>
/// Reads the whole episode range out of a TV filename stem, so a single file that
/// holds several episodes (for example <c>Show S01E01E02</c>) reports every
/// episode it covers rather than only the first.
/// </summary>
/// <remarks>
/// Recognised forms (case-insensitive, series prefix optional for SxxExx):
/// <c>S01E01E02</c>, <c>S01E01-E02</c>, <c>S01E01-02</c>, <c>S01E01E02E03</c>, <c>1x01-02</c>.
/// Only unbroken ascending runs count: <c>S01E01E03</c> (gap) or a run longer than
/// <see cref="MaxEpisodesPerFile"/> (typo such as <c>E01E200</c>) is reported as an
/// unresolved range so callers can keep the file in Review instead of guessing.
/// </remarks>
public static partial class EpisodeRangeParser
{
    /// <summary>Longest run of episodes a single file may cover and still be linked automatically.</summary>
    public const int MaxEpisodesPerFile = 6;

    // series is optional so a leading "S01E01E02 - Title" stem also parses.
    [GeneratedRegex(
        @"^(?:.+?\s*[.\-_ ]*)?[Ss](?<season>\d{1,2})\s*[Ee](?<first>\d{1,4})(?<tail>(?:\s*-\s*[Ee]\d{1,4}|\s*[Ee]\d{1,4}|-\d{1,4}(?![\dA-Za-z]))*)",
        RegexOptions.Compiled)]
    private static partial Regex SxxExxRange();

    [GeneratedRegex(
        @"^.+?\s*[.\-_ ]+(?<season>\d{1,2})[Xx](?<first>\d{1,4})(?<tail>(?:-\d{1,4}(?![\dA-Za-z]))*)",
        RegexOptions.Compiled)]
    private static partial Regex NxNnRange();

    [GeneratedRegex(@"(?<dash>-)?\s*[Ee]?(?<n>\d{1,4})", RegexOptions.Compiled)]
    private static partial Regex TailToken();

    /// <summary>
    /// Parses <paramref name="text"/> (a filename stem, extension removed). Returns
    /// <c>null</c> when no season/episode pattern is present.
    /// </summary>
    public static EpisodeRange? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = SxxExxRange().Match(text);
        if (!match.Success)
        {
            match = NxNnRange().Match(text);
        }

        if (!match.Success)
        {
            return null;
        }

        var season = int.Parse(match.Groups["season"].Value, CultureInfo.InvariantCulture);
        var first = int.Parse(match.Groups["first"].Value, CultureInfo.InvariantCulture);
        var tail = match.Groups["tail"].Value;
        var baseEnd = match.Groups["tail"].Index;

        if (tail.Length == 0)
        {
            return new EpisodeRange(season, first, first, false, baseEnd);
        }

        var last = first;
        foreach (Match token in TailToken().Matches(tail))
        {
            var n = int.Parse(token.Groups["n"].Value, CultureInfo.InvariantCulture);
            var isDash = token.Groups["dash"].Success;

            // A dash range may jump forward; a chained E02E03 must be the very next episode.
            var contiguous = isDash ? n > last : n == last + 1;
            if (!contiguous || n - first + 1 > MaxEpisodesPerFile)
            {
                // Gap, reversed, repeated or oversized: not an unbroken range.
                return new EpisodeRange(season, first, first, true, baseEnd);
            }

            last = n;
        }

        return new EpisodeRange(season, first, last, false, match.Index + match.Length);
    }
}

/// <summary>
/// Season and episode span read from a filename.
/// </summary>
/// <param name="Season">Season number.</param>
/// <param name="FirstEpisode">First (or only) episode number.</param>
/// <param name="LastEpisode">Last episode number; equals <paramref name="FirstEpisode"/> for a single episode.</param>
/// <param name="IsUnresolvedRange">True when the name hints at several episodes but not as an unbroken, in-limit run.</param>
/// <param name="EndIndex">Index in the parsed text just after the episode designator.</param>
public sealed record EpisodeRange(int Season, int FirstEpisode, int LastEpisode, bool IsUnresolvedRange, int EndIndex)
{
    /// <summary>True when the file covers more than one episode.</summary>
    public bool IsRange => LastEpisode > FirstEpisode;

    /// <summary>Number of episodes covered.</summary>
    public int Count => LastEpisode - FirstEpisode + 1;
}
