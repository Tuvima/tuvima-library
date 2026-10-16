using System.Globalization;
using MediaEngine.Contracts.Display;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Components.MediaTiles;

/// <summary>
/// Pure rules for the phone Continue row: which items belong in it, in what order, and the one-line label under each
/// card. Phones show a single row (not one group per media type), newest activity first.
/// </summary>
public static class PhoneContinueLogic
{
    /// <summary>An item belongs in Continue only when the person has actually started it.</summary>
    public static bool HasProgress(MediaTileViewModel item)
        => item.ProgressPct is > 0 and < 99.5 || item.ContinuationState == DisplayContinuationState.InProgress;

    /// <summary>Started items only, most recently touched first (ties keep the Engine's order).</summary>
    public static IReadOnlyList<MediaTileViewModel> Select(IEnumerable<MediaTileViewModel> items)
        => items.Where(HasProgress)
            .Select((item, index) => (item, index))
            .OrderByDescending(entry => entry.item.SortTimestamp)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.item)
            .ToList();

    /// <summary>A thin bar is drawn only for real progress, never at zero.</summary>
    public static bool ShowsProgressBar(MediaTileViewModel item) => item.ProgressPct is > 0 and < 99.5;

    /// <summary>The line under the title: time left for video, the saved place for books and audio.</summary>
    public static string SubLabel(MediaTileViewModel item)
    {
        var remaining = RemainingText(item.RemainingSeconds);
        switch (item.MediaKind)
        {
            case "TV":
                // The title line already says S1 E2, so this line is only the time left (or the percent).
                return remaining ?? PercentLabel(item, "watched");
            case "Movie":
                return remaining ?? PercentLabel(item, "watched");
            case "Book":
            case "Comic":
                return Clean(item.ProgressLabel) ?? PercentLabel(item, "read");
            case "Audiobook":
                return Clean(item.ProgressLabel) ?? remaining ?? PercentLabel(item, "listened");
            default:
                return Clean(item.ProgressLabel) ?? remaining ?? PercentLabel(item, "played");
        }
    }

    /// <summary>The small type icon on a card (an Outlined icon from <c>AppMaterialIcons</c>).</summary>
    public static string TypeIcon(string? mediaKind) => mediaKind switch
    {
        "Movie" => AppMaterialIcons.Outlined.Movie,
        "TV" => AppMaterialIcons.Outlined.Tv,
        "Audiobook" => AppMaterialIcons.Outlined.Headphones,
        "Music" => AppMaterialIcons.Outlined.Album,
        _ => AppMaterialIcons.Outlined.MenuBook,
    };

    /// <summary>The word for the type icon, read to screen readers in place of the picture.</summary>
    public static string TypeName(string? mediaKind) => mediaKind switch
    {
        "Movie" => "Movie",
        "TV" => "TV show",
        "Audiobook" => "Audiobook",
        "Music" => "Album",
        "Comic" => "Comic",
        _ => "Book",
    };

    private static string? RemainingText(double? seconds)
    {
        if (seconds is not > 0 || !double.IsFinite(seconds.Value))
        {
            return null;
        }

        var minutes = (int)Math.Ceiling(seconds.Value / 60);
        return minutes >= 60
            ? $"{minutes / 60} hr{(minutes % 60 == 0 ? string.Empty : $" {minutes % 60} min")} left"
            : $"{minutes} min left";
    }

    private static string PercentLabel(MediaTileViewModel item, string verb)
        => item.ProgressPct is > 0 and < 99.5 ? $"{Math.Round(item.ProgressPct.Value).ToString(CultureInfo.InvariantCulture)}% {verb}" : "In progress";

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
        return present.Count == 0 ? null : string.Join(" · ", present);
    }
}
