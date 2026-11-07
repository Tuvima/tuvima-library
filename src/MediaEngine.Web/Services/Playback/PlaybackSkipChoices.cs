using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Playback;

public enum PlaybackSkipDirection
{
    Back,
    Forward,
}

/// <summary>
/// The skip lengths Playback settings offers (unified player v2 D14) and the change each choice makes to the profile's playback preferences.
/// Video keeps its own pair in the Watching settings and audio in the Listening settings, the same two pairs Settings used to hold; the lengths
/// offered are the ones the Engine accepts for each.
/// </summary>
public static class PlaybackSkipChoices
{
    private static readonly int[] WatchingBack = [5, 10, 15, 30];
    private static readonly int[] WatchingForward = [10, 30, 60, 90];
    private static readonly int[] ListeningBack = [10, 15, 30];
    private static readonly int[] ListeningForward = [15, 30, 60];

    public static IReadOnlyList<int> Options(bool video, PlaybackSkipDirection direction) => (video, direction) switch
    {
        (true, PlaybackSkipDirection.Back) => WatchingBack,
        (true, _) => WatchingForward,
        (false, PlaybackSkipDirection.Back) => ListeningBack,
        _ => ListeningForward,
    };

    /// <summary>The length the profile chose for this media, read from its saved preferences (the defaults when nothing is saved).</summary>
    public static int Current(UserPlaybackSettingsDto settings, bool video, PlaybackSkipDirection direction) => (video, direction) switch
    {
        (true, PlaybackSkipDirection.Back) => settings.Watching.SkipBackSeconds,
        (true, _) => settings.Watching.SkipForwardSeconds,
        (false, PlaybackSkipDirection.Back) => settings.Listening.SkipBackSeconds,
        _ => settings.Listening.SkipForwardSeconds,
    };

    /// <summary>Sets one length for this media.</summary>
    public static Action<UserPlaybackSettingsDto> Set(bool video, PlaybackSkipDirection direction, int seconds) => settings =>
    {
        switch (video, direction)
        {
            case (true, PlaybackSkipDirection.Back): settings.Watching.SkipBackSeconds = seconds; break;
            case (true, _): settings.Watching.SkipForwardSeconds = seconds; break;
            case (false, PlaybackSkipDirection.Back): settings.Listening.SkipBackSeconds = seconds; break;
            default: settings.Listening.SkipForwardSeconds = seconds; break;
        }
    };

    /// <summary>Puts both pairs back to the app's defaults (the "Reset playback preferences" footer).</summary>
    public static void ResetLengths(UserPlaybackSettingsDto settings)
    {
        var watching = new WatchingSettingsDto();
        var listening = new ListeningSettingsDto();
        settings.Watching.SkipBackSeconds = watching.SkipBackSeconds;
        settings.Watching.SkipForwardSeconds = watching.SkipForwardSeconds;
        settings.Listening.SkipBackSeconds = listening.SkipBackSeconds;
        settings.Listening.SkipForwardSeconds = listening.SkipForwardSeconds;
    }
}
