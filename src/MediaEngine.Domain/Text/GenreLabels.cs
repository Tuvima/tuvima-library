namespace MediaEngine.Domain.Text;

/// <summary>
/// Turns one stored genre value into separate genre labels. Some providers send a whole category list as one
/// comma-, semicolon- or slash-joined string; every place that counts or shows genres splits it first so each genre
/// is one short label and the same genre is never counted under two spellings.
/// </summary>
public static class GenreLabels
{
    private static readonly char[] Separators = [',', ';', '|', '\n'];

    /// <summary>The distinct, trimmed, lower-cased labels in <paramref name="value"/>; empty when it has none.</summary>
    public static IReadOnlyList<string> Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Like <see cref="Split"/> but keeps each label's own capitalisation, for storing and showing genres. Distinct ignoring case.</summary>
    public static IReadOnlyList<string> SplitKeepingCase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
