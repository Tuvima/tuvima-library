using Dapper;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Playback;

public sealed class AudiobookListenHistoryRepository
{
    private readonly IDatabaseConnection _db;

    public AudiobookListenHistoryRepository(IDatabaseConnection db)
    {
        _db = db;
    }

    public Task<IReadOnlyList<AudiobookListenHistoryItemDto>> GetRecentAsync(
        Guid profileId,
        Guid? workId,
        int limit = 10,
        CancellationToken ct = default,
        IReadOnlySet<Guid>? authorizedAssetIds = null)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var cappedLimit = Math.Clamp(limit, 1, 50);
        var queryLimit = Math.Min(150, cappedLimit * 4);
        var rows = conn.Query<HistoryRow>("""
            SELECT id AS Id,
                   profile_id AS ProfileId,
                   work_id AS WorkId,
                   asset_id AS AssetId,
                   title AS Title,
                   chapter_title AS ChapterTitle,
                   chapter_index AS ChapterIndex,
                   position_seconds AS PositionSeconds,
                   duration_seconds AS DurationSeconds,
                   progress_pct AS ProgressPct,
                   device_id AS DeviceId,
                   client AS Client,
                   started_at AS StartedAt,
                   ended_at AS EndedAt
            FROM audiobook_listen_history
            WHERE profile_id = @profileId
              AND (@unrestricted = 1 OR asset_id IN @allowedAssets)
              AND (@workId IS NULL OR work_id = @workId)
            ORDER BY ended_at DESC
            LIMIT @limit;
            """, new
        {
            profileId,
            workId,
            limit = queryLimit,
            unrestricted = authorizedAssetIds is null ? 1 : 0,
            allowedAssets = (authorizedAssetIds ?? new HashSet<Guid>()).Select(GuidSql.ToBlob).ToArray()
        }).ToList();

        var active = conn.Query<ActiveSegmentRow>("""
            SELECT profile_id AS ProfileId,
                   work_id AS WorkId,
                   asset_id AS AssetId,
                   queue_item_id AS QueueItemId,
                   title AS Title,
                   chapter_title AS ChapterTitle,
                   chapter_index AS ChapterIndex,
                   started_at AS StartedAt,
                   started_position_seconds AS StartedPositionSeconds,
                   last_position_seconds AS LastPositionSeconds,
                   duration_seconds AS DurationSeconds,
                   device_id AS DeviceId,
                   client AS Client,
                   last_heartbeat_at AS LastHeartbeatAt
            FROM audiobook_listen_active_segments
            WHERE profile_id = @profileId
              AND (@unrestricted = 1 OR asset_id IN @allowedAssets)
              AND (@workId IS NULL OR work_id = @workId);
            """, new
        {
            profileId,
            workId,
            unrestricted = authorizedAssetIds is null ? 1 : 0,
            allowedAssets = (authorizedAssetIds ?? new HashSet<Guid>()).Select(GuidSql.ToBlob).ToArray()
        }).ToList();

        var items = rows.Select(ToDto)
            .Concat(active.Select(ToDto))
            .ToList();

        return Task.FromResult<IReadOnlyList<AudiobookListenHistoryItemDto>>(Clean(items, cappedLimit));
    }

    public Task TrackHeartbeatAsync(
        Guid profileId,
        PlayerQueueItemDto item,
        PlayerHeartbeatDto heartbeat,
        int qualificationSeconds,
        int historyLimit,
        int activeSegmentGapSeconds,
        int positionJumpToleranceSeconds,
        string deviceId,
        string client,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (item.AssetId is not Guid assetId)
        {
            return Task.CompletedTask;
        }

        using var conn = _db.CreateConnection();
        var now = DateTimeOffset.UtcNow;
        var active = conn.QueryFirstOrDefault<ActiveSegmentRow>("""
            SELECT profile_id AS ProfileId,
                   work_id AS WorkId,
                   asset_id AS AssetId,
                   queue_item_id AS QueueItemId,
                   title AS Title,
                   chapter_title AS ChapterTitle,
                   chapter_index AS ChapterIndex,
                   started_at AS StartedAt,
                   started_position_seconds AS StartedPositionSeconds,
                   last_position_seconds AS LastPositionSeconds,
                   duration_seconds AS DurationSeconds,
                   device_id AS DeviceId,
                   client AS Client,
                   last_heartbeat_at AS LastHeartbeatAt
            FROM audiobook_listen_active_segments
            WHERE profile_id = @profileId
            LIMIT 1;
            """, new { profileId });

        if (!heartbeat.IsPlaying)
        {
            if (active is not null)
            {
                FinalizeActive(conn, active, heartbeat.PositionSeconds, heartbeat.DurationSeconds, now, qualificationSeconds, historyLimit);
                ClearActive(conn, profileId);
            }

            return Task.CompletedTask;
        }

        if (active is null || ShouldRestartSegment(active, item, heartbeat, now, activeSegmentGapSeconds, positionJumpToleranceSeconds))
        {
            if (active is not null)
            {
                FinalizeActive(conn, active, active.LastPositionSeconds, active.DurationSeconds, now, qualificationSeconds, historyLimit);
            }

            UpsertActive(conn, profileId, item, heartbeat, assetId, now, deviceId, client);
            return Task.CompletedTask;
        }

        var chapter = ResolveChapter(item, heartbeat);
        conn.Execute("""
            UPDATE audiobook_listen_active_segments
            SET last_position_seconds = @positionSeconds,
                duration_seconds = COALESCE(@durationSeconds, duration_seconds),
                chapter_title = COALESCE(@chapterTitle, chapter_title),
                chapter_index = COALESCE(@chapterIndex, chapter_index),
                last_heartbeat_at = @now,
                device_id = @deviceId,
                client = @client
            WHERE profile_id = @profileId;
            """, new
        {
            profileId,
            positionSeconds = Math.Max(0, heartbeat.PositionSeconds),
            durationSeconds = heartbeat.DurationSeconds,
            chapterTitle = StringHelpers.FirstNonBlank(heartbeat.ChapterTitle, chapter?.Title),
            chapterIndex = heartbeat.ChapterIndex ?? chapter?.Index,
            now,
            deviceId,
            client,
        });

        return Task.CompletedTask;
    }

    private static bool ShouldRestartSegment(
        ActiveSegmentRow active,
        PlayerQueueItemDto item,
        PlayerHeartbeatDto heartbeat,
        DateTimeOffset now,
        int activeSegmentGapSeconds,
        int positionJumpToleranceSeconds)
    {
        if (item.AssetId != active.AssetId || item.WorkId != active.WorkId)
        {
            return true;
        }

        if (heartbeat.QueueItemId.HasValue
            && active.QueueItemId.HasValue
            && heartbeat.QueueItemId.Value != active.QueueItemId.Value)
        {
            return true;
        }

        if (now - active.LastHeartbeatAt > TimeSpan.FromSeconds(Math.Clamp(activeSegmentGapSeconds, 5, 300)))
        {
            return true;
        }

        var elapsed = Math.Max(0, (now - active.LastHeartbeatAt).TotalSeconds);
        var expected = active.LastPositionSeconds + elapsed * Math.Max(0.5, heartbeat.PlaybackRate ?? 1d);
        return Math.Abs(heartbeat.PositionSeconds - expected) > Math.Clamp(positionJumpToleranceSeconds, 1, 120);
    }

    private static void UpsertActive(
        System.Data.IDbConnection conn,
        Guid profileId,
        PlayerQueueItemDto item,
        PlayerHeartbeatDto heartbeat,
        Guid assetId,
        DateTimeOffset now,
        string deviceId,
        string client)
    {
        var chapter = ResolveChapter(item, heartbeat);
        conn.Execute("""
            INSERT INTO audiobook_listen_active_segments
                (profile_id, work_id, asset_id, queue_item_id, title, chapter_title, chapter_index,
                 started_at, started_position_seconds, last_position_seconds, duration_seconds,
                 device_id, client, last_heartbeat_at)
            VALUES
                (@profileId, @workId, @assetId, @queueItemId, @title, @chapterTitle, @chapterIndex,
                 @now, @positionSeconds, @positionSeconds, @durationSeconds,
                 @deviceId, @client, @now)
            ON CONFLICT(profile_id) DO UPDATE SET
                work_id = excluded.work_id,
                asset_id = excluded.asset_id,
                queue_item_id = excluded.queue_item_id,
                title = excluded.title,
                chapter_title = excluded.chapter_title,
                chapter_index = excluded.chapter_index,
                started_at = excluded.started_at,
                started_position_seconds = excluded.started_position_seconds,
                last_position_seconds = excluded.last_position_seconds,
                duration_seconds = excluded.duration_seconds,
                device_id = excluded.device_id,
                client = excluded.client,
                last_heartbeat_at = excluded.last_heartbeat_at;
            """, new
        {
            profileId,
            workId = item.WorkId,
            assetId,
            queueItemId = heartbeat.QueueItemId ?? item.QueueItemId,
            title = item.Title,
            chapterTitle = StringHelpers.FirstNonBlank(heartbeat.ChapterTitle, chapter?.Title),
            chapterIndex = heartbeat.ChapterIndex ?? chapter?.Index,
            now,
            positionSeconds = Math.Max(0, heartbeat.PositionSeconds),
            durationSeconds = heartbeat.DurationSeconds ?? item.DurationSeconds,
            deviceId,
            client,
        });
    }

    private static void FinalizeActive(
        System.Data.IDbConnection conn,
        ActiveSegmentRow active,
        double positionSeconds,
        double? durationSeconds,
        DateTimeOffset endedAt,
        int qualificationSeconds,
        int historyLimit)
    {
        if ((endedAt - active.StartedAt).TotalSeconds < qualificationSeconds)
        {
            return;
        }

        var safeDuration = durationSeconds ?? active.DurationSeconds;
        var safePosition = Math.Max(0, positionSeconds);
        var progress = safeDuration is > 0
            ? Math.Clamp(safePosition / safeDuration.Value * 100d, 0, 100)
            : 0d;

        conn.Execute("""
            INSERT INTO audiobook_listen_history
                (id, profile_id, work_id, asset_id, title, chapter_title, chapter_index,
                 position_seconds, duration_seconds, progress_pct, device_id, client, started_at, ended_at)
            VALUES
                (@id, @profileId, @workId, @assetId, @title, @chapterTitle, @chapterIndex,
                 @positionSeconds, @durationSeconds, @progressPct, @deviceId, @client, @startedAt, @endedAt);
            """, new
        {
            id = Guid.NewGuid(),
            profileId = active.ProfileId,
            workId = active.WorkId,
            assetId = active.AssetId,
            title = active.Title,
            chapterTitle = active.ChapterTitle,
            chapterIndex = active.ChapterIndex,
            positionSeconds = safePosition,
            durationSeconds = safeDuration,
            progressPct = progress,
            deviceId = active.DeviceId,
            client = active.Client,
            startedAt = active.StartedAt,
            endedAt,
        });

        conn.Execute("""
            DELETE FROM audiobook_listen_history
            WHERE profile_id = @profileId
              AND work_id = @workId
              AND id NOT IN (
                  SELECT id
                  FROM audiobook_listen_history
                  WHERE profile_id = @profileId
                    AND work_id = @workId
                  ORDER BY ended_at DESC
                  LIMIT @limit
              );
            """, new { profileId = active.ProfileId, workId = active.WorkId, limit = Math.Clamp(historyLimit, 1, 50) });
    }

    private static void ClearActive(System.Data.IDbConnection conn, Guid profileId)
    {
        conn.Execute("DELETE FROM audiobook_listen_active_segments WHERE profile_id = @profileId;", new { profileId });
    }

    private static PlaybackChapterDto? ResolveChapter(PlayerQueueItemDto item, PlayerHeartbeatDto heartbeat)
    {
        if (!string.IsNullOrWhiteSpace(heartbeat.ChapterTitle) || heartbeat.ChapterIndex.HasValue)
        {
            return new PlaybackChapterDto
            {
                Index = heartbeat.ChapterIndex ?? -1,
                Title = heartbeat.ChapterTitle ?? string.Empty,
                StartSeconds = Math.Max(0, heartbeat.PositionSeconds),
            };
        }

        return item.Chapters.LastOrDefault(chapter =>
            chapter.StartSeconds <= heartbeat.PositionSeconds
            && (!chapter.EndSeconds.HasValue || heartbeat.PositionSeconds < chapter.EndSeconds.Value));
    }

    private static AudiobookListenHistoryItemDto ToDto(HistoryRow row) => new()
    {
        Id = row.Id,
        ProfileId = row.ProfileId,
        WorkId = row.WorkId,
        AssetId = row.AssetId,
        Title = row.Title,
        ChapterTitle = row.ChapterTitle,
        ChapterIndex = row.ChapterIndex,
        PositionSeconds = row.PositionSeconds,
        DurationSeconds = row.DurationSeconds,
        ProgressPct = row.ProgressPct,
        DeviceId = row.DeviceId,
        Client = row.Client,
        StartedAt = row.StartedAt,
        EndedAt = row.EndedAt,
    };

    private static AudiobookListenHistoryItemDto ToDto(ActiveSegmentRow row)
    {
        var progress = row.DurationSeconds is > 0
            ? Math.Clamp(row.LastPositionSeconds / row.DurationSeconds.Value * 100d, 0, 100)
            : 0d;

        return new AudiobookListenHistoryItemDto
        {
            Id = Guid.Empty,
            ProfileId = row.ProfileId,
            WorkId = row.WorkId,
            AssetId = row.AssetId,
            Title = row.Title,
            ChapterTitle = row.ChapterTitle,
            ChapterIndex = row.ChapterIndex,
            PositionSeconds = Math.Max(0, row.LastPositionSeconds),
            DurationSeconds = row.DurationSeconds,
            ProgressPct = progress,
            DeviceId = row.DeviceId,
            Client = row.Client,
            StartedAt = row.StartedAt,
            EndedAt = row.LastHeartbeatAt,
        };
    }

    private static IReadOnlyList<AudiobookListenHistoryItemDto> Clean(
        IReadOnlyList<AudiobookListenHistoryItemDto> items,
        int limit)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var nonZero = items.Where(item => item.PositionSeconds > 0.5d).ToList();
        var candidates = nonZero.Count > 0 ? nonZero : items.ToList();
        return candidates
            .OrderByDescending(item => item.EndedAt)
            .GroupBy(item => new
            {
                item.WorkId,
                item.AssetId,
                Chapter = item.ChapterIndex ?? -1,
                PositionBucket = (int)Math.Floor(item.PositionSeconds / 30d),
            })
            .Select(group => group.First())
            .Take(Math.Clamp(limit, 1, 50))
            .ToList();
    }


    private sealed record ActiveSegmentRow
    {
        public Guid ProfileId { get; init; }
        public Guid WorkId { get; init; }
        public Guid AssetId { get; init; }
        public Guid? QueueItemId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string? ChapterTitle { get; init; }
        public int? ChapterIndex { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public double StartedPositionSeconds { get; init; }
        public double LastPositionSeconds { get; init; }
        public double? DurationSeconds { get; init; }
        public string DeviceId { get; init; } = "web";
        public string Client { get; init; } = "web";
        public DateTimeOffset LastHeartbeatAt { get; init; }
    }

    private sealed record HistoryRow
    {
        public Guid Id { get; init; }
        public Guid ProfileId { get; init; }
        public Guid WorkId { get; init; }
        public Guid AssetId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string? ChapterTitle { get; init; }
        public int? ChapterIndex { get; init; }
        public double PositionSeconds { get; init; }
        public double? DurationSeconds { get; init; }
        public double ProgressPct { get; init; }
        public string DeviceId { get; init; } = "web";
        public string Client { get; init; } = "web";
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset EndedAt { get; init; }
    }
}
