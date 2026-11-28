using System.Globalization;
using System.Text;

namespace MediaEngine.Providers.Services;

internal static class RetailTextSimilarity
{
    public static double ComputeWordOverlap(string a, string b)
    {
        var aWords = Tokenize(a);
        var bWords = Tokenize(b);

        if (aWords.Count == 0 || bWords.Count == 0)
        {
            return 0.0;
        }

        var coverage = (double)aWords.Count(w => bWords.Contains(w)) / aWords.Count;
        var precision = (double)bWords.Count(w => aWords.Contains(w)) / bWords.Count;

        if (coverage + precision == 0)
        {
            return 0.0;
        }

        return 2 * coverage * precision / (coverage + precision);
    }

    public static bool AreEquivalentNames(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            NormalizeComparableText(left),
            NormalizeComparableText(right),
            StringComparison.OrdinalIgnoreCase);
    }

    public static HashSet<string> Tokenize(string text)
    {
        // "&" and "and" are the same word: "Prince & The Revolution" is "Prince and The Revolution".
        return [.. StripDiacritics(text).Replace("&", " and ", StringComparison.Ordinal).ToLowerInvariant()
            .Split([' ', ',', '.', '-', ':', ';', '\'', '"', '(', ')', '[', ']'],
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)];
    }

    public static string StripDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// THE canonical comparable-text normalization for retail and Wikidata matching.
    /// Strips diacritics, maps <c>&amp;</c> to " and ", lowercases, and collapses every
    /// run of non-alphanumeric characters (including whitespace) to a single space.
    /// Stage 1 retail matching (<see cref="RetailMatchScoringService"/>, <c>RetailMatchWorker</c>)
    /// and Stage 2 Wikidata bridge resolution (<c>WikidataBridgeWorker</c>) both depend on
    /// using this exact implementation — if either stage normalizes text differently, the
    /// same title can compare equal in one stage and unequal in the other, breaking
    /// pipeline consistency. Do not reintroduce a private copy of this method elsewhere.
    /// </summary>
    public static string NormalizeComparableText(string text)
    {
        var chars = StripDiacritics(text)
            .Replace("&", " and ", StringComparison.Ordinal)
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();

        return string.Join(' ', new string(chars)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
