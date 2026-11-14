using MediaEngine.Contracts.Playback;

namespace MediaEngine.Web.Services.Playback;

/// <summary>What a video player draws while the Engine gets a title ready.</summary>
public enum PlaybackWaitingKind
{
    /// <summary>The Engine is repackaging, converting sound or encoding the picture.</summary>
    Working,

    /// <summary>Another title is being prepared; this one waits for a free slot and can be cancelled.</summary>
    WaitingForSlot,

    /// <summary>Preparation failed: one sentence, Try again and Show details.</summary>
    Failed,
}

/// <summary>
/// The waiting state of the video player, read from the manifest's <see cref="PlaybackPathDto"/> (playback-path spec D8, section 4).
/// <see cref="Line"/> is the Engine's own wording and is shown as it arrives so every client says the same words;
/// it is empty only when the Engine sent none, and the player then shows its neutral line.
/// </summary>
/// <param name="Kind">Which of the three waiting states to draw.</param>
/// <param name="Line">The Engine's waiting line (or its failure sentence), or empty.</param>
/// <param name="Percent">How much is prepared, 0 to 100, once the Engine knows; null before.</param>
/// <param name="ExpectSoon">True while the picture is being encoded and no percent is known yet ("Playback starts in a moment").</param>
/// <param name="Detail">The Engine's own text for "Show details" on a failure; null otherwise.</param>
public sealed record PlaybackWaitingView(PlaybackWaitingKind Kind, string Line, int? Percent, bool ExpectSoon, string? Detail)
{
    public bool HasLine => !string.IsNullOrWhiteSpace(Line);
}

public static class PlaybackWaitingRules
{
    /// <summary>Shown only when the Engine sent no wording (an older Engine, or the first moments before the manifest arrives); never a bare "Preparing video…".</summary>
    public const string NeutralLine = "Getting this ready";

    /// <summary>The one failure sentence when the Engine sent none (spec section 4, mockup screen 1).</summary>
    public const string NeutralFailure = "This file could not be prepared for this player.";

    /// <summary>
    /// The waiting state while a title is prepared. A direct play (or a path the Engine marks ready) never gets one, so this is only called for a
    /// manifest that is still preparing; a missing path (an older Engine) still gets a neutral working state, never an empty player.
    /// </summary>
    public static PlaybackWaitingView ForPreparing(PlaybackPathDto? path)
    {
        if (path is null)
        {
            return new PlaybackWaitingView(PlaybackWaitingKind.Working, string.Empty, null, false, null);
        }

        if (path.Phase == PlaybackPathPhases.WaitingForSlot)
        {
            return new PlaybackWaitingView(PlaybackWaitingKind.WaitingForSlot, path.WaitingLabel, null, false, null);
        }

        if (path.Phase == PlaybackPathPhases.Failed)
        {
            return ForFailure(path, path.FailureDetail);
        }

        var percent = path.PercentPrepared is { } value ? Math.Clamp(value, 0, 100) : (int?)null;
        return new PlaybackWaitingView(
            PlaybackWaitingKind.Working,
            path.WaitingLabel,
            percent,
            ExpectSoon: path.Rung == PlaybackRungs.EncodePicture && percent is null,
            Detail: null);
    }

    /// <summary>
    /// The failed state: the Engine's one sentence when it sent one, and its own error text behind Show details. <paramref name="detail"/> is the
    /// player's message when the Engine sent none.
    /// </summary>
    public static PlaybackWaitingView ForFailure(PlaybackPathDto? path, string? detail)
    {
        var text = !string.IsNullOrWhiteSpace(path?.FailureDetail) ? path!.FailureDetail : detail;
        return new PlaybackWaitingView(
            PlaybackWaitingKind.Failed,
            path?.WaitingLabel ?? string.Empty,
            null,
            false,
            string.IsNullOrWhiteSpace(text) ? null : text);
    }
}
