using System.Text.RegularExpressions;

namespace MediaEngine.Domain.Services;

/// <summary>Search hints only. Never rewrites embedded titles or source names.</summary>
public static class ReleaseTitleHints
{
    private static readonly Regex TechnicalSuffix = new(
        @"(?i)(?:\s+[-–]\s+|[ ._]+|\s*\[)(?=(?:2160p|1080[pi]|720p|480p|VC-?1|HEVC|x26[45]|H[.]?26[45]|AV1|BluRay|WEB-DL|WEBRip|BDRemux|REMUX|TrueHD|DTS(?:-HD)?|DDP\d)(?:\b|[ ._-]))[^\r\n]*$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex YearSuffix = new(
        @"^(?<title>.+?)\s*[([](?<year>(?:18|19|20)\d{2})[)\]]\s*$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static (string? Title, string? Year) Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (value, null);
        var cleaned = TechnicalSuffix.Replace(value.Trim(), "").TrimEnd(' ', '-', '.', '_');
        var match = YearSuffix.Match(cleaned);
        var year = match.Success ? match.Groups["year"].Value : null;
        if (match.Success) cleaned = match.Groups["title"].Value.Trim();
        // Separators in release filenames are normalized only with technical or
        // year evidence; an ordinary source title retains meaningful punctuation.
        if (year is not null || !string.Equals(cleaned, value.Trim(), StringComparison.Ordinal))
            cleaned = Regex.Replace(cleaned, @"(?<=\p{L})[._](?=\p{L})", " ");
        return (cleaned.Length == 0 ? value.Trim() : cleaned, year);
    }
}
