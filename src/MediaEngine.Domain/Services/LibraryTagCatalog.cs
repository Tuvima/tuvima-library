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

    /// <summary>
    /// Applies one batch change to a title's tags (details overlay spec D8): <c>add</c> keeps what is there and adds the new tags,
    /// <c>remove</c> takes the named tags away (tags not present are ignored), <c>replace</c> sets exactly the new tags.
    /// Returns false with the reason when a tag is not acceptable. The result is in the normal display form.
    /// </summary>
    public static bool TryApplyDisplayChange(
        string? existing,
        string? change,
        string mode,
        out string result,
        out string? error)
    {
        result = string.Empty;
        if (!TryNormalizeDisplayValue(change, out var normalizedChange, out error))
        {
            return false;
        }

        if (string.Equals(mode, "replace", StringComparison.OrdinalIgnoreCase))
        {
            result = normalizedChange;
            return true;
        }

        var current = ParseDisplayValue(existing);
        var named = ParseDisplayValue(normalizedChange);
        IEnumerable<string> merged;
        if (string.Equals(mode, "add", StringComparison.OrdinalIgnoreCase))
        {
            merged = current.Concat(named);
        }
        else if (string.Equals(mode, "remove", StringComparison.OrdinalIgnoreCase))
        {
            merged = current.Where(tag => !named.Contains(tag, StringComparer.OrdinalIgnoreCase));
        }
        else
        {
            error = $"Unknown change mode '{mode}'.";
            return false;
        }

        return TryNormalizeDisplayValue(string.Join("; ", merged), out result, out error);
    }
}
