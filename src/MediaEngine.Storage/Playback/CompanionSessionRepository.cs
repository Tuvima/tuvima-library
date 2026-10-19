using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Playback;

/// <summary>A device's choice to follow another of the profile's devices, or a position set by hand.</summary>
public sealed record CompanionSessionRecord
{
    public Guid ProfileId { get; init; }
    public string CompanionDeviceId { get; init; } = string.Empty;
    /// <summary>"follow" or "manual".</summary>
    public string Mode { get; init; } = string.Empty;
    public string? FollowedDeviceId { get; init; }
    public Guid? ManualWorkId { get; init; }
    public int? ManualChapterIndex { get; init; }
    public double? ManualPositionSeconds { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class CompanionSessionRepository
{
    private readonly IDatabaseConnection _db;

    public CompanionSessionRepository(IDatabaseConnection db)
    {
        _db = db;
    }

    public Task<CompanionSessionRecord?> GetAsync(Guid profileId, string companionDeviceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<CompanionSessionRecord>("""
            SELECT profile_id AS ProfileId,
                   companion_device_id AS CompanionDeviceId,
                   mode AS Mode,
                   followed_device_id AS FollowedDeviceId,
                   manual_work_id AS ManualWorkId,
                   manual_chapter_index AS ManualChapterIndex,
                   manual_position_seconds AS ManualPositionSeconds,
                   updated_at AS UpdatedAt
            FROM companion_sessions
            WHERE profile_id = @profileId
              AND companion_device_id = @companionDeviceId;
            """, new { profileId, companionDeviceId });
        return Task.FromResult(row);
    }

    /// <summary>Follows another device, replacing any earlier follow or manual position for this companion.</summary>
    public Task UpsertFollowAsync(
        Guid profileId, string companionDeviceId, string followedDeviceId, DateTimeOffset at, CancellationToken ct = default) =>
        UpsertAsync(profileId, companionDeviceId, "follow", followedDeviceId, null, null, null, at, ct);

    /// <summary>Holds a position set by hand, replacing any earlier follow or manual position for this companion.</summary>
    public Task UpsertManualAsync(
        Guid profileId,
        string companionDeviceId,
        Guid workId,
        int? chapterIndex,
        double? positionSeconds,
        DateTimeOffset at,
        CancellationToken ct = default) =>
        UpsertAsync(profileId, companionDeviceId, "manual", null, workId, chapterIndex, positionSeconds, at, ct);

    public Task DeleteAsync(Guid profileId, string companionDeviceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            conn.Execute("""
                DELETE FROM companion_sessions
                WHERE profile_id = @profileId
                  AND companion_device_id = @companionDeviceId;
                """, new { profileId, companionDeviceId }, tx);
        }, ct);
    }

    private Task UpsertAsync(
        Guid profileId,
        string companionDeviceId,
        string mode,
        string? followedDeviceId,
        Guid? manualWorkId,
        int? manualChapterIndex,
        double? manualPositionSeconds,
        DateTimeOffset at,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var updatedAt = at.ToUniversalTime();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            conn.Execute("""
                INSERT INTO companion_sessions
                    (profile_id, companion_device_id, mode, followed_device_id, manual_work_id,
                     manual_chapter_index, manual_position_seconds, updated_at)
                VALUES
                    (@profileId, @companionDeviceId, @mode, @followedDeviceId, @manualWorkId,
                     @manualChapterIndex, @manualPositionSeconds, @updatedAt)
                ON CONFLICT(profile_id, companion_device_id) DO UPDATE SET
                    mode = excluded.mode,
                    followed_device_id = excluded.followed_device_id,
                    manual_work_id = excluded.manual_work_id,
                    manual_chapter_index = excluded.manual_chapter_index,
                    manual_position_seconds = excluded.manual_position_seconds,
                    updated_at = excluded.updated_at;
                """, new
            {
                profileId,
                companionDeviceId,
                mode,
                followedDeviceId,
                manualWorkId,
                manualChapterIndex,
                manualPositionSeconds,
                updatedAt,
            }, tx);
        }, ct);
    }
}
