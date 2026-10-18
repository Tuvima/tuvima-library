using Dapper;
using MediaEngine.Contracts.Playback;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Playback;

public sealed class MusicPlayStatsRepository
{
    private const double DefaultQualificationSeconds = 30d;
    private const double ShortTrackBoundarySeconds = 30d;
    private const double PositionToleranceSeconds = 3d;
    private static readonly TimeSpan ActiveSegmentGap = TimeSpan.FromSeconds(45);

    private readonly IDatabaseConnection _db;
    private readonly TimeProvider _timeProvider;

    public MusicPlayStatsRepository(
        IDatabaseConnection db,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyDictionary<Guid, MusicPlayStat>> GetStatsAsync(
        Guid profileId,
        IEnumerable<Guid> workIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var requestedIds = workIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (requestedIds.Length == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<Guid, MusicPlayStat>>(
                new Dictionary<Guid, MusicPlayStat>());
        }

        using var conn = _db.CreateConnection();
        var requestedIdHex = requestedIds
            .Select(id => Convert.ToHexString(GuidSql.ToBlob(id)))
            .ToArray();
        var rows = conn.Query<MusicPlayStatRow>("""
            SELECT work_id AS WorkId,
                   play_count AS PlayCount,
                   last_played_at AS LastPlayedAt
            FROM music_play_stats
            WHERE profile_id = @profileId
              AND HEX(work_id) IN @requestedIdHex;
            """, new { profileId, requestedIdHex });

        return Task.FromResult<IReadOnlyDictionary<Guid, MusicPlayStat>>(
            rows.ToDictionary(
                row => row.WorkId,
                row => new MusicPlayStat(row.PlayCount, row.LastPlayedAt)));
    }

    public Task TrackHeartbeatAsync(
        Guid profileId,
        PlayerQueueItemDto item,
        PlayerHeartbeatDto heartbeat,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (item.WorkId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            var now = _timeProvider.GetUtcNow();
            var active = conn.QueryFirstOrDefault<ActiveSegmentRow>("""
                SELECT profile_id AS ProfileId,
                       work_id AS WorkId,
                       asset_id AS AssetId,
                       queue_item_id AS QueueItemId,
                       started_at AS StartedAt,
                       last_position_seconds AS LastPositionSeconds,
                       listened_seconds AS ListenedSeconds,
                       duration_seconds AS DurationSeconds,
                       qualified AS Qualified,
                       last_heartbeat_at AS LastHeartbeatAt
                FROM music_play_active_segments
                WHERE profile_id = @profileId
                LIMIT 1;
                """, new { profileId }, tx);

            if (!heartbeat.IsPlaying)
            {
                if (active is not null)
                {
                    UpdateAndQualify(conn, tx, active, item, heartbeat, now);
                    ClearActive(conn, tx, profileId);
                }

                return;
            }

            if (active is null || ShouldRestart(active, item, heartbeat, now))
            {
                UpsertActive(conn, tx, profileId, item, heartbeat, now);
                return;
            }

            UpdateAndQualify(conn, tx, active, item, heartbeat, now);
        }, ct);
    }

    private static bool ShouldRestart(
        ActiveSegmentRow active,
        PlayerQueueItemDto item,
        PlayerHeartbeatDto heartbeat,
        DateTimeOffset now)
    {
        if (active.WorkId != item.WorkId || active.AssetId != item.AssetId)
        {
            return true;
        }

        var queueItemId = heartbeat.QueueItemId ?? item.QueueItemId;
        if (queueItemId != Guid.Empty
            && active.QueueItemId.HasValue
            && queueItemId != active.QueueItemId.Value)
        {
            return true;
        }

        if (now - active.LastHeartbeatAt > ActiveSegmentGap)
        {
            return true;
        }

        return heartbeat.PositionSeconds + PositionToleranceSeconds < active.LastPositionSeconds;
    }

    private static void UpdateAndQualify(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction transaction,
        ActiveSegmentRow active,
        PlayerQueueItemDto item,
        PlayerHeartbeatDto heartbeat,
        DateTimeOffset now)
    {
        var duration = heartbeat.DurationSeconds ?? active.DurationSeconds ?? item.DurationSeconds;
        var elapsedSeconds = Math.Max(0d, (now - active.LastHeartbeatAt).TotalSeconds);
        var positionDelta = Math.Max(0d, heartbeat.PositionSeconds - active.LastPositionSeconds);
        var playbackRate = Math.Clamp(heartbeat.PlaybackRate ?? 1d, 0.5d, 2d);
        var maximumCreditableDelta = elapsedSeconds * playbackRate + PositionToleranceSeconds;
        var creditedDelta = positionDelta <= maximumCreditableDelta
            ? positionDelta
            : 0d;
        var listenedSeconds = active.ListenedSeconds + creditedDelta;
        var qualified = active.Qualified;
        var qualificationSeconds = duration is > 0 and < ShortTrackBoundarySeconds
            ? duration.Value * 0.5d
            : DefaultQualificationSeconds;

        if (!qualified && listenedSeconds + 0.01d >= qualificationSeconds)
        {
            conn.Execute("""
                INSERT INTO music_play_stats
                    (profile_id, work_id, play_count, last_played_at)
                VALUES
                    (@profileId, @workId, 1, @now)
                ON CONFLICT(profile_id, work_id) DO UPDATE SET
                    play_count = music_play_stats.play_count + 1,
                    last_played_at = excluded.last_played_at;
                """, new
            {
                profileId = active.ProfileId,
                workId = active.WorkId,
                now,
            }, transaction);
            qualified = true;
        }

        conn.Execute("""
            UPDATE music_play_active_segments
            SET last_position_seconds = @positionSeconds,
                listened_seconds = @listenedSeconds,
                duration_seconds = COALESCE(@durationSeconds, duration_seconds),
                qualified = @qualified,
                last_heartbeat_at = @now
            WHERE profile_id = @profileId;
            """, new
        {
            profileId = active.ProfileId,
            positionSeconds = Math.Max(0d, heartbeat.PositionSeconds),
            listenedSeconds,
            durationSeconds = duration,
            qualified,
            now,
        }, transaction);
    }

    private static void UpsertActive(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction transaction,
        Guid profileId,
        PlayerQueueItemDto item,
        PlayerHeartbeatDto heartbeat,
        DateTimeOffset now)
    {
        conn.Execute("""
            INSERT INTO music_play_active_segments
                (profile_id, work_id, asset_id, queue_item_id, started_at,
                 last_position_seconds, listened_seconds, duration_seconds,
                 qualified, last_heartbeat_at)
            VALUES
                (@profileId, @workId, @assetId, @queueItemId, @now,
                 @positionSeconds, 0.0, @durationSeconds, 0, @now)
            ON CONFLICT(profile_id) DO UPDATE SET
                work_id = excluded.work_id,
                asset_id = excluded.asset_id,
                queue_item_id = excluded.queue_item_id,
                started_at = excluded.started_at,
                last_position_seconds = excluded.last_position_seconds,
                listened_seconds = 0.0,
                duration_seconds = excluded.duration_seconds,
                qualified = 0,
                last_heartbeat_at = excluded.last_heartbeat_at;
            """, new
        {
            profileId,
            workId = item.WorkId,
            assetId = item.AssetId,
            queueItemId = heartbeat.QueueItemId ?? item.QueueItemId,
            now,
            positionSeconds = Math.Max(0d, heartbeat.PositionSeconds),
            durationSeconds = heartbeat.DurationSeconds ?? item.DurationSeconds,
        }, transaction);
    }

    private static void ClearActive(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction transaction,
        Guid profileId)
    {
        conn.Execute(
            "DELETE FROM music_play_active_segments WHERE profile_id = @profileId;",
            new { profileId },
            transaction);
    }

    private sealed record ActiveSegmentRow
    {
        public Guid ProfileId { get; init; }
        public Guid WorkId { get; init; }
        public Guid? AssetId { get; init; }
        public Guid? QueueItemId { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public double LastPositionSeconds { get; init; }
        public double ListenedSeconds { get; init; }
        public double? DurationSeconds { get; init; }
        public bool Qualified { get; init; }
        public DateTimeOffset LastHeartbeatAt { get; init; }
    }

    private sealed record MusicPlayStatRow
    {
        public Guid WorkId { get; init; }
        public int PlayCount { get; init; }
        public DateTimeOffset LastPlayedAt { get; init; }
    }
}

public sealed record MusicPlayStat(int PlayCount, DateTimeOffset LastPlayedAt);
