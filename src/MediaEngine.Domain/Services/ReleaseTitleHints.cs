using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Domain.Services;

/// <summary>Search hints only. Never rewrites embedded titles or source names.</summary>
public static class ReleaseTitleHints
{
    /// <summary>Tags that are also ordinary title words ("Magnum Opus"); they only count after " - " or "[".</summary>
    private static readonly HashSet<string> AmbiguousTags = new(StringComparer.OrdinalIgnoreCase) { "opus" };

    /// <summary>Evidence the video naming list does not carry: older codecs and audio formats seen in release names.</summary>
    private static readonly string[] BuiltInTags =
    [
        @"VC-?1", @"H[.]?26[45]", @"BDRemux", @"REMUX", @"TrueHD", @"DTS(?:-HD)?", @"DDP\d", @"WEB-DL", @"BluRay",
    ];

    private static readonly object Gate = new();
    private static Regex TechnicalSuffix = BuildTechnicalSuffix(new VideoTitleCleaningConfiguration().RemoveTags);

    /// <summary>
    /// Replaces the format tags the cleaner recognises. The tags come from <c>remove_tags</c> in
    /// <c>config/videos.json</c> so Videos folders and lookup names share one list.
    /// </summary>
    public static void UseTags(IEnumerable<string>? tags)
    {
        var list = (tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        lock (Gate)
        {
            TechnicalSuffix = BuildTechnicalSuffix(list.Count == 0 ? new VideoTitleCleaningConfiguration().RemoveTags : list);
        }
    }

    private static Regex BuildTechnicalSuffix(IEnumerable<string> tags)
    {
        var plain = new List<string>(BuiltInTags);
        var ambiguous = new List<string>();
        foreach (var tag in tags)
        {
            var trimmed = tag.Trim();
            (AmbiguousTags.Contains(trimmed) ? ambiguous : plain).Add(Regex.Escape(trimmed));
        }

        var strong = $@"(?:\s+[-–]\s+|[ ._]+|\s*\[)(?=(?:{string.Join('|', plain.Distinct())})(?:\b|[ ._-]))";
        var weak = ambiguous.Count == 0
            ? ""
            : $@"|(?:\s+[-–]\s+|\s*\[)(?=(?:{string.Join('|', ambiguous)})(?:\b|[ ._-]))";
        return new Regex($@"(?i)(?:{strong}{weak})[^\r\n]*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    private static readonly Regex YearSuffix = new(
        @"^(?<title>.+?)\s*[([](?<year>(?:18|19|20)\d{2})[)\]]\s*$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // "Movie.2019" with no brackets counts as a year only once a format tag followed it.
    private static readonly Regex BareYearSuffix = new(
        @"^(?<title>.+?)[ ._](?<year>(?:18|19|20)\d{2})$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static (string? Title, string? Year) Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (value, null);
        }
        var trimmed = value.Trim();
        var stripped = TechnicalSuffix.Replace(trimmed, "");
        var cleaned = stripped.TrimEnd(' ', '-', '.', '_');
        var match = YearSuffix.Match(cleaned);
        // Only dotted or underscored release names ("Movie.2019.1080p"): "Blade Runner 2049 1080p" keeps its title.
        if (!match.Success && stripped.Length < trimmed.Length && !cleaned.Contains(' '))
        {
            match = BareYearSuffix.Match(cleaned);
        }
        var year = match.Success ? match.Groups["year"].Value : null;
        if (match.Success)
        {
            cleaned = match.Groups["title"].Value.Trim();
        }
        // Separators in release filenames are normalized only with technical or
        // year evidence; an ordinary source title retains meaningful punctuation.
        if (year is not null || !string.Equals(cleaned, value.Trim(), StringComparison.Ordinal))
        {
            cleaned = Regex.Replace(cleaned, @"(?<=\p{L})[._](?=\p{L})", " ");
        }
        return (cleaned.Length == 0 ? value.Trim() : cleaned, year);
    }
}
