namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Library-tag validation shared by the editor Details section and Editing Mode pencils,
/// so a tag is accepted or refused the same way wherever it is typed.
/// </summary>
public static class MediaEditorTagRules
{
    public const int MaxTags = 30;
    public const int MaxTagLength = 64;

    /// <summary>Splits on comma, semicolon or pipe, trims, de-duplicates and re-joins with "; ".</summary>
    public static string NormalizeLibraryTags(string value, out string? error)
    {
        var tags = string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split([',', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        if (tags.Length > MaxTags)
        {
            error = "Library tags are limited to 30 values.";
            return string.Empty;
        }
        if (tags.Any(tag => tag.Length > MaxTagLength))
        {
            error = "Each library tag must be 64 characters or fewer.";
            return string.Empty;
        }
        error = null;
        return string.Join("; ", tags);
    }

    public static bool IsLibraryTagsKey(string key) =>
        string.Equals(key, "custom_tags", StringComparison.OrdinalIgnoreCase);
}
