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
        string? starRating = null)
    {
        var facts = new List<string>();
        switch (mediaKind)
        {
            case "Movie":
            case "TV":
                AddFact(facts, NormalizeContentRating(contentRating), title);
                AddFact(facts, year, title);
                AddFact(facts, FormatLength(runtime, duration, durationFirst: false), title);
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
                AddFact(facts, FormatLength(runtime, duration, durationFirst: true), title);
                AddFact(facts, FormatStarRating(starRating ?? rating), title);
                break;
            case "Music":
                AddFact(facts, artist ?? author, title);
                AddFact(facts, NormalizeContentRating(contentRating), title);
                AddFact(facts, year, title);
                AddFact(facts, FormatLength(runtime, duration, durationFirst: true), title);
                AddFact(facts, FormatStarRating(starRating ?? rating), title);
                break;
            default:
                AddFact(facts, author ?? artist, title);
                AddFact(facts, NormalizeContentRating(contentRating), title);
                AddFact(facts, year, title);
                AddFact(facts, FormatLength(runtime, duration, durationFirst: false), title);
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

    // "Duration" arrives as whole seconds (the data store converts minutes and the measured file length to
    // seconds); "runtime" is minutes. Reading either as the other is what showed songs as "0:03".
    private static string? FormatLength(string? runtime, string? duration, bool durationFirst)
    {
        var seconds = ParseNumber(duration);
        var minutes = ParseNumber(runtime);
        if (durationFirst)
        {
            return seconds is > 0 ? FormatElapsed(TimeSpan.FromSeconds(seconds.Value))
                : minutes is > 0 ? FormatElapsed(TimeSpan.FromMinutes(minutes.Value))
                : NonNumericText(duration) ?? NonNumericText(runtime);
        }

        return minutes is > 0 ? $"{minutes.Value:0.#} min"
            : seconds is > 0 ? $"{Math.Round(seconds.Value / 60d):0} min"
            : NonNumericText(runtime) ?? NonNumericText(duration);
    }

    private static double? ParseNumber(string? value)
        => double.TryParse(value?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static string? NonNumericText(string? value)
        => string.IsNullOrWhiteSpace(value) || ParseNumber(value) is not null ? null : value.Trim();

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
