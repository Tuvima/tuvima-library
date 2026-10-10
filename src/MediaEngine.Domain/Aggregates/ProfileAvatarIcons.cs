namespace MediaEngine.Domain.Aggregates;

/// <summary>
/// The built-in illustrated avatar icons a profile can wear instead of an initial or a photo.
/// The Dashboard draws each key; the Engine only checks that a saved key is one of these.
/// </summary>
public static class ProfileAvatarIcons
{
    /// <summary>Every supported icon key, in the order the editor shows them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "cat", "fox", "owl", "bear", "panda", "penguin",
        "robot", "rocket", "planet", "moon", "crown", "ghost",
    ];

    /// <summary>Returns <see langword="true"/> when <paramref name="key"/> is a supported icon key.</summary>
    public static bool IsValid(string? key) =>
        key is not null && All.Contains(key, StringComparer.Ordinal);

    /// <summary>
    /// Trims and lower-cases a requested key. A missing or blank key means "no icon" and returns <see langword="null"/>;
    /// an unknown key throws <see cref="ArgumentException"/>.
    /// </summary>
    public static string? Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var normalized = key.Trim().ToLowerInvariant();
        if (!IsValid(normalized))
        {
            throw new ArgumentException("Choose one of the built-in avatar icons.");
        }

        return normalized;
    }
}
