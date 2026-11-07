namespace MediaEngine.Web.Services.Playback;

/// <summary>What pressing Previous on a music player does (unified player v2 D3).</summary>
public enum PlaybackPreviousAction
{
    /// <summary>Start the current song again.</summary>
    Restart,
    /// <summary>Go to the song before it.</summary>
    GoToPrevious,
}

/// <summary>
/// The convention Spotify and Apple Music use: after the threshold of play (<c>music_previous_restart_seconds</c> in
/// <c>config/ui/playback-client.json</c>, 3 s) Previous restarts the song; inside the first threshold seconds, or pressed again within the
/// threshold of a restart, it goes to the previous song; at the start of the queue it restarts. One instance per player session so every
/// surface (the bar, the lock screen's Previous, a headset) shares the "second press" memory.
/// </summary>
public sealed class MusicPreviousRule(double thresholdSeconds, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private Guid? _restartedItem;
    private DateTimeOffset _restartedAt;

    /// <summary>Seconds of play after which Previous restarts instead of going back.</summary>
    public double ThresholdSeconds { get; } = thresholdSeconds;

    /// <summary>Decides the press and remembers a restart so an immediate second press goes back.</summary>
    /// <param name="itemKey">Identity of the song that is playing (its asset), so a restart of one song never counts for another.</param>
    /// <param name="positionSeconds">How far into the song playback is.</param>
    /// <param name="hasPreviousSong">False at the start of the queue.</param>
    public PlaybackPreviousAction Press(Guid? itemKey, double positionSeconds, bool hasPreviousSong)
    {
        var now = _clock.GetUtcNow();
        if (!hasPreviousSong)
        {
            Remember(itemKey, now);
            return PlaybackPreviousAction.Restart;
        }

        var justRestarted = itemKey is not null
            && _restartedItem == itemKey
            && (now - _restartedAt).TotalSeconds < ThresholdSeconds;
        if (justRestarted || positionSeconds < ThresholdSeconds)
        {
            _restartedItem = null;
            return PlaybackPreviousAction.GoToPrevious;
        }

        Remember(itemKey, now);
        return PlaybackPreviousAction.Restart;
    }

    private void Remember(Guid? itemKey, DateTimeOffset now)
    {
        _restartedItem = itemKey;
        _restartedAt = now;
    }
}
