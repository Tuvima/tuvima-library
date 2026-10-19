using System.Globalization;
using MediaEngine.Contracts.Activity;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Formatting;

/// <summary>How a tile of the Overview's Recent activity strip shows its progress.</summary>
public enum ActivityProgressMark
{
    /// <summary>Nothing is drawn: music, My list and anything whose progress is not tracked.</summary>
    None,

    /// <summary>A thin bar along the bottom of the artwork.</summary>
    Bar,

    /// <summary>A check, because the item is finished.</summary>
    Finished,
}

/// <summary>
/// The words and marks of the Overview's Recent activity strip (D13, D17, D18): one verb-led caption per tile, a progress bar,
/// a check or nothing, and the lines of the hover card. Kept apart from the components so the wording is tested on its own.
/// </summary>
public static class PersonalActivityPresentation
{
    /// <summary>The longest strip the Overview asks for.</summary>
    public const int StripLimit = 20;

    /// <summary>"52 m", "1 h 20 m", "1 h 05 m". Under a minute reads "under 1 m" so a short look is not shown as nothing.</summary>
    public static string FormatDuration(long seconds)
    {
        if (seconds < 30)
        {
            return "under 1 m";
        }

        var totalMinutes = (int)Math.Round(seconds / 60d, MidpointRounding.AwayFromZero);
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours} h {minutes:00} m")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes} m");
    }

    /// <summary>
    /// The one non-wrapping line under a tile. It starts with a verb so it cannot be read as the length of the item:
    /// Watched, Read, Listened (audiobooks), Played (music) or Added to My list.
    /// </summary>
    public static string Caption(PersonalActivityRecentTileDto tile)
    {
        if (string.Equals(tile.Kind, PersonalActivityKindValues.MyList, StringComparison.Ordinal))
        {
            return "Added to My list";
        }

        var verb = Verb(tile.Kind);
        return tile.PlayedSeconds <= 0 ? verb : $"{verb} {FormatDuration(tile.PlayedSeconds)}";
    }

    private static string Verb(string kind) => kind switch
    {
        PersonalActivityKindValues.Watch => "Watched",
        PersonalActivityKindValues.Read => "Read",
        PersonalActivityKindValues.Listen => "Listened",
        PersonalActivityKindValues.MusicSession => "Played",
        _ => "Opened",
    };

    /// <summary>
    /// A check for a finished item, a thin bar for one that is part way through, nothing for music, My list and anything not
    /// started or without tracked progress.
    /// </summary>
    public static ActivityProgressMark ProgressMark(PersonalActivityRecentTileDto tile)
    {
        if (tile.Kind is PersonalActivityKindValues.MusicSession or PersonalActivityKindValues.MyList)
        {
            return ActivityProgressMark.None;
        }

        if (tile.Finished || tile.ProgressPct is >= FinishedPercent)
        {
            return ActivityProgressMark.Finished;
        }

        return tile.ProgressPct is > 0 ? ActivityProgressMark.Bar : ActivityProgressMark.None;
    }

    /// <summary>At this point the shared artwork progress shows a check instead of a bar.</summary>
    private const double FinishedPercent = 99.5;

    /// <summary>The media kind the shared artwork progress uses to word its completion label.</summary>
    public static string ProgressMediaKind(PersonalActivityRecentTileDto tile) => tile.Kind switch
    {
        PersonalActivityKindValues.Watch => "Movie",
        PersonalActivityKindValues.Read => "Book",
        _ => "Audiobook",
    };

    /// <summary>The covers a music session shows in its three-by-three collage: at most nine.</summary>
    public static IReadOnlyList<string> CollageCovers(PersonalActivityRecentTileDto tile) =>
        tile.CoverUrls is null ? [] : [.. tile.CoverUrls.Where(url => !string.IsNullOrWhiteSpace(url)).Take(9)];

    public static bool IsMusicSession(PersonalActivityRecentTileDto tile) =>
        string.Equals(tile.Kind, PersonalActivityKindValues.MusicSession, StringComparison.Ordinal);

    /// <summary>The tile's shape class; an unknown shape falls back to square.</summary>
    public static string ShapeClass(PersonalActivityRecentTileDto tile) => tile.Shape switch
    {
        PersonalActivityShapeValues.Portrait => "portrait",
        PersonalActivityShapeValues.Landscape => "landscape",
        _ => "square",
    };

    /// <summary>The title, or "Music" for a session (whose title is a placeholder).</summary>
    public static string HoverTitle(PersonalActivityRecentTileDto tile) =>
        IsMusicSession(tile) && string.IsNullOrWhiteSpace(tile.Title) ? "Music" : tile.Title;

    /// <summary>For a session, "50 songs from 12 albums"; null for anything else or when the counts are not known.</summary>
    public static string? MusicCounts(PersonalActivityRecentTileDto tile)
    {
        if (!IsMusicSession(tile) || tile.SongCount is not { } songs)
        {
            return null;
        }

        var songText = $"{songs} {(songs == 1 ? "song" : "songs")}";
        return tile.AlbumCount is { } albums
            ? $"{songText} from {albums} {(albums == 1 ? "album" : "albums")}"
            : songText;
    }

    /// <summary>"Today, 9:41 PM", "Yesterday, 9:41 PM" or "Oct 12, 9:41 PM", in the viewer's local time.</summary>
    public static string When(string startedAt, DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(startedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var started))
        {
            return string.Empty;
        }

        var local = started.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == today)
        {
            return $"Today, {time}";
        }

        return local.Date == today.AddDays(-1)
            ? $"Yesterday, {time}"
            : $"{local.ToString("MMM d", CultureInfo.CurrentCulture)}, {time}";
    }

    /// <summary>
    /// Where a tap on the tile goes: the item for a watched, read, listened or My list entry, or the Activity page for a music
    /// session or an item whose record no longer points at anything.
    /// </summary>
    public static string Href(PersonalActivityRecentTileDto tile)
    {
        if (IsMusicSession(tile) || !Guid.TryParse(tile.WorkId, out var workId))
        {
            return SettingsNav.ActivityRoute;
        }

        var context = tile.Kind switch
        {
            PersonalActivityKindValues.Watch => "?context=watch",
            PersonalActivityKindValues.Read => "?context=read",
            PersonalActivityKindValues.Listen => "?context=listen",
            _ => string.Empty,
        };
        return $"/details/work/{workId:D}{context}";
    }

    /// <summary>Whether the tile opens an item (true) or only the Activity page (false).</summary>
    public static bool OpensItem(PersonalActivityRecentTileDto tile) =>
        !IsMusicSession(tile) && Guid.TryParse(tile.WorkId, out _);

    /// <summary>The accessible name of a tile: the title and episode that are not printed, then the caption.</summary>
    public static string AccessibleName(PersonalActivityRecentTileDto tile)
    {
        var parts = new List<string> { HoverTitle(tile) };
        if (!string.IsNullOrWhiteSpace(tile.Subtitle))
        {
            parts.Add(tile.Subtitle!);
        }

        parts.Add(Caption(tile));
        return string.Join(", ", parts);
    }

    /// <summary>Seconds of watching, reading and listening, summed from the profile's totals by media type.</summary>
    public static (double Watched, double Read, double Listened) ConsumedSeconds(IReadOnlyDictionary<string, double>? byMediaType)
    {
        double watched = 0, read = 0, listened = 0;
        foreach (var (mediaType, seconds) in byMediaType ?? new Dictionary<string, double>())
        {
            if (seconds <= 0)
            {
                continue;
            }

            var type = mediaType.Trim().ToLowerInvariant();
            if (type.Contains("audio", StringComparison.Ordinal) || type.Contains("music", StringComparison.Ordinal))
            {
                listened += seconds;
            }
            else if (type.Contains("book", StringComparison.Ordinal) || type.Contains("comic", StringComparison.Ordinal)
                     || type.Contains("epub", StringComparison.Ordinal))
            {
                read += seconds;
            }
            else if (type.Contains("movie", StringComparison.Ordinal) || type.Contains("tv", StringComparison.Ordinal)
                     || type.Contains("video", StringComparison.Ordinal))
            {
                watched += seconds;
            }
        }

        return (watched, read, listened);
    }

    /// <summary>
    /// One line about the person's taste: their top three genres and, when known, their top two moods. Falls back to the genres
    /// they have opened most when no taste profile has been built yet. Null when there is nothing honest to say.
    /// </summary>
    public static string? TasteSummary(
        IReadOnlyDictionary<string, double>? genreWeights,
        IReadOnlyDictionary<string, double>? moodWeights,
        IReadOnlyDictionary<string, int>? genreCounts)
    {
        var genres = Top(genreWeights, 3);
        if (genres.Count == 0)
        {
            genres = Top(genreCounts?.ToDictionary(pair => pair.Key, pair => (double)pair.Value), 3);
        }

        if (genres.Count == 0)
        {
            return null;
        }

        var moods = Top(moodWeights, 2);
        return moods.Count == 0
            ? $"Mostly {JoinList(genres)}."
            : $"Mostly {JoinList(genres)}, in a {JoinList(moods).ToLowerInvariant()} mood.";
    }

    private static List<string> Top(IReadOnlyDictionary<string, double>? weights, int count) =>
        weights is null
            ? []
            : [.. weights
                .Where(pair => pair.Value > 0 && !string.IsNullOrWhiteSpace(pair.Key))
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Take(count)
                .Select(pair => pair.Key.Trim())];

    private static string JoinList(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    /// <summary>"12 h" for ten hours or more, "3.5 h" below that, "40 m" under an hour. Null when there is nothing to show.</summary>
    public static string? FormatHours(double seconds)
    {
        if (seconds < 60)
        {
            return null;
        }

        if (seconds < 3600)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(seconds / 60d)} m");
        }

        var hours = seconds / 3600d;
        return hours >= 10
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Round(hours)} h")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(hours, 1):0.#} h");
    }
}
