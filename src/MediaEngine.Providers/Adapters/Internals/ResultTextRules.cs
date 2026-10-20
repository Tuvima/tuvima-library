using System.Globalization;
using System.Text.RegularExpressions;
using MediaEngine.Providers.Services;

namespace MediaEngine.Providers.Adapters;

/// <summary>
/// Text comparisons shared by result selection, candidate ranking and claim rules. Behaviour is
/// deliberately the one the adapter always used, so that moving it out changes nothing.
/// </summary>
internal static class ResultTextRules
{
    private static readonly char[] WordSeparators = [' ', ',', '.', '-', ':', ';', '\'', '"', '(', ')', '[', ']'];

    private static readonly string[] ForeignMarkers =
    [
        " der ", " die ", " das ", " und ", " eine ", " einem ", " einen ",
        " von ", " mit ", " nicht ", " fur ", " uber ", " ist ", " sich ",
    ];

    /// <summary>Lower-cases, strips accents, reads "&amp;" as "and" and turns other punctuation into single spaces.</summary>
    public static string NormalizeEquivalentText(string value)
    {
        var chars = RetailTextSimilarity.StripDiacritics(value)
            .Replace("&", " and ", StringComparison.Ordinal)
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static bool AreEquivalentText(string left, string right) =>
        string.Equals(NormalizeEquivalentText(left), NormalizeEquivalentText(right), StringComparison.Ordinal);

    public static bool AreEquivalentOrdinals(string left, string right)
    {
        if (int.TryParse(ExtractLeadingDigits(left), out var leftNumber)
            && int.TryParse(ExtractLeadingDigits(right), out var rightNumber))
        {
            return leftNumber == rightNumber;
        }

        return string.Equals(left.TrimStart('0'), right.TrimStart('0'), StringComparison.OrdinalIgnoreCase)
            || string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static string ExtractLeadingDigits(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var match = Regex.Match(value.Trim(), @"^\D*0*(\d+)");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    public static int? ExtractYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, @"(?<!\d)(19|20)\d{2}(?!\d)");
        return match.Success && int.TryParse(match.Value, out var year) ? year : null;
    }

    public static int? ParseLeadingInt(string? value) =>
        int.TryParse(ExtractLeadingDigits(value ?? string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    /// <summary>True when the text carries at least three common German function words.</summary>
    public static bool LooksNonEnglish(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = $" {RetailTextSimilarity.StripDiacritics(value).ToLowerInvariant()} ";
        return ForeignMarkers.Count(marker => normalized.Contains(marker, StringComparison.Ordinal)) >= 3;
    }

    /// <summary>Word-overlap similarity (0.0-1.0): the harmonic mean of coverage and precision of the word sets.</summary>
    public static double ComputeWordOverlap(string query, string candidate)
    {
        var queryWords = Words(query);
        var candidateWords = Words(candidate);
        if (queryWords.Count == 0 || candidateWords.Count == 0)
        {
            return 0.0;
        }

        var coverage = (double)queryWords.Count(word => candidateWords.Contains(word)) / queryWords.Count;
        var precision = (double)candidateWords.Count(word => queryWords.Contains(word)) / candidateWords.Count;
        return coverage + precision == 0 ? 0.0 : 2 * coverage * precision / (coverage + precision);
    }

    private static HashSet<string> Words(string text) =>
        RetailTextSimilarity.StripDiacritics(text).ToLowerInvariant()
            .Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 2)
            .ToHashSet();
}
