using System.Globalization;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Display;

public static class DisplayFactBuilder
{
    public static IReadOnlyList<string> Build(
        string mediaKind,
        string title,
        string? year = null,
        string? genre = null,
        string? author = null,
        string? artist = null,
        string? narrator = null,
        string? publisher = null,
        string? series = null,
        string? seriesPosition = null,
        string? showName = null,
        string? season = null,
        string? episode = null,
        string? track = null,
        string? album = null,
        string? rating = null,
        string? contentRating = null,
        string? runtime = null,
        string? duration = null,
        string? pageCount = null,
        string? durationSeconds = null,
        string? starRating = null)
    {
        var facts = new List<string>();
        switch (mediaKind)
        {
            case "Movie":
            case "TV":
                AddFact(facts, NormalizeContentRating(contentRating), title);
                AddFact(facts, year, title);
                AddFact(facts, FormatDuration(StringHelpers.FirstNonBlank(runtime, duration), durationSeconds, mediaKind), title);
                AddFact(facts, FormatStarRating(starRating ?? rating), title);
                break;
            case "Book":
            case "Comic":
                AddFact(facts, author, title);
                if (mediaKind == "Comic")
                {
                    AddFact(facts, publisher, title);
                }
                AddFact(facts, NormalizeContentRating(contentRating), title);
                AddFact(facts, year, title);
                AddFact(facts, FormatPageCount(pageCount), title);
                AddFact(facts, FormatStarRating(starRating ?? rating), title);
                break;
            case "Audiobook":
                AddFact(facts, author, title);
                AddFact(facts, string.IsNullOrWhiteSpace(narrator) ? null : $"Narrated by {narrator.Trim()}", title);
                AddFact(facts, NormalizeContentRating(contentRating), title);
                AddFact(facts, year, title);
                AddFact(facts, FormatDuration(StringHelpers.FirstNonBlank(duration, runtime), durationSeconds, mediaKind), title);
                AddFact(facts, FormatStarRating(starRating ?? rating), title);
                break;
            case "Music":
                AddFact(facts, artist ?? author, title);
                AddFact(facts, NormalizeContentRating(contentRating), title);
                AddFact(facts, year, title);
                AddFact(facts, FormatDuration(StringHelpers.FirstNonBlank(duration, runtime), durationSeconds, mediaKind), title);
                AddFact(facts, FormatStarRating(starRating ?? rating), title);
                break;
            default:
                AddFact(facts, author ?? artist, title);
                AddFact(facts, NormalizeContentRating(contentRating), title);
                AddFact(facts, year, title);
                AddFact(facts, FormatDuration(StringHelpers.FirstNonBlank(runtime, duration), durationSeconds, mediaKind), title);
                AddFact(facts, FormatStarRating(starRating ?? rating), title);
                break;
        }

        return facts;
    }

    private static void AddFact(List<string> facts, string? value, params string?[] exclusions)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var cleaned = value.Trim();
        if (exclusions.Any(exclusion => string.Equals(cleaned, exclusion?.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (!facts.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
        {
            facts.Add(cleaned);
        }
    }

    private static string? FormatStarRating(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return null;
        }

        var cleaned = rating.Trim();
        if (cleaned.StartsWith('\u2605'))
        {
            return cleaned;
        }

        if (cleaned.StartsWith("rating", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned["rating".Length..].Trim(' ', ':');
        }

        if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var score))
        {
            cleaned = score.ToString("0.0", CultureInfo.InvariantCulture);
        }

        return string.IsNullOrWhiteSpace(cleaned) ? null : $"\u2605 {cleaned}";
    }

    private static string? NormalizeContentRating(string? rating)
        => string.IsNullOrWhiteSpace(rating) ? null : rating.Trim();

    private static string? FormatPageCount(string? pageCount)
    {
        if (string.IsNullOrWhiteSpace(pageCount))
        {
            return null;
        }

        var cleaned = pageCount.Trim();
        return cleaned.Contains("page", StringComparison.OrdinalIgnoreCase)
            ? cleaned
            : $"{cleaned} pages";
    }

    // "duration" and "runtime" carry the provider's own unit (minutes for video, milliseconds for songs); "duration_sec"
    // always carries seconds. Keeping them apart stops a file-probed 1361.4 seconds reading as 1361.4 minutes.
    private static string? FormatDuration(string? duration, string? durationSeconds, string mediaKind)
    {
        var hasSeconds = TryParseNumber(durationSeconds, out var seconds) && seconds > 0;
        if (string.IsNullOrWhiteSpace(duration))
        {
            return hasSeconds ? FormatFromSeconds(seconds, mediaKind) : null;
        }

        var cleaned = duration.Trim();
        if (!TryParseNumber(cleaned, out var numeric))
        {
            return cleaned;
        }

        if (mediaKind == "Music")
        {
            return FormatElapsed(TimeSpan.FromMilliseconds(numeric));
        }

        if (mediaKind == "Audiobook")
        {
            var elapsed = numeric >= 100_000
                ? TimeSpan.FromMilliseconds(numeric)
                : TimeSpan.FromSeconds(numeric);
            return FormatElapsed(elapsed);
        }

        return $"{numeric:0.#} min";
    }

    private static string FormatFromSeconds(double seconds, string mediaKind)
        => mediaKind is "Music" or "Audiobook"
            ? FormatElapsed(TimeSpan.FromSeconds(seconds))
            : $"{Math.Max(1, Math.Round(seconds / 60d, MidpointRounding.AwayFromZero)):0} min";

    private static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        return !string.IsNullOrWhiteSpace(text)
            && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
        {
            var hours = (int)Math.Floor(elapsed.TotalHours);
            return elapsed.Minutes > 0 ? $"{hours}h {elapsed.Minutes}m" : $"{hours}h";
        }

        return $"{Math.Max(0, (int)elapsed.TotalMinutes)}:{elapsed.Seconds:00}";
    }

}
