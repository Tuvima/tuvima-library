namespace MediaEngine.Domain.Services;

/// <summary>Shared normalization and length policy for audiobook bookmark notes.</summary>
public static class AudiobookBookmarkNotePolicy
{
    public const int MaximumLength = 200;

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
}
