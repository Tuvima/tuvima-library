using System.Globalization;
using MediaEngine.Contracts.Library;

namespace MediaEngine.Web.Components.Settings.LibraryActivity;

/// <summary>
/// The plain words of the Activity page (spec library-activity): one counts line shared by the progress card and a finished batch
/// ("412 movies · 85 episodes · 51 tracks"), the History entry's headline, and the batch kinds. US spelling.
/// </summary>
public static class LibraryActivityText
{
    public static string Noun(string mediaType, int count) => (mediaType, count == 1) switch
    {
        ("Movies", true) => "movie",
        ("Movies", _) => "movies",
        ("TV", true) => "episode",
        ("TV", _) => "episodes",
        ("Music", true) => "track",
        ("Music", _) => "tracks",
        ("Books", true) => "book",
        ("Books", _) => "books",
        ("Audiobooks", true) => "audiobook",
        ("Audiobooks", _) => "audiobooks",
        ("Comics", true) => "issue",
        ("Comics", _) => "issues",
        ("Videos", true) => "video",
        ("Videos", _) => "videos",
        (_, true) => "item",
        _ => "items",
    };

    /// <summary>"412 movies · 85 episodes · 51 tracks": the biggest counts first.</summary>
    public static string Counts(IEnumerable<(string MediaType, int Count)> counts)
    {
        var parts = counts
            .Where(entry => entry.Count > 0)
            .OrderByDescending(entry => entry.Count)
            .Select(entry => $"{entry.Count.ToString("N0", CultureInfo.InvariantCulture)} {Noun(entry.MediaType, entry.Count)}")
            .ToList();
        return string.Join(" · ", parts);
    }

    public static string Counts(ActivityRunDto run) => Counts(run.Counts.Select(entry => (entry.MediaType, entry.Count)));

    public static string KindVerb(string kind) => kind switch
    {
        ActivityRunKindNames.Updated => "Updated",
        ActivityRunKindNames.Fixed => "Fixed",
        ActivityRunKindNames.Removed => "Removed",
        _ => "Added",
    };

    /// <summary>One dated marker on a searched title's own timeline: "4 new episodes" for an import, "4 episodes" for an update, fix or removal.</summary>
    public static string MarkerLine(ActivityTitleMarkerDto marker, string mediaType)
        => marker.Kind == ActivityRunKindNames.Added
            ? NewLabel(mediaType, marker.ItemCount)
            : Counts([(mediaType, marker.ItemCount)]);

    /// <summary>"2 titles" beside the History heading while a search is showing.</summary>
    public static string TitlesCount(int count) => $"{count.ToString("N0", CultureInfo.InvariantCulture)} {(count == 1 ? "title" : "titles")}";

    public static string MonthHeading(DateTimeOffset when) => when.ToLocalTime().ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    public static string Bubble(DateTimeOffset when) => when.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture);

    public static string BatchCount(int count) => $"{count} {(count == 1 ? "batch" : "batches")}";

    /// <summary>The group tile's bottom-left label: "12 new episodes", "1 new movie" (D12, D18).</summary>
    public static string NewLabel(string mediaType, int count) => $"{count.ToString("N0", CultureInfo.InvariantCulture)} new {Noun(mediaType, count)}";

    /// <summary>"12 minutes ago", "3 hours ago", "2 days ago".</summary>
    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var span = now - when;
        if (span < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return Unit((int)span.TotalMinutes, "minute");
        }

        if (span < TimeSpan.FromDays(1))
        {
            return Unit((int)span.TotalHours, "hour");
        }

        return Unit((int)span.TotalDays, "day");

        static string Unit(int amount, string name) => $"{amount} {name}{(amount == 1 ? string.Empty : "s")} ago";
    }
}
