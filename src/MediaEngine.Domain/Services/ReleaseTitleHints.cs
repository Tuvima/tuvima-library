using System.Text.RegularExpressions;

namespace MediaEngine.Domain.Services;

/// <summary>Search hints only. Never rewrites embedded titles or source names. The noise words and the year rule come from <c>config/filename_cleaning.json</c>.</summary>
public static class ReleaseTitleHints
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    private sealed record Compiled(FilenameCleaningRules Rules, Regex TechnicalSuffix, Regex YearSuffix, Regex BareYearSuffix);

    private static readonly object Gate = new();
    private static Compiled? _compiled;

    private static Compiled For(FilenameCleaningRules rules)
    {
        lock (Gate)
        {
            if (_compiled is { } built && ReferenceEquals(built.Rules, rules))
            {
                return built;
            }

            var year = rules.Configuration.Year.Pattern;
            var strong = $@"(?:\s+[-–]\s+|[ ._]+|\s*\[)(?=(?:{rules.PlainFragments})(?:\b|[ ._-]))";
            var weak = rules.AmbiguousFragments.Length == 0
                ? ""
                : $@"|(?:\s+[-–]\s+|\s*\[)(?=(?:{rules.AmbiguousFragments})(?:\b|[ ._-]))";
            _compiled = new Compiled(
                rules,
                new Regex($@"(?i)(?:{strong}{weak})[^\r\n]*$", RegexOptions.CultureInvariant, Timeout),
                new Regex($@"^(?<title>.+?)\s*[([](?<year>{year})[)\]]\s*$", RegexOptions.CultureInvariant, Timeout),
                // "Movie.2019" with no brackets counts as a year only once a format tag followed it.
                new Regex($@"^(?<title>.+?)[ ._](?<year>{year})$", RegexOptions.CultureInvariant, Timeout));
            return _compiled;
        }
    }

    public static (string? Title, string? Year) Parse(string? value) => Parse(value, FilenameCleaner.Rules);

    public static (string? Title, string? Year) Parse(string? value, FilenameCleaningRules rules)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (value, null);
        }
        var compiled = For(rules);
        var trimmed = value.Trim();
        var stripped = compiled.TechnicalSuffix.Replace(trimmed, "");
        var cleaned = stripped.TrimEnd(' ', '-', '.', '_');
        var match = compiled.YearSuffix.Match(cleaned);
        // Only dotted or underscored release names ("Movie.2019.1080p"): "Blade Runner 2049 1080p" keeps its title.
        if (!match.Success && stripped.Length < trimmed.Length && !cleaned.Contains(' '))
        {
            match = compiled.BareYearSuffix.Match(cleaned);
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
