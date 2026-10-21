using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Display;

public static class DisplayMediaRules
{
    public static string? NormalizeLane(string? lane)
    {
        if (string.IsNullOrWhiteSpace(lane))
        {
            return null;
        }

        var normalized = lane.Trim().ToLowerInvariant();
        return normalized is "watch" or "read" or "listen" ? normalized : null;
    }

    public static string NormalizeMediaType(string mediaType) => NormalizeDisplayKind(mediaType) switch
    {
        "Movie" => "Movies",
        "TV" => "TV",
        "Video" => "Videos",
        "Book" => "Books",
        "Comic" => "Comics",
        "Audiobook" => "Audiobooks",
        "Music" => "Music",
        var value => value,
    };

    public static string NormalizeDisplayKind(string? mediaType)
    {
        var label = MediaTypeClassifier.GetDisplayLabel(mediaType ?? string.Empty);
        return label switch
        {
            "Movies" => "Movie",
            "Books" => "Book",
            "Comics" => "Comic",
            "Audiobooks" => "Audiobook",
            _ => label,
        };
    }

    public static bool IsWatchKind(string mediaType) => NormalizeDisplayKind(mediaType) is "Movie" or "Video" or "TV";

    public static bool IsReadKind(string mediaType) => NormalizeDisplayKind(mediaType) is "Book" or "Comic";

    public static bool IsListenKind(string mediaType) => NormalizeDisplayKind(mediaType) is "Music" or "Audiobook";

    public static IReadOnlyList<string> SplitValues(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(['|', ';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string StableKey(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var key = new string(chars).Trim('-');
        while (key.Contains("--", StringComparison.Ordinal))
        {
            key = key.Replace("--", "-", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(key) ? "group" : key;
    }

    public static double? ParseDouble(string? value) =>
        double.TryParse(value, out var parsed) ? parsed : null;
}
