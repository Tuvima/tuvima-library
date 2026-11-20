using System.Globalization;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Services.Formatting;

namespace MediaEngine.Web.Components.Settings.LibraryActivity;

/// <summary>What the batch list's headings sort by (review mock-ups D3). <see cref="Arrival"/> is the order the Engine sent them in, with no arrow.</summary>
public enum LibraryActivityEntrySort
{
    Arrival,
    Title,
    Type,
    Size,
    Status,
}

/// <summary>
/// The rules of an opened batch's list (review mock-ups D3), kept apart from the markup so they can be tested on their own: which rows are
/// video collections, which are still being worked on, how the headings sort and what the media-type chips read.
/// </summary>
public static class LibraryActivityEntryRows
{
    /// <summary>A folder of videos is one row that opens to its files; every other row is a single line.</summary>
    public static bool IsCollection(ActivityGroupDto group) => group.MediaType == "Videos" && group.Files is { Count: > 0 };

    /// <summary>
    /// How many of a row's items are still being worked on. The Engine records an item in a batch when it is usable, and follow-up work
    /// (details, pictures) carries on after that, so a row is working while its title is in the live "Working now" set. A batch that has ended
    /// has nothing working. A collection counts its own files; any other row counts all of its items.
    /// </summary>
    public static int WorkingItems(ActivityGroupDto group, IReadOnlySet<Guid>? working)
    {
        if (working is null || working.Count == 0)
        {
            return 0;
        }

        if (IsCollection(group))
        {
            return group.Files!.Count(file => file.WorkId is { } id && working.Contains(id));
        }

        return group.WorkId is { } workId && working.Contains(workId) ? group.ItemCount : 0;
    }

    public static bool IsWorking(ActivityGroupDto group, IReadOnlySet<Guid>? working) => WorkingItems(group, working) > 0;

    /// <summary>The rows of one media-type chip (or every row when none is picked), sorted by the chosen heading.</summary>
    public static IReadOnlyList<ActivityGroupDto> Rows(
        IEnumerable<ActivityGroupDto> groups, string? mediaType, LibraryActivityEntrySort sort, bool descending, IReadOnlySet<Guid>? working)
    {
        var rows = groups.Where(group => mediaType is null || string.Equals(group.MediaType, mediaType, StringComparison.Ordinal));
        if (sort == LibraryActivityEntrySort.Arrival)
        {
            return rows.ToList();
        }

        // Every heading breaks its ties by title, so equal sizes or types never shuffle between clicks.
        IOrderedEnumerable<ActivityGroupDto> ordered = sort switch
        {
            LibraryActivityEntrySort.Title => Order(rows, group => group.Title, descending),
            LibraryActivityEntrySort.Type => Order(rows, group => TypeLabel(group.MediaType), descending),
            LibraryActivityEntrySort.Size => Order(rows, group => group.SizeBytes, descending),
            _ => Order(rows, group => IsWorking(group, working) ? 1 : 0, descending),
        };
        return ordered.ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IOrderedEnumerable<ActivityGroupDto> Order(IEnumerable<ActivityGroupDto> rows, Func<ActivityGroupDto, string> key, bool descending)
        => descending ? rows.OrderByDescending(key, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(key, StringComparer.OrdinalIgnoreCase);

    private static IOrderedEnumerable<ActivityGroupDto> Order(IEnumerable<ActivityGroupDto> rows, Func<ActivityGroupDto, long> key, bool descending)
        => descending ? rows.OrderByDescending(key) : rows.OrderBy(key);

    private static IOrderedEnumerable<ActivityGroupDto> Order(IEnumerable<ActivityGroupDto> rows, Func<ActivityGroupDto, int> key, bool descending)
        => descending ? rows.OrderByDescending(key) : rows.OrderBy(key);

    /// <summary>The Type column and the chips: the plain name of a media type.</summary>
    public static string TypeLabel(string mediaType) => string.IsNullOrWhiteSpace(mediaType) ? "Other" : mediaType;

    /// <summary>The Size column: "1.4 GB", or a dash when the file could not be measured.</summary>
    public static string Size(long bytes) => DisplayFormat.FormatFileSize(bytes) ?? "—";

    /// <summary>The summary row's stamp: "Nov 18, 3:42 PM".</summary>
    public static string Stamp(DateTimeOffset when) => when.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture);
}
