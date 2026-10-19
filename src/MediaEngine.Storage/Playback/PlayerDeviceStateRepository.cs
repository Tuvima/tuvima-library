using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Playback;

/// <summary>
/// One device's latest playback state on a profile, written from that device's own heartbeats. The shared player
/// (<c>player_sessions</c>) holds a single queue and transport per profile; this record is separate so two devices on
/// one profile never overwrite each other's live state.
/// </summary>
public sealed record PlayerDeviceStateRecord
{
    public Guid ProfileId { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public string Client { get; init; } = string.Empty;
    public Guid? PlayerSessionId { get; init; }
    public Guid AssetId { get; init; }
    public Guid? WorkId { get; init; }
    /// <summary>"playing", "paused" or "ended".</summary>
    public string PlaybackState { get; init; } = string.Empty;
    public double PositionSeconds { get; init; }
    public double? DurationSeconds { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class PlayerDeviceStateRepository
{
    /// <summary>Rows not refreshed for this long are removed the next time the profile writes one.</summary>
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(24);

    private const string SelectColumns = """
        SELECT profile_id AS ProfileId,
               device_id AS DeviceId,
               client AS Client,
               player_session_id AS PlayerSessionId,
               asset_id AS AssetId,
               work_id AS WorkId,
               playback_state AS PlaybackState,
               position_seconds AS PositionSeconds,
               duration_seconds AS DurationSeconds,
               updated_at AS UpdatedAt
        FROM player_device_states
        """;

    private readonly IDatabaseConnection _db;

    public PlayerDeviceStateRepository(IDatabaseConnection db)
    {
        _db = db;
    }

    /// <summary>
    /// Inserts or replaces the device's row and, in the same write, removes this profile's rows that have not been
    /// refreshed within <see cref="RetentionWindow"/>.
    /// </summary>
    public Task UpsertAsync(PlayerDeviceStateRecord state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var updatedAt = state.UpdatedAt.ToUniversalTime();
        var cutoff = updatedAt - RetentionWindow;
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            conn.Execute("""
                INSERT INTO player_device_states
                    (profile_id, device_id, client, player_session_id, asset_id, work_id,
                     playback_state, position_seconds, duration_seconds, updated_at)
                VALUES
                    (@ProfileId, @DeviceId, @Client, @PlayerSessionId, @AssetId, @WorkId,
                     @PlaybackState, @PositionSeconds, @DurationSeconds, @updatedAt)
                ON CONFLICT(profile_id, device_id) DO UPDATE SET
                    client = excluded.client,
                    player_session_id = excluded.player_session_id,
                    asset_id = excluded.asset_id,
                    work_id = excluded.work_id,
                    playback_state = excluded.playback_state,
                    position_seconds = excluded.position_seconds,
                    duration_seconds = excluded.duration_seconds,
                    updated_at = excluded.updated_at;
                """, new
            {
                state.ProfileId,
                state.DeviceId,
                state.Client,
                state.PlayerSessionId,
                state.AssetId,
                state.WorkId,
                state.PlaybackState,
                PositionSeconds = Math.Max(0, state.PositionSeconds),
                DurationSeconds = state.DurationSeconds.HasValue ? Math.Max(0, state.DurationSeconds.Value) : (double?)null,
                updatedAt,
            }, tx);

            conn.Execute("""
                DELETE FROM player_device_states
                WHERE profile_id = @ProfileId
                  AND updated_at < @cutoff;
                """, new { state.ProfileId, cutoff }, tx);
        }, ct);
    }

    /// <summary>Marks the device's row ended (playback stopped or finished); a missing row is left alone.</summary>
    public Task MarkEndedAsync(Guid profileId, string deviceId, DateTimeOffset at, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var updatedAt = at.ToUniversalTime();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            conn.Execute("""
                UPDATE player_device_states
                SET playback_state = 'ended',
                    updated_at = @updatedAt
                WHERE profile_id = @profileId
                  AND device_id = @deviceId;
                """, new { profileId, deviceId, updatedAt }, tx);
        }, ct);
    }

    /// <summary>Every row for the profile, any age; the caller applies freshness against an explicit clock.</summary>
    public Task<IReadOnlyList<PlayerDeviceStateRecord>> ListAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<PlayerDeviceStateRecord>(
            SelectColumns + " WHERE profile_id = @profileId ORDER BY updated_at DESC, device_id;",
            new { profileId }).ToList();
        return Task.FromResult<IReadOnlyList<PlayerDeviceStateRecord>>(rows);
    }

    public Task<PlayerDeviceStateRecord?> GetAsync(Guid profileId, string deviceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<PlayerDeviceStateRecord>(
            SelectColumns + " WHERE profile_id = @profileId AND device_id = @deviceId;",
            new { profileId, deviceId });
        return Task.FromResult(row);
    }
}
