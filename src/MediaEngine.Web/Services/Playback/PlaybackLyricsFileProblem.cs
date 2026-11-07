namespace MediaEngine.Web.Services.Playback;

/// <summary>Why a lyrics file the person picked was not added; the lyrics panel turns each into a plain sentence.</summary>
public enum PlaybackLyricsFileProblem
{
    None,

    /// <summary>It is not a .lrc or .txt file.</summary>
    WrongType,

    /// <summary>It is bigger than 5 MB.</summary>
    TooBig,

    /// <summary>The browser could not read it.</summary>
    Unreadable,
}
