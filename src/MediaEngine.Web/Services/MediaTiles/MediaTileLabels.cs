using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.MediaTiles;

/// <summary>
/// The words a tile draws on its cover (One tile design spec D3, D6, D7, D10, D11). A tile carries no caption underneath, so the
/// only text besides the title on the art is a status label top-left and a bottom label (the group count) bottom-left. One
/// label per slot; a word that cannot be decided is never guessed.
/// </summary>
public static class MediaTileLabels
{
    /// <summary>
    /// The top-left slot: "Getting details" while the item is still being matched, otherwise the New / New episode label from the
    /// Engine's New rule. Null leaves the slot empty.
    /// </summary>
    public static string? Status(MediaTileViewModel item, string gettingDetails)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsSettling)
        {
            return gettingDetails;
        }

        // A video tile carries no New pill (item 14): its still and name are the whole story.
        if (IsVideoStillTile(item))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(item.NewLabel) ? null : item.NewLabel;
    }

    /// <summary>
    /// The bottom-left slot: the group count ("6 books", "11 songs", "12 episodes", "3 videos"). Null when the tile is a single
    /// title or has no known count. A group count and a "Kept" mark share the slot, so the count wins (a kept file is never a group).
    /// </summary>
    public static string? Bottom(MediaTileViewModel item, string kept)
    {
        ArgumentNullException.ThrowIfNull(item);

        // A playlist says what it is and how much it holds ("Playlist · 48 songs"); the plain count is the fallback.
        if (item.IsPlaylist)
        {
            return string.IsNullOrWhiteSpace(item.TypeLine) ? GroupCount(item) : item.TypeLine;
        }

        // A person counts what they have ("11 albums", "9 films"); a person tile never says "Kept".
        if (item.Person is not null)
        {
            return PersonCount(item);
        }

        // A group of titles counts what it holds ("6 books"); with no per-kind counts the Series noun reads better than "titles".
        if (item.RenderAsGroupTile)
        {
            return item.MediaCounts.Any(mediaCount => mediaCount.Count > 0)
                ? GroupCount(item)
                : CountText(item) ?? GroupCount(item);
        }

        if (CountText(item) is { } count)
        {
            return count;
        }

        return item.IsKept ? kept : null;
    }

    /// <summary>
    /// The bottom label of a group of titles (curated collection, Series, Universe, list, set, playlist): the count of its one kind
    /// ("6 books") or, when it mixes kinds, the total ("9 titles"). Null when the group is empty or its size is unknown.
    /// </summary>
    public static string? GroupCount(MediaTileViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var counts = item.MediaCounts.Where(count => count.Count > 0).ToList();
        if (counts.Count == 1)
        {
            return $"{counts[0].Count} {counts[0].Label.ToLowerInvariant()}";
        }

        var total = GroupTotal(item);
        return total > 0 ? $"{total} {(total == 1 ? "title" : "titles")}" : null;
    }

    /// <summary>
    /// How many titles a group holds: the sum of its counts, or its owned or previewed total when it has no counts. The same number the
    /// count label says, so the collage's "+N" chip never disagrees with it.
    /// </summary>
    public static int GroupTotal(MediaTileViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var counts = item.MediaCounts.Where(count => count.Count > 0).ToList();
        return counts.Count > 0
            ? counts.Sum(count => count.Count)
            : item.GroupSummary?.OwnedCount ?? item.PreviewTotalCount ?? 0;
    }

    /// <summary>
    /// The count on a person's tile: the first kind they have a count for ("11 albums" for an artist, whose album count comes first;
    /// "9 films" for a director), or, for a person with several kinds at once (the people list), the total ("5 titles"). Null with none.
    /// </summary>
    public static string? PersonCount(MediaTileViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var first = item.MediaCounts.FirstOrDefault(count => count.Count > 0);
        return item.RenderAsGroupTile && first is not null
            ? $"{first.Count} {first.Label.ToLowerInvariant()}"
            : GroupCount(item);
    }

    /// <summary>"12 episodes", "1 book": the number with the noun for what the group holds, or null when there is no count.</summary>
    public static string? CountText(MediaTileViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var isGroup = item.IsCollection || item.Presentation == MediaTilePresentation.Album;
        if (!isGroup || item.PreviewTotalCount is not > 0)
        {
            return null;
        }

        var count = item.PreviewTotalCount.Value;
        return $"{count} {Noun(item, count)}";
    }

    /// <summary>
    /// The heading of an episode tile ("S3 E8 · Episode title"). A video from a Videos or Other videos library says its number and title
    /// instead ("3 · Ball handling"): it never carries a season or an S/E code (issue 703).
    /// </summary>
    public static string EpisodeHeading(MediaTileViewModel item, DisplayEpisodeContextDto episode)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(episode);
        return VideoFolderWording.IsVideoItemMediaType(item.MediaKind)
            ? VideoFolderWording.NumberedTitle(episode.EpisodeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture), episode.EpisodeTitle)
            : $"S{episode.SeasonNumber} E{episode.EpisodeNumber} · {episode.EpisodeTitle}";
    }

    /// <summary>
    /// The short episode code a Pick up tile draws just above its progress bar ("S3 E8"). A video from a Videos or Other videos library
    /// never carries a season or an S/E code, so it says only its number; null when there is none to say.
    /// </summary>
    public static string? EpisodeCode(MediaTileViewModel item, DisplayEpisodeContextDto episode)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(episode);
        if (VideoFolderWording.IsVideoItemMediaType(item.MediaKind))
        {
            return episode.EpisodeNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return $"S{episode.SeasonNumber} E{episode.EpisodeNumber}";
    }

    /// <summary>
    /// The first row of a Continue tile (review mock-ups D6): an episode reads "S1 E1: Title" (a video from a Videos library keeps its own
    /// number and title), anything else its title. An episode with no title of its own says just the code.
    /// </summary>
    public static string ContinueHeading(MediaTileViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.EpisodeContext is not { } episode)
        {
            return item.Title;
        }

        if (VideoFolderWording.IsVideoItemMediaType(item.MediaKind))
        {
            return EpisodeHeading(item, episode);
        }

        var code = EpisodeCode(item, episode);
        if (string.IsNullOrWhiteSpace(episode.EpisodeTitle))
        {
            return code ?? episode.ShowTitle;
        }

        return string.IsNullOrWhiteSpace(code) ? episode.EpisodeTitle : $"{code}: {episode.EpisodeTitle}";
    }

    /// <summary>
    /// The second row of a Continue tile (review mock-ups D6): the type in words, then the content rating, then the year ("Film · R · 2023", "Book · 2017"); parts that are not known are left out,
    /// null when there is none. Never a star rating, never the progress.
    /// </summary>
    public static string? ContinueSecondLine(MediaTileViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var parts = new[] { TypeWord(item), item.ContentRating?.Trim(), YearText(item) }.Where(part => !string.IsNullOrWhiteSpace(part));
        var line = string.Join(" · ", parts);
        return line.Length == 0 ? null : line;
    }

    /// <summary>
    /// What a title is, in words, for the description line under a tile ("Film", "Episode", "Book", "Audiobook"): one rule for every kind,
    /// so no tile carries a type label on its picture and no page uses icons for it (issue 1193). Null when the kind is not known.
    /// </summary>
    public static string? TypeWord(MediaTileViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.EpisodeContext is not null)
        {
            return VideoFolderWording.IsVideoItemMediaType(item.MediaKind) ? "Video" : "Episode";
        }

        return (item.MediaKind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "movie" or "movies" => "Film",
            "tv" => "Show",
            "book" or "books" => "Book",
            "audiobook" or "audiobooks" => "Audiobook",
            "comic" or "comics" => "Comic",
            "music" => item.Presentation == MediaTilePresentation.Album ? "Album" : "Song",
            "video" or "videos" => "Video",
            _ => null,
        };
    }

    /// <summary>The percent complete a Continue tile wears as a small badge ("42%"), or null when nothing real has been done yet.</summary>
    public static string? ContinuePercent(MediaTileViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.ProgressPct is > 0 and < 99.5
            ? $"{Math.Round(item.ProgressPct.Value).ToString(System.Globalization.CultureInfo.InvariantCulture)}%"
            : null;
    }

    /// <summary>The noun for what a group holds, singular for one.</summary>
    public static string Noun(MediaTileViewModel item, int count)
    {
        ArgumentNullException.ThrowIfNull(item);
        var one = count == 1;

        // A folder of Other videos is built like a show but holds videos: never "episodes" (item 17).
        if (VideoFolderWording.IsVideoFolderMediaType(item.MediaKind))
        {
            return VideoFolderWording.VideoNoun(count);
        }

        if (item.Presentation == MediaTilePresentation.TvSeries)
        {
            return one ? "episode" : "episodes";
        }

        if (item.Presentation == MediaTilePresentation.Album)
        {
            return one ? "song" : "songs";
        }

        if (item.Presentation == MediaTilePresentation.ComicSeries)
        {
            return one ? "issue" : "issues";
        }

        if (item.Presentation == MediaTilePresentation.AudiobookSeries)
        {
            return one ? "audiobook" : "audiobooks";
        }

        if (item.Presentation == MediaTilePresentation.BookSeries)
        {
            return one ? "book" : "books";
        }

        if (item.Presentation == MediaTilePresentation.MovieSeries)
        {
            return one ? "movie" : "movies";
        }

        return (item.MediaKind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "tv" => one ? "episode" : "episodes",
            "music" => one ? "song" : "songs",
            "comic" or "comics" => one ? "issue" : "issues",
            "audiobook" or "audiobooks" => one ? "audiobook" : "audiobooks",
            "book" or "books" => one ? "book" : "books",
            "movie" or "movies" => one ? "movie" : "movies",
            "video" or "videos" => one ? "video" : "videos",
            _ => one ? "item" : "items",
        };
    }

    /// <summary>
    /// True for a video with no cover art of its own (Other videos, home videos): it uses the two-part tile of D7 and D16 (a still on top,
    /// the title and one detail line below), at poster size, everywhere.
    /// </summary>
    public static bool IsVideoStillTile(MediaTileViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return !item.IsPlaylist
            && item.Person is null
            && (item.MediaKind ?? string.Empty).Trim().ToLowerInvariant() is "video" or "videos";
    }

    /// <summary>
    /// The one small line under a video tile's title: the count for a folder ("3 videos"), otherwise the first fact the card holds
    /// (its length, or "3 videos · Dec 2024" as the Engine composed it). Null when there is nothing to say.
    /// </summary>
    public static string? VideoDetail(MediaTileViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (CountText(item) is { } count)
        {
            return count;
        }

        if (!string.IsNullOrWhiteSpace(item.Subtitle))
        {
            return item.Subtitle;
        }

        return item.HoverFacts.FirstOrDefault(fact => !string.IsNullOrWhiteSpace(fact));
    }

    /// <summary>The rating a card carries (for example "7.8" or "8.1/10"), or null. Used by the Continue tile's second line.</summary>
    public static string? RatingText(MediaTileViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var fact = item.HoverFacts.FirstOrDefault(IsRating);
        if (string.IsNullOrWhiteSpace(fact))
        {
            return null;
        }

        var value = fact.Trim().TrimStart('\u2605').Trim();
        if (value.StartsWith("Rating:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["Rating:".Length..].Trim();
        }
        else if (value.StartsWith("Rating", StringComparison.OrdinalIgnoreCase))
        {
            value = value["Rating".Length..].Trim();
        }

        return value.Length == 0 ? null : $"\u2605 {value}";
    }

    /// <summary>The year a card is known by, or null when it has none.</summary>
    public static string? YearText(MediaTileViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.SortYear is >= 1800 and <= 2200 ? item.SortYear.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static bool IsRating(string? fact) =>
        !string.IsNullOrWhiteSpace(fact)
        && (fact.Contains('\u2605')
            || fact.Contains("IMDb", StringComparison.OrdinalIgnoreCase)
            || fact.Contains("/10", StringComparison.OrdinalIgnoreCase)
            || fact.StartsWith("rating", StringComparison.OrdinalIgnoreCase));
}
