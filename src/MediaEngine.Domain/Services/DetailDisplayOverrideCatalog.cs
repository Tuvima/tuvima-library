using System.Globalization;

namespace MediaEngine.Domain.Services;

/// <summary>
/// Presentation-only scalar fields that may be overridden on a work. These
/// values never participate in canonical identity or structural placement.
/// </summary>
public static class DetailDisplayOverrideCatalog
{
    private static readonly string[] Keys =
    [
        "title", "original_title", "tagline", "subtitle", "description", "genre",
        "year", "release_date", "air_date", "first_air_date", "publication_date",
        "original_publication_date", "edition_release_date", "runtime", "duration",
        "rating", "content_rating", "language", "sort_title", "custom_tags",
    ];

    private static readonly HashSet<string> KeySet = new(Keys, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> AllowedKeys => Keys;

    public static bool IsAllowed(string? key) => !string.IsNullOrWhiteSpace(key) && KeySet.Contains(key.Trim());

    public static bool TryValidateValue(string key, string? value, out string normalized, out string? error)
    {
        normalized = (value ?? string.Empty).Trim();
        error = null;
        if (!IsAllowed(key))
        {
            error = $"Unsupported display override field '{key}'.";
            return false;
        }

        if (normalized.Length == 0)
        {
            return true; // Empty values revert scalars; custom_tags uses an explicit empty sentinel.
        }

        var normalizedKey = key.Trim().ToLowerInvariant();
        if (normalizedKey == "custom_tags")
        {
            if (!LibraryTagCatalog.TryNormalizeDisplayValue(normalized, out var normalizedTags, out error))
            {
                return false;
            }

            normalized = normalizedTags;
            return true;
        }

        var maxLength = normalizedKey == "description" ? 10000 : 500;
        if (normalized.Length > maxLength)
        {
            error = $"Display override '{key}' must be {maxLength} characters or fewer.";
            return false;
        }

        if (normalizedKey is "year")
        {
            if (!int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
                || year is < 1 or > 9999)
            {
                error = "Year must be a four-digit calendar year.";
                return false;
            }

            normalized = year.ToString(CultureInfo.InvariantCulture);
        }
        else if (normalizedKey is "release_date" or "air_date" or "first_air_date" or "publication_date"
            or "original_publication_date" or "edition_release_date")
        {
            if (!DateOnly.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                error = $"'{key}' must be a valid date.";
                return false;
            }

            normalized = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        else if (normalizedKey is "runtime" or "duration")
        {
            if (!int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
                || minutes is < 0 or > 100000)
            {
                error = $"'{key}' must be a non-negative number of minutes.";
                return false;
            }

            normalized = minutes.ToString(CultureInfo.InvariantCulture);
        }
        else if (normalizedKey == "rating")
        {
            if (!decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var rating)
                || rating is < 0 or > 10)
            {
                error = "Rating must be a number from 0 through 10.";
                return false;
            }

            normalized = rating.ToString("0.##", CultureInfo.InvariantCulture);
        }

        return true;
    }

    /// <summary>Applies validated editor values; blank scalars revert while blank custom_tags remains authoritative.</summary>
    public static IReadOnlyList<string> ApplyChanges(
        IDictionary<string, string> current,
        IReadOnlyDictionary<string, string> updates)
    {
        var updatedKeys = new List<string>();
        foreach (var (key, value) in updates)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }
            if (!TryValidateValue(key, value, out var normalized, out var error))
            {
                throw new ArgumentException(error, nameof(updates));
            }

            var normalizedKey = key.Trim().ToLowerInvariant();
            foreach (var existingKey in current.Keys
                         .Where(existing => string.Equals(existing, normalizedKey, StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(existing, normalizedKey, StringComparison.Ordinal))
                         .ToList())
            {
                current.Remove(existingKey);
            }

            if (normalized.Length == 0)
            {
                if (string.Equals(normalizedKey, "custom_tags", StringComparison.OrdinalIgnoreCase))
                {
                    // Keep an explicit empty value so older canonical tags do not reappear after clear.
                    current[normalizedKey] = string.Empty;
                    updatedKeys.Add(normalizedKey);
                    continue;
                }

                if (current.Remove(normalizedKey))
                {
                    updatedKeys.Add(normalizedKey);
                }
                continue;
            }

            current[normalizedKey] = normalized;
            updatedKeys.Add(normalizedKey);
        }

        return updatedKeys;
    }
}
