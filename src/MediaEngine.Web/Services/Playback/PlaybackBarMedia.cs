namespace MediaEngine.Web.Services.Playback;

/// <summary>The four kinds of media the one persistent bar is drawn for (unified player v2 D1, D3, D4).</summary>
public enum PlaybackBarMedia
{
    Music,
    Audiobook,
    Tv,
    Movie,
}

public static class PlaybackBarMediaResolver
{
    /// <summary>
    /// Music and audiobooks follow the item's playback experience; video is a TV episode when it belongs to a show and a movie otherwise
    /// (other video, such as a course lesson, has a movie's controls).
    /// </summary>
    public static PlaybackBarMedia For(ListenQueueItem item) => item.PlaybackExperience switch
    {
        PlaybackExperience.Audiobook => PlaybackBarMedia.Audiobook,
        PlaybackExperience.Video => VideoPresentationResolver.IsTvEpisode(item) ? PlaybackBarMedia.Tv : PlaybackBarMedia.Movie,
        _ => PlaybackBarMedia.Music,
    };
}
