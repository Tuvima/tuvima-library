using MediaEngine.Web.Services.MediaTiles;

namespace MediaEngine.Web.Services.Playback;

/// <summary>Bounded artwork shared by audio presentations, including restored legacy book queues.</summary>
public static class PlaybackArtworkUrl
{
    public static string? ForItem(ListenQueueItem? item, string size = "m") => item is null ? null
        : item.PlaybackExperience == PlaybackExperience.Audiobook
            ? AudiobookCover(item.AudiobookWorkId ?? item.AlbumWorkId ?? item.WorkId, item.CoverUrl, size)
            : MusicCover(item.AlbumWorkId ?? item.WorkId, item.CoverUrl, size);

    private static string? MusicCover(Guid? workId, string? coverUrl, string size)
    {
        if (!IsRendition(size)) return null;
        if (BoundedSource(coverUrl, size) is { } bounded) return bounded;
        return workId is { } id && id != Guid.Empty
            ? $"/engine-image/stream/entity/work/{id:D}/cover?size={size.ToLowerInvariant()}"
            : string.IsNullOrWhiteSpace(coverUrl) ? null : coverUrl;
    }

    public static string? AudiobookCover(Guid? bookWorkId, string? coverUrl, string size = "m")
    {
        if (BoundedSource(coverUrl, size) is { } bounded) return bounded;
        // Recording-cover endpoints have no rendition parameter. Resolve the owned book's
        // canonical cover through the existing authorized, rendition-aware proxy instead.
        return bookWorkId is { } id && id != Guid.Empty && IsRendition(size)
            ? $"/engine-image/stream/entity/work/{id:D}/cover?size={size.ToLowerInvariant()}"
            : null;
    }

    private static string? BoundedSource(string? source, string size)
    {
        if (!IsRendition(size) || string.IsNullOrWhiteSpace(source)) return null;
        if (MediaTileArtworkUrl.Sized(source, size) is { } sized) return sized;
        var path = source.Split('?', '#')[0];
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") path = uri.AbsolutePath;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 4 && segments[0].Equals("engine-image", StringComparison.OrdinalIgnoreCase)) segments = segments[1..];
        if (segments.Length == 3 && segments[0].Equals("stream", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[1], out _) && segments[2].Equals("cover", StringComparison.OrdinalIgnoreCase)) return null;
        // Some already-delivered renditions have a different route family. Keep that
        // exact bounded URL; inventing s/m/l siblings would misrepresent its contract.
        var queryStart = source.IndexOf('?');
        if (queryStart < 0) return null;
        var query = source[(queryStart + 1)..].Split('#')[0];
        return query.Split('&').Any(parameter =>
            parameter.StartsWith("size=", StringComparison.OrdinalIgnoreCase) && IsRendition(parameter[5..]))
                ? source : null;
    }

    private static bool IsRendition(string size) => size.ToLowerInvariant() is "s" or "m" or "l";
}
