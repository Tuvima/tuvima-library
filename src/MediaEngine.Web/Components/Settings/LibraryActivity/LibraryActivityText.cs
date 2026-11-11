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

    public static string KindLabel(string kind) => KindVerb(kind);

    /// <summary>The entry's headline: "Added 14 movies" (a removal, which knows no types, reads "Removed 3 items").</summary>
    public static string Headline(ActivityRunDto run)
    {
        var what = run.Kind == ActivityRunKindNames.Removed || run.Counts.Count == 0
            ? $"{run.ItemCount.ToString("N0", CultureInfo.InvariantCulture)} {Noun(string.Empty, run.ItemCount)}"
            : Counts(run);
        return $"{KindVerb(run.Kind)} {what}";
    }

    private static readonly string[] TypeOrder = ["Movies", "TV", "Music", "Books", "Audiobooks", "Comics", "Videos"];

    /// <summary>"Movies, TV and Music": the kinds a batch touched, in one fixed order, for the small line under its headline.</summary>
    public static string Kinds(IEnumerable<string> mediaTypes)
    {
        var present = mediaTypes.Where(type => !string.IsNullOrEmpty(type)).Distinct(StringComparer.Ordinal)
            .OrderBy(Rank)
            .ThenBy(type => type, StringComparer.Ordinal)
            .ToList();
        return present.Count switch
        {
            0 => string.Empty,
            1 => present[0],
            _ => $"{string.Join(", ", present.Take(present.Count - 1))} and {present[^1]}",
        };
    }

    private static int Rank(string mediaType)
    {
        var index = Array.IndexOf(TypeOrder, mediaType);
        return index < 0 ? int.MaxValue : index;
    }

    /// <summary>
    /// The small line under an entry's headline: what kinds it touched ("Movies, TV and Music"), or why it happened for an update, a fix
    /// or a removal. A batch with no kinds recorded falls back to its library's name.
    /// </summary>
    public static string Subtitle(ActivityRunDto run)
    {
        var kinds = Kinds(run.Counts.Select(entry => entry.MediaType));
        return run.Kind switch
        {
            ActivityRunKindNames.Removed => "Manually removed from library",
            ActivityRunKindNames.Fixed => "Resolved from Needs Review",
            ActivityRunKindNames.Updated => string.IsNullOrEmpty(kinds) ? "Metadata refresh" : $"Metadata refresh · {kinds}",
            _ => string.IsNullOrEmpty(kinds) ? run.LibraryName : kinds,
        };
    }

    /// <summary>One dated marker on a searched title's own timeline: "4 new episodes" for an import, "4 episodes" for an update, fix or removal.</summary>
    public static string MarkerLine(ActivityTitleMarkerDto marker, string mediaType)
        => marker.Kind == ActivityRunKindNames.Added
            ? NewLabel(mediaType, marker.ItemCount)
            : Counts([(mediaType, marker.ItemCount)]);

    /// <summary>"2 titles" beside the History heading while a search is showing.</summary>
    public static string TitlesCount(int count) => $"{count.ToString("N0", CultureInfo.InvariantCulture)} {(count == 1 ? "title" : "titles")}";

    public static string StatusLabel(string status) => status switch
    {
        ActivityRunStatusNames.Active => "Active",
        ActivityRunStatusNames.Failed => "Failed",
        _ => "Completed",
    };

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
