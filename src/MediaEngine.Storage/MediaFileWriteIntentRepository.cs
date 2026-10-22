using System.Globalization;
using Dapper;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// The durable to-do list for every write into a file (batched write-back D1). One row per target (a media asset, later a View item);
/// a newer change replaces the older one by bumping its generation, so only the final version is written. Rows are claimed under a
/// lease, so a row that was being worked on when the Engine stopped comes back once its lease runs out. Also keeps the schedule's
/// own memory (<c>file_write_runs</c>).
/// </summary>
public sealed class MediaFileWriteIntentRepository(IDatabaseConnection database, TimeProvider? clock = null)
{
    public const string MediaAssetKind = "media_asset";
    public const string ViewItemKind = "view_item";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    // The one statement every place that asks for a file write uses. A newer ask for a file already on the list (whatever state it is
    // in, including being written right now) bumps the generation and puts it back to pending, so the write that is in flight cannot
    // mark the newer version done. A move in progress is never cleared: the first step recorded wins until the move is finished.
    // An id that is not a media asset adds nothing.
    private const string UpsertAssetSql = """
        INSERT INTO media_file_write_intents
            (id, target_kind, asset_id, generation, operation_token, trigger, status, attempts, created_at, updated_at, move_step)
        SELECT @id, 'media_asset', @assetId, 1, @token, @trigger, 'pending', 0, @now, @now, @moveStep
        WHERE EXISTS (SELECT 1 FROM media_assets WHERE id = @assetId)
        ON CONFLICT(asset_id) WHERE asset_id IS NOT NULL DO UPDATE SET
            generation = media_file_write_intents.generation + 1,
            operation_token = excluded.operation_token,
            trigger = CASE
                WHEN media_file_write_intents.trigger = 'library_move' AND media_file_write_intents.move_step IS NOT NULL
                THEN media_file_write_intents.trigger
                ELSE excluded.trigger END,
            status = 'pending', attempts = 0, lease_expires_at = NULL, last_error = NULL, completed_at = NULL,
            updated_at = excluded.updated_at,
            move_step = COALESCE(media_file_write_intents.move_step, excluded.move_step);
        """;

    private const string UpsertViewItemSql = """
        INSERT INTO media_file_write_intents
            (id, target_kind, local_item_id, generation, operation_token, trigger, status, attempts, created_at, updated_at)
        SELECT @id, 'view_item', @localItemId, 1, @token, @trigger, 'pending', 0, @now, @now
        WHERE EXISTS (SELECT 1 FROM local_items WHERE id = @localItemId)
        ON CONFLICT(local_item_id) WHERE local_item_id IS NOT NULL DO UPDATE SET
            generation = media_file_write_intents.generation + 1,
            operation_token = excluded.operation_token, trigger = excluded.trigger,
            status = 'pending', attempts = 0, lease_expires_at = NULL, last_error = NULL, completed_at = NULL,
            updated_at = excluded.updated_at;
        """;

    private const string SelectColumns = """
        i.id AS Id, i.target_kind AS TargetKind, i.asset_id AS AssetId, i.local_item_id AS LocalItemId,
        i.generation AS Generation, i.operation_token AS OperationToken, i.trigger AS Trigger,
        i.status AS Status, i.attempts AS Attempts, i.move_step AS MoveStep,
        i.move_source_path AS MoveSourcePath, i.move_target_path AS MoveTargetPath, i.move_source_hash AS MoveSourceHash
        """;

    /// <summary>
    /// Puts a media asset on the list inside a transaction the caller already holds (a library move, an editor save). Returns false when
    /// the id is not a media asset. <paramref name="requestMove"/> also records that the file must be renamed and moved at once.
    /// </summary>
    public static bool UpsertAsset(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        string trigger,
        string token,
        DateTimeOffset now,
        bool requestMove = false) =>
        connection.Execute(UpsertAssetSql, new
        {
            id = Guid.NewGuid(),
            assetId,
            token,
            trigger,
            now = Iso(now),
            moveStep = requestMove ? "requested" : null,
        }, transaction) > 0;

    /// <summary>Puts media assets on the list. Returns how many are now waiting because of this call.</summary>
    public Task<int> EnqueueAssetsAsync(IReadOnlyCollection<Guid> assetIds, string trigger, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var queued = 0;
            var now = _clock.GetUtcNow();
            var operation = Guid.NewGuid().ToString("N");
            foreach (var assetId in assetIds.Distinct())
            {
                token.ThrowIfCancellationRequested();
                if (UpsertAsset(connection, transaction, assetId, trigger, operation, now))
                {
                    queued++;
                }
            }
            return queued;
        }, ct);

    /// <summary>Puts a View item's file on the list. Returns false when the item does not exist.</summary>
    public Task<bool> EnqueueViewItemAsync(Guid localItemId, string trigger, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute(UpsertViewItemSql, new
            {
                id = Guid.NewGuid(),
                localItemId,
                token = Guid.NewGuid().ToString("N"),
                trigger,
                now = Iso(_clock.GetUtcNow()),
            }, transaction) > 0;
        }, ct);

    /// <summary>
    /// Claims up to <paramref name="limit"/> waiting rows of one kind for a run: pending rows, and rows whose claim ran out (the Engine
    /// stopped mid-write). Rows with a move in progress wait for the move. Only rows last changed at or before
    /// <paramref name="changedBefore"/> are taken, so a change that lands during a run, and a retry that is only now due, wait for the
    /// next run instead of being picked up again in the same one. <paramref name="excluding"/> are rows the run has already handled.
    /// </summary>
    public Task<IReadOnlyList<MediaFileWriteIntent>> ClaimBatchAsync(
        string targetKind,
        int limit,
        TimeSpan lease,
        DateTimeOffset changedBefore,
        IReadOnlyCollection<Guid>? excluding = null,
        CancellationToken ct = default) =>
        database.ExecuteWriteAsync<IReadOnlyList<MediaFileWriteIntent>>((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var now = _clock.GetUtcNow();
            var rows = connection.Query<MediaFileWriteIntent>($"""
                SELECT {SelectColumns}, w.media_type AS MediaType
                FROM media_file_write_intents i
                LEFT JOIN media_assets ma ON ma.id = i.asset_id
                LEFT JOIN editions e ON e.id = ma.edition_id
                LEFT JOIN works w ON w.id = e.work_id
                WHERE i.target_kind = @targetKind AND i.move_step IS NULL
                  AND (i.status = 'pending' OR (i.status = 'writing' AND i.lease_expires_at < @now))
                  AND i.updated_at <= @changedBefore AND i.id NOT IN @excluded
                ORDER BY i.updated_at, i.id
                LIMIT @limit;
                """, new
            {
                targetKind,
                now = Iso(now),
                changedBefore = Iso(changedBefore),
                excluded = (excluding ?? []).Select(GuidSql.ToBlob).ToArray(),
                limit = Math.Max(1, limit),
            }, transaction).ToList();

            var claimed = new List<MediaFileWriteIntent>(rows.Count);
            foreach (var row in rows)
            {
                var changed = connection.Execute("""
                    UPDATE media_file_write_intents
                    SET status = 'writing', attempts = attempts + 1, lease_expires_at = @leaseUntil
                    WHERE id = @id AND generation = @generation AND move_step IS NULL
                      AND (status = 'pending' OR (status = 'writing' AND lease_expires_at < @now));
                    """, new
                {
                    id = row.Id,
                    generation = row.Generation,
                    now = Iso(now),
                    leaseUntil = Iso(now.Add(lease)),
                }, transaction);
                if (changed == 1)
                {
                    claimed.Add(row with { Status = "writing", Attempts = row.Attempts + 1 });
                }
            }
            return claimed;
        }, ct);

    /// <summary>
    /// Claims the next row whose file must be moved now (library moves do not wait for the schedule), or null.
    /// <paramref name="excluding"/> are rows already handled in this pass, so a move that keeps failing is not retried in a tight loop.
    /// </summary>
    public Task<MediaFileWriteIntent?> ClaimNextMoveAsync(
        TimeSpan lease, IReadOnlyCollection<Guid>? excluding = null, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var now = _clock.GetUtcNow();
            var row = connection.QueryFirstOrDefault<MediaFileWriteIntent>($"""
                SELECT {SelectColumns}
                FROM media_file_write_intents i
                WHERE i.move_step = 'requested' AND i.target_kind = 'media_asset'
                  AND (i.status = 'pending' OR (i.status = 'writing' AND i.lease_expires_at < @now))
                  AND i.id NOT IN @excluded
                ORDER BY i.updated_at, i.id LIMIT 1;
                """, new { now = Iso(now), excluded = (excluding ?? []).Select(GuidSql.ToBlob).ToArray() }, transaction);
            if (row is null)
            {
                return null;
            }
            var changed = connection.Execute("""
                UPDATE media_file_write_intents
                SET status = 'writing', attempts = attempts + 1, lease_expires_at = @leaseUntil
                WHERE id = @id AND generation = @generation
                  AND (status = 'pending' OR (status = 'writing' AND lease_expires_at < @now));
                """, new { id = row.Id, generation = row.Generation, now = Iso(now), leaseUntil = Iso(now.Add(lease)) }, transaction);
            return changed == 1 ? row with { Status = "writing", Attempts = row.Attempts + 1 } : null;
        }, ct);

    /// <summary>
    /// The file has been moved: the row goes back to pending so its tags are written on the next scheduled run. Ignored when a newer
    /// change arrived while the move ran (the move is simply checked again, which changes nothing).
    /// </summary>
    public Task<bool> CompleteMoveAsync(Guid id, long generation, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute("""
                UPDATE media_file_write_intents
                SET move_step = NULL, status = 'pending', attempts = 0, lease_expires_at = NULL, last_error = NULL,
                    updated_at = @now
                WHERE id = @id AND generation = @generation AND status = 'writing' AND move_step IS NOT NULL;
                """, new { id, generation, now = Iso(_clock.GetUtcNow()) }, transaction) == 1;
        }, ct);

    /// <summary>The move did not work this time: it is tried again (<c>pending</c>) or given up (<c>failed</c>), with the reason.</summary>
    public Task<bool> FailMoveAsync(Guid id, long generation, string status, string? error, CancellationToken ct = default)
    {
        if (status is not ("pending" or "failed"))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute("""
                UPDATE media_file_write_intents
                SET status = @status, lease_expires_at = NULL, last_error = @error, updated_at = @now
                WHERE id = @id AND generation = @generation AND status = 'writing' AND move_step IS NOT NULL;
                """, new { id, generation, status, error, now = Iso(_clock.GetUtcNow()) }, transaction) == 1;
        }, ct);
    }

    public Task<bool> CompleteAsync(Guid assetId, long generation, string status,
        string? error = null, CancellationToken ct = default) =>
        CompleteCoreAsync("asset_id", assetId, generation, status, error, ct);

    /// <summary>Records the outcome of a write by row id. Ignored when a newer change replaced the row while it was being written.</summary>
    public Task<bool> CompleteByIdAsync(Guid id, long generation, string status,
        string? error = null, CancellationToken ct = default) =>
        CompleteCoreAsync("id", id, generation, status, error, ct);

    private Task<bool> CompleteCoreAsync(string keyColumn, Guid key, long generation, string status, string? error, CancellationToken ct)
    {
        if (status is not ("verified" or "blocked" or "unsupported" or "failed" or "pending"))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var now = Iso(_clock.GetUtcNow());
            return connection.Execute($"""
                UPDATE media_file_write_intents
                SET status = @status, lease_expires_at = NULL, last_error = @error, updated_at = @now,
                    completed_at = CASE WHEN @status = 'pending' THEN completed_at ELSE @now END
                WHERE {keyColumn} = @key AND generation = @generation AND status = 'writing';
                """, new { key, generation, status, error, now }, transaction) == 1;
        }, ct);
    }

    /// <summary>Of the given media assets, the ones that have a write waiting (pending or being written).</summary>
    public Task<ISet<Guid>> GetWaitingAssetIdsAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (assetIds.Count == 0)
        {
            return Task.FromResult<ISet<Guid>>(new HashSet<Guid>());
        }
        using var connection = database.CreateConnection();
        var found = connection.Query<Guid>("""
            SELECT asset_id FROM media_file_write_intents
            WHERE asset_id IN @ids AND status IN ('pending', 'writing');
            """, new { ids = assetIds.Select(GuidSql.ToBlob).ToArray() });
        return Task.FromResult<ISet<Guid>>(found.ToHashSet());
    }

    public Task<FileWriteCounts> GetCountsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return Task.FromResult(connection.QuerySingle<FileWriteCounts>("""
            SELECT COALESCE(SUM(CASE WHEN status IN ('pending', 'writing') THEN 1 ELSE 0 END), 0) AS Waiting,
                   COALESCE(SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END), 0) AS Failed
            FROM media_file_write_intents;
            """));
    }

    /// <summary>The files that could not be written, with the reason, newest first.</summary>
    public Task<IReadOnlyList<FailedFileWrite>> GetFailedAsync(int limit = 500, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<FailedFileWrite>("""
            SELECT i.target_kind AS TargetKind, i.asset_id AS AssetId, i.local_item_id AS LocalItemId,
                   COALESCE(
                       (SELECT cv.value FROM canonical_values cv WHERE cv.entity_id = e.work_id AND cv.key = 'title' LIMIT 1),
                       li.title) AS Title,
                   COALESCE(ma.file_path_root, li.primary_file_name) AS Path,
                   i.last_error AS Reason, i.attempts AS Attempts, i.updated_at AS UpdatedAt
            FROM media_file_write_intents i
            LEFT JOIN media_assets ma ON ma.id = i.asset_id
            LEFT JOIN editions e ON e.id = ma.edition_id
            LEFT JOIN local_items li ON li.id = i.local_item_id
            WHERE i.status = 'failed'
            ORDER BY i.updated_at DESC, i.id
            LIMIT @limit;
            """, new { limit = Math.Clamp(limit, 1, 5000) });
        return Task.FromResult<IReadOnlyList<FailedFileWrite>>(rows.ToList());
    }

    /// <summary>What is waiting for one file: whether a write is waiting, why the last attempt failed and when it last finished.</summary>
    public Task<AssetFileWriteState> GetAssetStateAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<AssetFileWriteState>("""
            SELECT CASE WHEN status IN ('pending', 'writing') THEN 1 ELSE 0 END AS Waiting,
                   CASE WHEN status IN ('failed', 'blocked') OR (status = 'pending' AND last_error IS NOT NULL)
                        THEN last_error END AS LastError,
                   CASE WHEN status = 'verified' THEN completed_at END AS LastWrittenAt
            FROM media_file_write_intents WHERE asset_id = @assetId;
            """, new { assetId });
        return Task.FromResult(row ?? new AssetFileWriteState());
    }

    public Task<FileWriteRunState?> GetRunStateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return Task.FromResult(connection.QueryFirstOrDefault<FileWriteRunState>("""
            SELECT anchor_at AS AnchorAt, last_run_started_at AS LastRunStartedAt, last_run_finished_at AS LastRunFinishedAt,
                   last_run_written AS LastRunWritten, last_run_failed AS LastRunFailed, last_run_kept AS LastRunKept
            FROM file_write_runs WHERE id = 1;
            """));
    }

    /// <summary>Sets the schedule's starting point the first time the scheduler runs and leaves it alone afterwards.</summary>
    public Task EnsureAnchorAsync(DateTimeOffset anchor, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(
                "INSERT INTO file_write_runs (id, anchor_at) VALUES (1, @anchor) ON CONFLICT(id) DO NOTHING;",
                new { anchor = Iso(anchor) }, transaction);
        }, ct);

    /// <summary>Records a finished run: when it started and finished, and how many files were written, failed or kept in Library only.</summary>
    public Task RecordRunAsync(
        DateTimeOffset startedAt, DateTimeOffset finishedAt, int written, int failed, int kept, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("""
                INSERT INTO file_write_runs
                    (id, anchor_at, last_run_started_at, last_run_finished_at, last_run_written, last_run_failed, last_run_kept)
                VALUES (1, @finished, @started, @finished, @written, @failed, @kept)
                ON CONFLICT(id) DO UPDATE SET
                    last_run_started_at = excluded.last_run_started_at, last_run_finished_at = excluded.last_run_finished_at,
                    last_run_written = excluded.last_run_written, last_run_failed = excluded.last_run_failed,
                    last_run_kept = excluded.last_run_kept;
                """, new { started = Iso(startedAt), finished = Iso(finishedAt), written, failed, kept }, transaction);
        }, ct);

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

public sealed record MediaFileWriteIntent
{
    public Guid Id { get; init; }
    public string TargetKind { get; init; } = MediaFileWriteIntentRepository.MediaAssetKind;
    public Guid AssetId { get; init; }
    public Guid? LocalItemId { get; init; }
    public long Generation { get; init; }
    public string OperationToken { get; init; } = "";
    public string Trigger { get; init; } = "";
    public string Status { get; init; } = "";
    public int Attempts { get; init; }
    public string? MoveStep { get; init; }
    public string? MoveSourcePath { get; init; }
    public string? MoveTargetPath { get; init; }
    public string? MoveSourceHash { get; init; }

    /// <summary>The Work's media type for a media asset (Music, Audiobooks, Movies...); null for a View item.</summary>
    public string? MediaType { get; init; }
}

public sealed record FileWriteCounts
{
    public int Waiting { get; init; }
    public int Failed { get; init; }
}

public sealed record FailedFileWrite
{
    public string TargetKind { get; init; } = "";
    public Guid? AssetId { get; init; }
    public Guid? LocalItemId { get; init; }
    public string? Title { get; init; }
    public string? Path { get; init; }
    public string? Reason { get; init; }
    public int Attempts { get; init; }
    public string UpdatedAt { get; init; } = "";
}

public sealed record AssetFileWriteState
{
    public bool Waiting { get; init; }
    public string? LastError { get; init; }
    public string? LastWrittenAt { get; init; }
}

public sealed record FileWriteRunState
{
    public string AnchorAt { get; init; } = "";
    public string? LastRunStartedAt { get; init; }
    public string? LastRunFinishedAt { get; init; }
    public int? LastRunWritten { get; init; }
    public int? LastRunFailed { get; init; }
    public int? LastRunKept { get; init; }
}
