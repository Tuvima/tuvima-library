using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Components.Settings;

/// <summary>
/// The words of the Now panel and the Added summary, kept in one place so the page, the tests and the Activity log wording
/// agree. Counts use thousands separators; a count of one uses the singular.
/// </summary>
public static class IngestionNowText
{
    /// <summary>"212 items being added" or "1 item being added".</summary>
    public static string CountLine(int total) =>
        $"{DisplayFormat.FormatCount(total)} {(total == 1 ? "item" : "items")} being added";

    /// <summary>
    /// "2,300 photos in progress", "12 personal videos in progress" or "2,300 photos and 12 personal videos in progress";
    /// null when neither is being taken in. Photos and personal videos are only ever counted, never listed.
    /// </summary>
    public static string? PersonalMediaLine(IngestionPersonalMediaProgressDto? progress)
    {
        if (progress is null)
        {
            return null;
        }

        var parts = new List<string>(2);
        if (progress.Photos > 0)
        {
            parts.Add($"{DisplayFormat.FormatCount(progress.Photos)} {(progress.Photos == 1 ? "photo" : "photos")}");
        }

        if (progress.Videos > 0)
        {
            parts.Add($"{DisplayFormat.FormatCount(progress.Videos)} personal {(progress.Videos == 1 ? "video" : "videos")}");
        }

        return parts.Count == 0 ? null : $"{string.Join(" and ", parts)} in progress";
    }

    /// <summary>The noun for a count of fully added items of one type: "movies", "episodes", "songs", "books", "audiobooks".</summary>
    public static string AddedNoun(string mediaType, int count)
    {
        if (mediaType == "Audiobooks")
        {
            return count == 1 ? "audiobook" : "audiobooks";
        }

        return mediaType is "Unknown" or ""
            ? (count == 1 ? "other item" : "other items")
            : IngestionQueueProgressText.TypeNoun(mediaType, count);
    }

    /// <summary>"Today", "Yesterday", or the date ("Oct 17").</summary>
    public static string DayLabel(DateOnly day, DateOnly today) =>
        day == today ? "Today"
        : day == today.AddDays(-1) ? "Yesterday"
        : day.ToString("MMM d", System.Globalization.CultureInfo.CurrentCulture);
}
