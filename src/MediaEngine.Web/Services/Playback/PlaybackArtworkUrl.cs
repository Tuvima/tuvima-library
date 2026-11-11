using MediaEngine.Web.Services.MediaTiles;

namespace MediaEngine.Web.Services.Playback;

/// <summary>Bounded artwork shared by audio presentations, including restored book queues.</summary>
public static class PlaybackArtworkUrl
{
    /// <summary>The copy for a cover drawn at <paramref name="drawnPx"/> CSS pixels (same rule as <see cref="MediaTileArtworkUrl.ForDrawnSize"/>).</summary>
    public static string? ForDrawnSize(ListenQueueItem? item, int drawnPx) => ForItem(item, ArtworkSize.ForPixels(drawnPx));

    /// <summary>A "1x, 2x, 3x" source list for a cover drawn at a fixed size; null when every density uses the same address.</summary>
    public static string? SrcSetForDrawnSize(ListenQueueItem? item, int drawnPx)
    {
        if (item is null || drawnPx <= 0)
        {
            return null;
        }

        var urls = new[] { 1, 2, 3 }.Select(density => ForDrawnSize(item, drawnPx * density)).ToArray();
        return urls.Any(string.IsNullOrWhiteSpace) || urls.All(url => url == urls[0])
            ? null
            : string.Join(", ", urls.Select((url, index) => $"{url} {index + 1}x"));
    }

    /// <summary>
    /// The picture for the bar's fixed artwork slot (unified player v2 D5). Music and audiobooks fill a square slot with the cover. TV and movies
    /// fill a 100 x 56 landscape slot: the episode's still when there is one, otherwise the poster, which the slot draws whole on a blurred fill
    /// (<see cref="PlaybackSlotArtwork.IsPoster"/>) and never stretches. Nothing found means the slot draws its placeholder.
    /// </summary>
    /// <param name="media">Which slot shape applies.</param>
    /// <param name="item">The playing item.</param>
    /// <param name="stillUrl">The episode still from the presentation context, if it is known yet.</param>
    public static PlaybackSlotArtwork ForBarSlot(PlaybackBarMedia media, ListenQueueItem? item, string? stillUrl = null)
    {
        if (item is null)
        {
            return PlaybackSlotArtwork.None;
        }

        if (media is PlaybackBarMedia.Music or PlaybackBarMedia.Audiobook)
        {
            return new(ForDrawnSize(item, SlotHeightPx), SrcSetForDrawnSize(item, SlotHeightPx), false);
        }

        if (!string.IsNullOrWhiteSpace(stillUrl) && MediaTileArtworkUrl.SupportsRenditions(stillUrl))
        {
            return new(MediaTileArtworkUrl.ForDrawnSize(stillUrl, LandscapeSlotWidthPx),
                MediaTileArtworkUrl.SrcSetForDrawnSize(stillUrl, LandscapeSlotWidthPx), false);
        }

        var poster = ForDrawnSize(item, SlotHeightPx);
        return string.IsNullOrWhiteSpace(poster) ? PlaybackSlotArtwork.None : new(poster, SrcSetForDrawnSize(item, SlotHeightPx), true);
    }

    /// <summary>The height of the bar's artwork slot in CSS pixels at desktop widths.</summary>
    public const int SlotHeightPx = 56;

    /// <summary>The width of the landscape slot video uses.</summary>
    public const int LandscapeSlotWidthPx = 100;

    /// <summary>The medium cover again for a retry after a failed load; attempt 0 is the plain address. The extra value only avoids a cached failure.</summary>
    public static string? ForItemRetry(ListenQueueItem? item, int attempt)
    {
        var url = ForItem(item);
        return url is null || attempt <= 0 ? url : url + (url.Contains('?') ? "&" : "?") + "retry=" + attempt;
    }

    public static string? ForItem(ListenQueueItem? item, string size = "m") => item is null ? null
        : item.PlaybackExperience == PlaybackExperience.Audiobook
            ? AudiobookCover(item.AudiobookWorkId ?? item.WorkId, item.CoverUrl, size)
            : MusicCover(item.AlbumWorkId ?? item.WorkId, item.CoverUrl, size);

    private static string? MusicCover(Guid? workId, string? coverUrl, string size)
    {
        if (!IsRendition(size))
        {
            return null;
        }
        if (BoundedSource(coverUrl, size) is { } bounded)
        {
            return bounded;
        }
        return workId is { } id && id != Guid.Empty
            ? $"/engine-image/stream/entity/work/{id:D}/cover?size={size.ToLowerInvariant()}"
            : string.IsNullOrWhiteSpace(coverUrl) ? null : coverUrl;
    }

    public static string? AudiobookCover(Guid? bookWorkId, string? coverUrl, string size = "m")
    {
        if (BoundedSource(coverUrl, size) is { } bounded)
        {
            return bounded;
        }
        // Recording-cover endpoints have no rendition parameter. Resolve the owned book's
        // canonical cover through the existing authorized, rendition-aware proxy instead.
        return bookWorkId is { } id && id != Guid.Empty && IsRendition(size)
            ? $"/engine-image/stream/entity/work/{id:D}/cover?size={size.ToLowerInvariant()}"
            : null;
    }

    private static string? BoundedSource(string? source, string size)
    {
        if (!IsRendition(size) || string.IsNullOrWhiteSpace(source))
        {
            return null;
        }
        var path = source.Split('?', '#')[0];
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            path = uri.AbsolutePath;
        }
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 4 && segments[0].Equals("engine-image", StringComparison.OrdinalIgnoreCase))
        {
            segments = segments[1..];
        }
        if (segments.Length == 3 && segments[0].Equals("stream", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[1], out _) && segments[2].Equals("cover", StringComparison.OrdinalIgnoreCase))
        {
            // A recording's own cover is not the book's cover: callers fall back to the owned work.
            return null;
        }
        if (MediaTileArtworkUrl.Sized(source, size) is { } sized)
        {
            return sized;
        }
        // Some already-delivered renditions have a different route family. Keep that
        // exact bounded URL; inventing s/m/l siblings would misrepresent its contract.
        var queryStart = source.IndexOf('?');
        if (queryStart < 0)
        {
            return null;
        }
        var query = source[(queryStart + 1)..].Split('#')[0];
        return query.Split('&').Any(parameter =>
            parameter.StartsWith("size=", StringComparison.OrdinalIgnoreCase) && IsRendition(parameter[5..]))
                ? source : null;
    }

    private static bool IsRendition(string size) => ArtworkSize.IsKnown(size.ToLowerInvariant());
}

/// <summary>What the bar's artwork slot draws. <paramref name="IsPoster"/> means a portrait poster in a landscape slot: contained on a blurred fill.</summary>
public sealed record PlaybackSlotArtwork(string? Src, string? SrcSet, bool IsPoster)
{
    public static PlaybackSlotArtwork None { get; } = new(null, null, false);
}
