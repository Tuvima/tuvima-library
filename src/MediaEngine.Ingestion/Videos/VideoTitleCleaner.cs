using System.Text;
using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Ingestion.Videos;

/// <summary>A cleaned name: the title to show, a number read from the start of the name, and a YouTube video ID found in it.</summary>
public sealed record CleanedVideoName(string Title, int? Number, string? YoutubeId);

/// <summary>
/// Turns a video file or folder name into a readable title using the rules in <c>config/videos.json</c>:
/// a <c>[youtubeid]</c> suffix, separators, release tags and a leading number.
/// </summary>
public static partial class VideoTitleCleaner
{
    [GeneratedRegex(@"\[(?<id>[A-Za-z0-9_-]{11})\]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketedIdRegex();

    [GeneratedRegex(@"^\s*(?:(?:lesson|part|episode|ep|chapter|lecture|video|day|week|module|tutorial|session)\s*)?(?<n>\d{1,3})\s*(?:[-–—:.)_]+\s*|\s+)(?=\S)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex LeadingNumberRegex();

    [GeneratedRegex(@"^\s*(?:(?:lesson|part|episode|ep|chapter|lecture|video|day|week|module|tutorial|session)\s*)?(?<n>\d{1,3})\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WholeNumberRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Finds a YouTube video ID written as <c>[abc123XYZ_-]</c>. Eleven plain letters such as <c>[Documentary]</c> are
    /// not an ID; a real one carries a digit, a dash, an underscore or a capital letter inside it.
    /// </summary>
    public static string? FindYoutubeId(string stem)
    {
        string? found = null;
        foreach (Match match in BracketedIdRegex().Matches(stem))
        {
            var id = match.Groups["id"].Value;
            if (LooksLikeYoutubeId(id))
            {
                found = id;
            }
        }

        return found;
    }

    public static bool LooksLikeYoutubeId(string id)
    {
        if (id.Length != 11)
        {
            return false;
        }

        var hasMixed = false;
        for (var i = 0; i < id.Length; i++)
        {
            var c = id[i];
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-'))
            {
                return false;
            }

            if (char.IsDigit(c) || c is '_' or '-' || (i > 0 && char.IsUpper(c)))
            {
                hasMixed = true;
            }
        }

        return hasMixed;
    }

    /// <summary>Cleans a name. A folder name keeps its leading number (<paramref name="stripLeadingNumber"/> false).</summary>
    public static CleanedVideoName Clean(string stem, VideoTitleCleaningConfiguration rules, bool stripLeadingNumber = true)
    {
        var youtubeId = FindYoutubeId(stem);
        var text = stem;
        if (rules.StripYoutubeIdSuffix && youtubeId is not null)
        {
            text = text.Replace($"[{youtubeId}]", " ", StringComparison.Ordinal);
        }

        text = ReplaceSeparators(text, rules.WordSeparators);
        text = WhitespaceRegex().Replace(text, " ").Trim(' ', '-', '_', '.');
        text = StripTrailingTags(text, rules.RemoveTags);

        int? number = null;
        if (stripLeadingNumber && rules.StripLeadingNumber)
        {
            var match = LeadingNumberRegex().Match(text);
            if (match.Success && int.TryParse(match.Groups["n"].Value, out var parsed))
            {
                var rest = text[match.Length..].Trim();
                if (rest.Length > 0)
                {
                    number = parsed;
                    text = rest;
                }
            }
        }

        if (number is null)
        {
            // "Lecture 10" and "42" are all number: the title stays, the number still gives the order.
            var match = (stripLeadingNumber ? WholeNumberRegex() : LeadingNumberRegex()).Match(text);
            if (!match.Success && !stripLeadingNumber)
            {
                match = WholeNumberRegex().Match(text);
            }

            if (match.Success && int.TryParse(match.Groups["n"].Value, out var parsed))
            {
                number = parsed;
            }
        }

        if (text.Length == 0)
        {
            text = stem.Trim();
        }

        return new CleanedVideoName(text, number, youtubeId);
    }

    /// <summary>The number at the start of a name ("03 - Loops" gives 3), or <see langword="null"/>.</summary>
    public static int? LeadingNumber(string name, VideoTitleCleaningConfiguration rules) =>
        Clean(name, rules, stripLeadingNumber: false).Number;

    private static string ReplaceSeparators(string text, IReadOnlyList<string> separators)
    {
        // A name that already has spaces keeps its full stops ("Dr. Strangelove"); underscores are always spaces.
        var hasSpaces = text.Contains(' ');
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var s = ch.ToString();
            if (separators.Contains(s) && (ch != '.' || !hasSpaces))
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static string StripTrailingTags(string text, IReadOnlyList<string> tags)
    {
        var tagSet = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1)
        {
            var last = tokens[^1].Trim('[', ']', '(', ')', '-');
            if (tagSet.Contains(last))
            {
                tokens.RemoveAt(tokens.Count - 1);
                continue;
            }

            break;
        }

        return string.Join(' ', tokens).TrimEnd(' ', '-', '[', '(');
    }
}
