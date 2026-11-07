namespace MediaEngine.Web.Services.Playback;

/// <summary>Whether the browser has the Remote Playback API (the picker for AirPlay and Google Cast). Unknown until the page has asked the element.</summary>
public enum PlaybackRemoteSupport
{
    Unknown,
    Unsupported,
    Supported,
}

/// <summary>Where playback is: in the browser, starting on a receiver, or on a receiver.</summary>
public enum PlaybackRemoteMode
{
    Local,
    Connecting,
    Remote,
}

/// <summary>What the "Play on another device..." row says and does (unified player v2 D28).</summary>
public enum PlaybackRemoteRowState
{
    /// <summary>"Play on another device...", ready to open the browser's picker.</summary>
    Start,

    /// <summary>"Stop playing on {0}", ready to bring playback back to the browser.</summary>
    Stop,

    /// <summary>Disabled: "Not available in this browser" (no Remote Playback API, as in Firefox).</summary>
    NotInBrowser,

    /// <summary>Disabled: nothing is playing.</summary>
    NothingPlaying,

    /// <summary>Disabled: "Not available for Vault items". A Vault item never casts.</summary>
    Vault,

    /// <summary>Ready, with the helper "Can't play on another device yet": the Engine or the browser did not start it the last time.</summary>
    CannotYet,
}

/// <summary>The pure rules for the row. The Vault check comes first so a Vault item never reaches the Engine or the browser's picker.</summary>
public static class PlaybackRemoteRules
{
    public static PlaybackRemoteRowState Evaluate(PlaybackRemoteSupport support, PlaybackRemoteMode mode, bool hasItem, bool isVault, bool lastAttemptFailed)
    {
        if (isVault)
        {
            return PlaybackRemoteRowState.Vault;
        }

        if (mode != PlaybackRemoteMode.Local)
        {
            return PlaybackRemoteRowState.Stop;
        }

        if (support == PlaybackRemoteSupport.Unsupported)
        {
            return PlaybackRemoteRowState.NotInBrowser;
        }

        if (!hasItem)
        {
            return PlaybackRemoteRowState.NothingPlaying;
        }

        return lastAttemptFailed ? PlaybackRemoteRowState.CannotYet : PlaybackRemoteRowState.Start;
    }

    /// <summary>True for the states where pressing the row does something.</summary>
    public static bool IsEnabled(PlaybackRemoteRowState state) =>
        state is PlaybackRemoteRowState.Start or PlaybackRemoteRowState.Stop or PlaybackRemoteRowState.CannotYet;
}
