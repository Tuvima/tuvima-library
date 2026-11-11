namespace MediaEngine.Web.Services.Playback;

/// <summary>Where the lyrics a person is reading came from; the lyrics footer names it ("Synced lyrics · Local file").</summary>
public enum PlaybackLyricsSource
{
    None,
    SongTags,
    LocalFile,
    Lrclib,
    Other,
}
