namespace MediaEngine.Web.Components.Shared;

public enum PlaybackUtilityGlyphKind
{
    Queue,
    NextUp,
    History,
    Lyrics,
    Chapters,
    Bookmark,
    Sleep,
    Speed,
    Mute,
    Volume,
    VolumeLow,
    Shuffle,
    Repeat,
    Captions,
    AudioTrack,
    Quality,
    Fullscreen,
    PictureInPicture,
    Cast,
    Favorite,
    Popup,
    Expand,
    Restore,
    Collapse,
    Close,
    More,
    Back,
    Like,
    Dislike,
    Add,
    Check,
    Settings,
    Previous,
    Next,
    Play,
    Pause,
    NowPlaying,
    Episodes,
    AudioSubtitles,
    Playlist,
    Clock,
    Disc,
    Person,
    Reset,
    Book,
    Film,
    SubtitleAppearance,
    Search,
    ChevronRight,
    ChevronDown,
    SkipBack,
    SkipForward,
    Info,
    OpenExternal,
}

public static class PlaybackUtilityGlyphMap
{
    public static bool TryFromControlKey(PlaybackControlKey key, out PlaybackUtilityGlyphKind glyph)
    {
        glyph = key switch
        {
            PlaybackControlKey.Queue => PlaybackUtilityGlyphKind.Queue,
            PlaybackControlKey.History => PlaybackUtilityGlyphKind.History,
            PlaybackControlKey.Lyrics => PlaybackUtilityGlyphKind.Lyrics,
            PlaybackControlKey.Chapters => PlaybackUtilityGlyphKind.Chapters,
            PlaybackControlKey.Bookmarks => PlaybackUtilityGlyphKind.Bookmark,
            PlaybackControlKey.SleepTimer => PlaybackUtilityGlyphKind.Sleep,
            PlaybackControlKey.Speed => PlaybackUtilityGlyphKind.Speed,
            PlaybackControlKey.Settings => PlaybackUtilityGlyphKind.Settings,
            PlaybackControlKey.Mute => PlaybackUtilityGlyphKind.Mute,
            PlaybackControlKey.Volume => PlaybackUtilityGlyphKind.Volume,
            PlaybackControlKey.Shuffle => PlaybackUtilityGlyphKind.Shuffle,
            PlaybackControlKey.Repeat => PlaybackUtilityGlyphKind.Repeat,
            PlaybackControlKey.Captions => PlaybackUtilityGlyphKind.Captions,
            PlaybackControlKey.AudioTrack => PlaybackUtilityGlyphKind.AudioTrack,
            PlaybackControlKey.Quality => PlaybackUtilityGlyphKind.Quality,
            PlaybackControlKey.Fullscreen => PlaybackUtilityGlyphKind.Fullscreen,
            PlaybackControlKey.PictureInPicture => PlaybackUtilityGlyphKind.PictureInPicture,
            PlaybackControlKey.Cast => PlaybackUtilityGlyphKind.Cast,
            PlaybackControlKey.Favorite => PlaybackUtilityGlyphKind.Favorite,
            PlaybackControlKey.Close => PlaybackUtilityGlyphKind.Close,
            PlaybackControlKey.Expand => PlaybackUtilityGlyphKind.Expand,
            PlaybackControlKey.Resume => PlaybackUtilityGlyphKind.Restore,
            PlaybackControlKey.More => PlaybackUtilityGlyphKind.More,
            PlaybackControlKey.PreviousItem => PlaybackUtilityGlyphKind.Previous,
            PlaybackControlKey.NextItem => PlaybackUtilityGlyphKind.Next,
            PlaybackControlKey.NowPlaying => PlaybackUtilityGlyphKind.NowPlaying,
            PlaybackControlKey.Episodes => PlaybackUtilityGlyphKind.Episodes,
            PlaybackControlKey.AudioSubtitles => PlaybackUtilityGlyphKind.AudioSubtitles,
            PlaybackControlKey.Like => PlaybackUtilityGlyphKind.Like,
            PlaybackControlKey.Dislike => PlaybackUtilityGlyphKind.Dislike,
            _ => default,
        };

        return key is PlaybackControlKey.Queue or PlaybackControlKey.History or PlaybackControlKey.Lyrics
            or PlaybackControlKey.Chapters or PlaybackControlKey.Bookmarks or PlaybackControlKey.SleepTimer
            or PlaybackControlKey.Speed or PlaybackControlKey.Settings or PlaybackControlKey.Mute or PlaybackControlKey.Volume
            or PlaybackControlKey.Shuffle or PlaybackControlKey.Repeat or PlaybackControlKey.Captions
            or PlaybackControlKey.AudioTrack or PlaybackControlKey.Quality or PlaybackControlKey.Fullscreen
            or PlaybackControlKey.PictureInPicture or PlaybackControlKey.Cast or PlaybackControlKey.Favorite
            or PlaybackControlKey.Close or PlaybackControlKey.Expand or PlaybackControlKey.Resume or PlaybackControlKey.More
            or PlaybackControlKey.PreviousItem or PlaybackControlKey.NextItem or PlaybackControlKey.NowPlaying
            or PlaybackControlKey.Episodes or PlaybackControlKey.AudioSubtitles
            or PlaybackControlKey.Like or PlaybackControlKey.Dislike;
    }
}
