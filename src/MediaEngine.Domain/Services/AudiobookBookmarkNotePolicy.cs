namespace MediaEngine.Domain.Services;

/// <summary>Shared normalization and length policy for audiobook bookmark notes.</summary>
public static class AudiobookBookmarkNotePolicy
{
    public const int MaximumLength = 200;

    /// <summary>The longest optional description a bookmark can carry (Videos spec D43).</summary>
    public const int MaximumDescriptionLength = 4000;

    /// <summary>
    /// Trims outer whitespace, maps an empty note to null, and rejects notes longer
    /// than the HTML maxlength contract measured in UTF-16 code units.
    /// </summary>
    public static bool TryNormalize(string? value, out string? normalized)
    {
        if (value is not null && value.Length > MaximumLength)
        {
            normalized = null;
            return false;
        }

        var candidate = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        normalized = candidate;
        return true;
    }

    /// <summary>The same rules for the longer description: trimmed, empty becomes null, at most 4,000 UTF-16 code units.</summary>
    public static bool TryNormalizeDescription(string? value, out string? normalized)
    {
        if (value is not null && value.Length > MaximumDescriptionLength)
        {
            normalized = null;
            return false;
        }

        normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return true;
    }
}
