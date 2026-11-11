namespace MediaEngine.Web.Components.Player;

/// <summary>
/// Decides whether the speaker icon shows "muted". Sound-on is the default: the icon only shows muted when
/// the person muted it, or after the player has reported a real level of zero. A level that has not been
/// reported yet (still the initial zero before the first player report) never counts as muted.
/// </summary>
public static class PlaybackMuteDisplay
{
    public static bool ShowsMuted(bool isMuted, double volume, bool volumeReported) =>
        isMuted || (volumeReported && volume <= 0d);
}
