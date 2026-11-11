using System.Globalization;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Times and Review lines of the editor's Bookmarks tab (universal editor v5 D37). The wording is the product's: "Everyone's bookmarks",
/// "Add to Everyone's", "Remove from Everyone's", "definitive set", "From profiles" and "Start over from profiles". Pure.
/// </summary>
public static class EditorBookmarkWording
{
    /// <summary>7 seconds reads "0:07", 754 reads "12:34", 3723 reads "1:02:03".</summary>
    public static string Time(double seconds)
    {
        var whole = (long)Math.Max(0, Math.Floor(seconds));
        var hours = whole / 3600;
        var minutes = whole % 3600 / 60;
        var rest = whole % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00}");
    }

    /// <summary>Reads "12:04", "1:02:03" or a plain number of seconds. False for anything else, a negative time or a time past the running time.</summary>
    public static bool TryParseTime(string? text, double? runningTime, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split(':');
        if (parts.Length > 3)
        {
            return false;
        }

        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0 || double.IsNaN(value) || double.IsInfinity(value))
            {
                return false;
            }

            total = total * 60 + value;
        }

        if (runningTime is { } end && end > 0 && total > end)
        {
            return false;
        }

        seconds = Math.Round(total, 3);
        return true;
    }

    /// <summary>Where a row's bookmark is: a time, a page, or the chapter it sits in.</summary>
    public static string Place(EditorBookmarkRowDto row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.PositionKind switch
        {
            "page" => string.Create(CultureInfo.InvariantCulture, $"Page {(int)row.PositionSeconds}"),
            "text" => string.IsNullOrWhiteSpace(row.ChapterTitle) ? "In the text" : row.ChapterTitle!,
            _ => Time(row.PositionSeconds),
        };
    }

    /// <summary>Where a bookmark in a bookmark file is: a time, or for a book the chapter it sits in.</summary>
    public static string IncomingPlace(BookmarkSetItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (PageLocator.TryParse(item.Locator) is { } page)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Page {page}");
        }

        return string.IsNullOrWhiteSpace(item.Locator)
            ? Time(item.PositionSeconds)
            : string.IsNullOrWhiteSpace(item.ChapterTitle) ? "In the text" : item.ChapterTitle!;
    }

    /// <summary>Where the nearest bookmark of Everyone's is, for the compare view: a time, or "In the text" for a book.</summary>
    public static string NearestPlace(BookmarkSetExistingDto near)
    {
        ArgumentNullException.ThrowIfNull(near);
        if (PageLocator.TryParse(near.Locator) is { } page)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Page {page}");
        }

        return string.IsNullOrWhiteSpace(near.Locator) ? Time(near.PositionSeconds) : "In the text";
    }

    /// <summary>
    /// One bookmark of the compare view as an import item. A book bookmark keeps its reader location and chapter (the chapter travels as the
    /// row's position); a video bookmark keeps the picture the file carried.
    /// </summary>
    public static EditorBookmarkImportItem ImportItem(BookmarkSetCompareRowDto row, bool replaceNearest)
    {
        ArgumentNullException.ThrowIfNull(row);
        var incoming = row.Incoming;
        var isPage = PageLocator.TryParse(incoming.Locator) is not null;
        var isText = !string.IsNullOrWhiteSpace(incoming.Locator) && !isPage;
        return new EditorBookmarkImportItem
        {
            PositionKind = isPage ? "page" : null,
            PositionSeconds = incoming.PositionSeconds,
            EndSeconds = isText ? null : incoming.EndSeconds,
            ChapterTitle = incoming.ChapterTitle,
            Locator = isText || isPage ? incoming.Locator : null,
            ChapterIndex = isText ? (int)incoming.PositionSeconds : null,
            FrameBase64 = isText ? null : incoming.FrameBase64,
            Name = incoming.Note,
            Description = incoming.Description,
            ReplacesBookmarkId = replaceNearest ? row.Yours?.Id : null,
        };
    }

    /// <summary>"12:04 Opening": the place and the name, for a Review line.</summary>
    public static string Short(EditorBookmarkRowDto row) =>
        string.IsNullOrWhiteSpace(row.Name) ? Place(row) : $"{Place(row)} {row.Name.Trim()}";

    public static string AddedLine(EditorBookmarkRowDto row) => $"Added to Everyone's: {Short(row)}";

    public static string RemovedLine(EditorBookmarkRowDto row) => $"Removed from Everyone's: {Short(row)}";

    public static string RenamedLine(EditorBookmarkRowDto row, string name) => $"Renamed {Place(row)} to {name.Trim()}";

    public static string TimeChangedLine(EditorBookmarkRowDto row, double to) =>
        $"Time changed{(string.IsNullOrWhiteSpace(row.Name) ? string.Empty : $" for {row.Name.Trim()}")}: {Time(row.PositionSeconds)} to {Time(to)}";

    /// <summary>"Imported 3 bookmarks", or "Imported 3 bookmarks (S1 E2)" when the import is for one episode of a show file.</summary>
    public static string ImportedLine(int count, string? forEpisode = null)
    {
        var line = count == 1 ? "Imported 1 bookmark" : string.Create(CultureInfo.InvariantCulture, $"Imported {count} bookmarks");
        return string.IsNullOrWhiteSpace(forEpisode) ? line : $"{line} ({forEpisode.Trim()})";
    }

    public const string StartOverLine = "Started over from profiles";

    /// <summary>
    /// Where the picture of a bookmark's moment is drawn from: the Dashboard's own address for it (the browser never talks to the Engine), keeping
    /// the Engine's version tag so a picture made again after a time change is fetched again. Null when the picture does not exist yet.
    /// </summary>
    public static string? FrameSource(EditorBookmarkRowDto row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrWhiteSpace(row.FrameUrl))
        {
            return null;
        }

        var query = row.FrameUrl.IndexOf('?', StringComparison.Ordinal);
        return $"/engine-bookmark-frame/{row.Id:D}" + (query >= 0 ? row.FrameUrl[query..] : string.Empty);
    }
}
