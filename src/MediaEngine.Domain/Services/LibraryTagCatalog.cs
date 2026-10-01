namespace MediaEngine.Domain.Services;

/// <summary>
/// Shared bounds and serialization rules for library-wide custom tags.
/// </summary>
public static class LibraryTagCatalog
{
    public const int MaximumTags = 30;
    public const int MaximumTagLength = 64;

    private static readonly char[] Separators = [',', ';', '|', '\r', '\n'];

    public static bool TryNormalize(
        IEnumerable<string?>? values,
        out IReadOnlyList<string> normalized,
        out string? error)
    {
        var tags = (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (tags.Count > MaximumTags)
        {
            normalized = [];
            error = $"Use at most {MaximumTags} library tags.";
            return false;
        }

        if (tags.Any(tag => tag.Length > MaximumTagLength))
        {
            normalized = [];
            error = $"Each library tag must be {MaximumTagLength} characters or fewer.";
            return false;
        }

        if (tags.Any(tag => tag.IndexOfAny(Separators) >= 0))
        {
            normalized = [];
            error = "A library tag cannot contain a tag separator (comma, semicolon, pipe, or line break).";
            return false;
        }

        normalized = tags;
        error = null;
        return true;
    }

    public static bool TryNormalizeDisplayValue(
        string? value,
        out string normalized,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            normalized = string.Empty;
            error = null;
            return true;
        }

        if (!TryNormalize(value.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                out var tags,
                out error))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = string.Join("; ", tags);
        return true;
    }

    public static IReadOnlyList<string> ParseDisplayValue(string? value) =>
        TryNormalizeDisplayValue(value, out var normalized, out _)
            ? normalized.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
}
