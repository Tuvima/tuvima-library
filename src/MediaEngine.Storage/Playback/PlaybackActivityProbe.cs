using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Playback;

/// <summary>
/// Reads the playback telemetry sessions to answer whether anyone is actively playing media.
/// A session counts only while it is open, in the playing state, and has been heard from recently;
/// paused and abandoned sessions do not.
/// </summary>
public sealed class PlaybackActivityProbe(IDatabaseConnection database, TimeProvider? clock = null) : IPlaybackActivityProbe
{
    /// <summary>Players report every few seconds, so a session silent for longer than this is treated as gone.</summary>
    public static readonly TimeSpan FreshnessWindow = TimeSpan.FromSeconds(90);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public Task<bool> IsAnyonePlayingAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var cutoff = _clock.GetUtcNow().Subtract(FreshnessWindow).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        using var connection = database.CreateConnection();
        var playing = connection.ExecuteScalar<long>("""
            SELECT EXISTS(
                SELECT 1 FROM playback_telemetry_sessions
                 WHERE ended_at IS NULL AND state = 'playing' AND last_observed_at >= @cutoff);
            """, new { cutoff });
        return Task.FromResult(playing != 0);
    }
}
