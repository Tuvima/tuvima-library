using System.Text.Json.Serialization;
using MediaEngine.Domain.Services;

namespace MediaEngine.Web.Services.Playback;

/// <summary>What the phone lock screen and notification shade show for the video that is playing.</summary>
public sealed record VideoMediaSessionMetadata(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string Artist,
    [property: JsonPropertyName("album")] string Album,
    [property: JsonPropertyName("artwork")] IReadOnlyList<VideoMediaSessionArtwork> Artwork)
{
    /// <summary>
    /// An episode reads as episode title, show name and season; a film as title, year and series. Artwork comes from the
    /// same bounded cover proxy the audio players use, largest rendition first.
    /// </summary>
    public static VideoMediaSessionMetadata Create(ListenQueueItem item, string? showTitle, Uri baseUri)
    {
        var episode = VideoPresentationResolver.IsTvEpisode(item);
        var title = StringHelpers.FirstNonBlankOr("Video", episode ? item.EpisodeTitle : null, item.Title);
        var artist = episode ? StringHelpers.FirstNonBlankOr(string.Empty, showTitle, item.Album) : StringHelpers.FirstNonBlankOr(string.Empty, item.Year);
        var album = episode
            ? StringHelpers.FirstNonBlankOr(string.Empty, string.IsNullOrWhiteSpace(item.SeasonNumber) ? null : $"Season {item.SeasonNumber}", item.Album)
            : StringHelpers.FirstNonBlankOr(string.Empty, item.Album);
        var artwork = new[] { "l", "m" }
            .Select(size => PlaybackArtworkUrl.ForItem(item, size))
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => new VideoMediaSessionArtwork(new Uri(baseUri, source!).ToString()))
            .ToList();
        return new(title, artist, album, artwork);
    }
}

public sealed record VideoMediaSessionArtwork([property: JsonPropertyName("src")] string Src);
