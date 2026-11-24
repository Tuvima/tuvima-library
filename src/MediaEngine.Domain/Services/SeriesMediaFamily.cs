namespace MediaEngine.Domain.Services;

/// <summary>
/// Which media types share one Series. A book and an audiobook of the same Series are one Series (Shy, 2026-11-24,
/// decision 227A); every other media type keeps its own Series (a TV show or a film line is not the book Series it
/// is based on).
/// </summary>
public static class SeriesMediaFamily
{
    /// <summary>The media type a shared Books-and-Audiobooks Series is stored under.</summary>
    public const string BookFormats = "Books";

    /// <summary>The media type a Series of <paramref name="mediaType"/> is stored under (Audiobooks become Books).</summary>
    public static string Normalize(string? mediaType)
    {
        var trimmed = mediaType?.Trim() ?? string.Empty;
        return string.Equals(trimmed, "Audiobooks", StringComparison.OrdinalIgnoreCase) ? BookFormats : trimmed;
    }

    /// <summary>Whether two media types belong to the same Series family.</summary>
    public static bool Same(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>Every media type name stored under the family of <paramref name="mediaType"/> (for SQL <c>IN</c> lists).</summary>
    public static string[] Members(string? mediaType) =>
        string.Equals(Normalize(mediaType), BookFormats, StringComparison.OrdinalIgnoreCase)
            ? ["Books", "Audiobooks"]
            : [mediaType?.Trim() ?? string.Empty];
}
