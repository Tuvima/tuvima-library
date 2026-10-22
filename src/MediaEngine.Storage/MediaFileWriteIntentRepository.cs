using System.Globalization;
using Dapper;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// The durable to-do list for every write into a file (batched write-back D1). One row per target (a media asset, later a View item);
/// a newer change replaces the older one by bumping its generation, so only the final version is written. Rows are claimed under a
/// lease, so a row that was being worked on when the Engine stopped comes back once its lease runs out. Also keeps the schedule's
/// own memory (<c>file_write_runs</c>).
/// </summary>
public sealed class MediaFileWriteIntentRepository(IDatabaseConnection database, TimeProvider? clock = null) : IFileMoveInProgress, IFilingQueue
{
    public const string MediaAssetKind = "media_asset";
    public const string ViewItemKind = "view_item";

    /// <summary>What a moved file's row says when write-back is off: the file was moved, its tags were not written.</summary>
    public const string MoveTagsBlockedReason = "Write-back is off, so the file was moved but its tags were not written.";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>A file with a move that is queued for the next run (<see cref="MoveStepWaiting"/>) or held back (<see cref="MoveStepHeld"/>) has not moved.</summary>
    public const string MoveStepWaiting = "waiting";

    /// <summary>An automatic filing move held until its newly added Managed folder passes its first check (Library setup D61); never moved while held.</summary>
    public const string MoveStepHeld = "held";

    public const string MoveOriginPerson = "person";
    public const string MoveOriginFiling = "filing";

    /// <summary>What a retry notes on a file that was in use when its filing turn came; the file was left exactly as it was.</summary>
    public const string InUseReason = "In use; will try again at the next run";

    // A move that is in flight or asked for now. A waiting or held filing move has not started: its file stays where it is, so the
    // watcher and reconciliation treat the file normally at its current path.
    private const string MoveInFlightFilter = "move_step IS NOT NULL AND move_step NOT IN ('waiting', 'held')";

    // The one statement every place that asks for a file write uses. A newer ask for a file already on the list (whatever state it is
    // in, including being written right now) bumps the generation and puts it back to pending, so the write that is in flight cannot
    // mark the newer version done. A move in progress is never cleared: the first step recorded wins until the move is finished.
    // The one exception is a person's own move asked for while an automatic filing waits or is held: theirs is done at once, so the
    // row becomes a requested move of the person's. An id that is not a media asset adds nothing.
    private const string UpsertAssetSql = """
        INSERT INTO media_file_write_intents
            (id, target_kind, asset_id, generation, operation_token, trigger, status, attempts, created_at, updated_at, move_step, move_origin)
        SELECT @id, 'media_asset', @assetId, 1, @token, @trigger, 'pending', 0, @now, @now, @moveStep,
               CASE WHEN @moveStep IS NULL THEN NULL ELSE 'person' END
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
            move_step = CASE
                WHEN media_file_write_intents.move_step IN ('waiting', 'held') AND excluded.move_step = 'requested' THEN 'requested'
                ELSE COALESCE(media_file_write_intents.move_step, excluded.move_step) END,
            move_origin = CASE WHEN excluded.move_step IS NOT NULL THEN 'person' ELSE media_file_write_intents.move_origin END,
            next_attempt_at = CASE WHEN excluded.move_step IS NOT NULL THEN NULL ELSE media_file_write_intents.next_attempt_at END,
            move_source_path = CASE WHEN media_file_write_intents.move_step IS NULL THEN NULL ELSE media_file_write_intents.move_source_path END,
            move_target_path = CASE WHEN media_file_write_intents.move_step IS NULL THEN NULL ELSE media_file_write_intents.move_target_path END,
            move_source_hash = CASE WHEN media_file_write_intents.move_step IS NULL THEN NULL ELSE media_file_write_intents.move_source_hash END,
            move_generation = CASE WHEN media_file_write_intents.move_step IS NULL THEN NULL ELSE media_file_write_intents.move_generation END,
            recovery_attempts = CASE WHEN media_file_write_intents.move_step IS NULL THEN 0 ELSE media_file_write_intents.recovery_attempts END;
        """;

    // An automatic filing move (Library setup D62): the row waits for the next run, or is held. A request never disturbs a move that is
    // asked for or running (a person's own move wins, and one already filing is not asked twice), and tags asked for meanwhile stay on
    // the same row. The trigger is left alone when tags are waiting on the row (so they are still written after the move) and is
    // 'filing' otherwise, which says nobody asked for tags: such a row is removed once its move is checked.
    private const string UpsertFilingSql = """
        INSERT INTO media_file_write_intents
            (id, target_kind, asset_id, generation, operation_token, trigger, status, attempts, created_at, updated_at, move_step, move_origin)
        SELECT @id, 'media_asset', @assetId, 1, @token, 'filing', 'pending', 0, @now, @now, @moveStep, 'filing'
        WHERE EXISTS (SELECT 1 FROM media_assets WHERE id = @assetId)
        ON CONFLICT(asset_id) WHERE asset_id IS NOT NULL DO UPDATE SET
            generation = media_file_write_intents.generation + 1,
            operation_token = excluded.operation_token,
            trigger = CASE
                WHEN media_file_write_intents.move_step IS NULL AND media_file_write_intents.status IN ('pending', 'writing')
                THEN media_file_write_intents.trigger
                ELSE 'filing' END,
            status = 'pending', attempts = 0, lease_expires_at = NULL, last_error = NULL, completed_at = NULL,
            updated_at = excluded.updated_at,
            move_step = excluded.move_step, move_origin = 'filing', next_attempt_at = NULL,
            move_source_path = NULL, move_target_path = NULL, move_source_hash = NULL, move_generation = NULL, recovery_attempts = 0
        WHERE media_file_write_intents.move_step IS NULL
           OR (media_file_write_intents.move_origin = 'filing' AND media_file_write_intents.move_step = 'waiting' AND excluded.move_step = 'held');
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
        i.status AS Status, i.attempts AS Attempts, i.move_step AS MoveStep, i.move_origin AS MoveOrigin,
        i.move_source_path AS MoveSourcePath, i.move_target_path AS MoveTargetPath, i.move_source_hash AS MoveSourceHash,
        i.move_generation AS MoveGeneration, i.recovery_attempts AS RecoveryAttempts
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

    /// <summary>
    /// Records that Library wants to file a media asset on its own (Library setup D62): the move waits for the next run (or Write now)
    /// and is done with the tags, moves first. The file is not touched and stays playable where it is. With <paramref name="held"/> the
    /// request is recorded as held (D61) and is never moved here. Returns false when nothing was recorded: the asset is not a media
    /// asset, or a move is already asked for or running (a person's own move wins) or already waiting.
    /// </summary>
    /// <inheritdoc/>
    public Task<bool> RequestFilingAsync(Guid assetId, bool held, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute(UpsertFilingSql, new
            {
                id = Guid.NewGuid(),
                assetId,
                token = Guid.NewGuid().ToString("N"),
                now = Iso(_clock.GetUtcNow()),
                moveStep = held ? MoveStepHeld : MoveStepWaiting,
            }, transaction) > 0;
        }, ct);

    /// <summary>
    /// A run is starting: every waiting filing move becomes a requested move, so the move lane takes it before any tag is written.
    /// Held moves are never touched. Returns how many were promoted.
    /// </summary>
    public Task<int> PromoteWaitingFilingsAsync(CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute("""
                UPDATE media_file_write_intents SET move_step = 'requested', next_attempt_at = NULL
                WHERE target_kind = 'media_asset' AND move_step = 'waiting' AND move_origin = 'filing' AND status = 'pending';
                """, transaction: transaction);
        }, ct);

    /// <summary>
    /// The file was in use when its turn came (or a sharing violation stopped its move), and was left exactly as it was: the filing
    /// move goes back to waiting for the next run. It is not a failed attempt, so the try is not counted and the reason is recorded.
    /// </summary>
    public Task<bool> DeferFilingAsync(Guid id, string? reason = null, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute("""
                UPDATE media_file_write_intents
                SET move_step = 'waiting', move_origin = 'filing', status = 'pending', lease_expires_at = NULL, last_error = @reason,
                    attempts = MAX(attempts - 1, 0), move_source_path = NULL, move_target_path = NULL, move_source_hash = NULL,
                    move_generation = NULL, recovery_attempts = 0, next_attempt_at = NULL, updated_at = @now
                WHERE id = @id AND move_origin = 'filing' AND move_step IN ('requested', 'planned', 'waiting');
                """, new { id, reason = reason ?? InUseReason, now = Iso(_clock.GetUtcNow()) }, transaction) == 1;
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
    /// next run instead of being picked up again in the same one. A row is marked with <paramref name="runId"/> when it is claimed, and a
    /// row already marked with the same run is not taken again, so the run never writes a file twice however many chunks it takes.
    /// </summary>
    public Task<IReadOnlyList<MediaFileWriteIntent>> ClaimBatchAsync(
        string targetKind,
        int limit,
        TimeSpan lease,
        DateTimeOffset changedBefore,
        string? runId = null,
        CancellationToken ct = default) =>
        database.ExecuteWriteAsync<IReadOnlyList<MediaFileWriteIntent>>((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            // A caller without a run of its own gets a fresh mark each time, so nothing it asks for is held back.
            var mark = runId ?? Guid.NewGuid().ToString("N");
            var now = _clock.GetUtcNow();
            var rows = connection.Query<MediaFileWriteIntent>($"""
                SELECT {SelectColumns}, w.media_type AS MediaType
                FROM media_file_write_intents i
                LEFT JOIN media_assets ma ON ma.id = i.asset_id
                LEFT JOIN editions e ON e.id = ma.edition_id
                LEFT JOIN works w ON w.id = e.work_id
                WHERE i.target_kind = @targetKind AND i.move_step IS NULL
                  AND (i.status = 'pending' OR (i.status = 'writing' AND i.lease_expires_at < @now))
                  AND i.updated_at <= @changedBefore AND (i.claimed_run_id IS NULL OR i.claimed_run_id <> @runId)
                  AND NOT ({FolderFirstCheckRepository.FileInHeldFolder("ma")})
                ORDER BY i.updated_at, i.id
                LIMIT @limit;
                """, new
            {
                targetKind,
                now = Iso(now),
                changedBefore = Iso(changedBefore),
                runId = mark,
                limit = Math.Max(1, limit),
            }, transaction).ToList();

            var claimed = new List<MediaFileWriteIntent>(rows.Count);
            foreach (var row in rows)
            {
                var changed = connection.Execute("""
                    UPDATE media_file_write_intents
                    SET status = 'writing', attempts = attempts + 1, lease_expires_at = @leaseUntil, claimed_run_id = @runId
                    WHERE id = @id AND generation = @generation AND move_step IS NULL
                      AND (status = 'pending' OR (status = 'writing' AND lease_expires_at < @now));
                    """, new
                {
                    id = row.Id,
                    generation = row.Generation,
                    now = Iso(now),
                    leaseUntil = Iso(now.Add(lease)),
                    runId = mark,
                }, transaction);
                if (changed == 1)
                {
                    claimed.Add(row with { Status = "writing", Attempts = row.Attempts + 1 });
                }
            }
            return claimed;
        }, ct);

    /// <summary>
    /// Claims the next row whose file must be moved now (library moves do not wait for the schedule), or null. A move that failed waits
    /// for its next attempt time, so one that keeps failing is not retried in a tight loop.
    /// </summary>
    public Task<MediaFileWriteIntent?> ClaimNextMoveAsync(TimeSpan lease, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var now = _clock.GetUtcNow();
            var row = connection.QueryFirstOrDefault<MediaFileWriteIntent>($"""
                SELECT {SelectColumns}
                FROM media_file_write_intents i
                WHERE i.move_step = 'requested' AND i.target_kind = 'media_asset'
                  AND (i.status = 'pending' OR (i.status = 'writing' AND i.lease_expires_at < @now))
                  AND (i.next_attempt_at IS NULL OR i.next_attempt_at <= @now)
                ORDER BY i.updated_at, i.id LIMIT 1;
                """, new { now = Iso(now) }, transaction);
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
    /// The move is done and checked: the move columns are cleared and the row goes back to pending, so its tags are written on the next
    /// scheduled run. When a newer change arrived while the move ran (a second move may have been asked for), the row is instead put
    /// back to <c>requested</c>, so the move lane plans again from the data store and moves the file again if it no longer fits.
    /// Returns true when nothing newer arrived.
    /// </summary>
    public Task<bool> CompleteMoveAsync(Guid id, long generation, bool writeTags = true, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var row = connection.QueryFirstOrDefault<MoveRowState>(
                "SELECT generation AS Generation, move_origin AS Origin, trigger AS Trigger FROM media_file_write_intents WHERE id = @id AND move_step IS NOT NULL;",
                new { id }, transaction);
            if (row is null)
            {
                return false;
            }
            var newerArrived = row.Generation != generation;
            if (!newerArrived && row.Origin == MoveOriginFiling && row.Trigger == MediaFileWriteTriggers.Filing)
            {
                // Nobody asked for tags on this file: filing was the whole job, so nothing is left waiting.
                connection.Execute("DELETE FROM media_file_write_intents WHERE id = @id AND move_step IS NOT NULL;", new { id }, transaction);
                return true;
            }
            // With write-back off the move still happened (it is filing, not tag writing) and a person's move drops its tag write. A
            // filing that already carries tags keeps them pending, as a plain tag row would, until write-back is turned back on.
            var keepTags = writeTags || newerArrived || row.Origin == MoveOriginFiling;
            connection.Execute("""
                UPDATE media_file_write_intents
                SET move_step = @nextStep, move_origin = CASE WHEN @nextStep IS NULL THEN NULL ELSE move_origin END,
                    move_source_path = NULL, move_target_path = NULL, move_source_hash = NULL,
                    move_generation = NULL, recovery_attempts = 0, next_attempt_at = NULL,
                    status = @status, attempts = 0, lease_expires_at = NULL, last_error = @error, updated_at = @now,
                    completed_at = CASE WHEN @status = 'blocked' THEN @now ELSE completed_at END
                WHERE id = @id AND move_step IS NOT NULL;
                """, new
            {
                id,
                nextStep = newerArrived ? "requested" : null,
                status = keepTags ? "pending" : "blocked",
                error = keepTags ? null : MoveTagsBlockedReason,
                now = Iso(_clock.GetUtcNow()),
            }, transaction);
            return !newerArrived;
        }, ct);

    /// <summary>
    /// The move did not work this time: it is tried again after <paramref name="retryAfter"/> (<c>pending</c>, back to <c>requested</c>,
    /// the file is known to be at its old place) or given up (<c>failed</c>), with the reason. A move given up keeps the paths it was
    /// planned with, for diagnosis, but no longer counts as a move in progress.
    /// </summary>
    public Task<bool> FailMoveAsync(Guid id, string status, string? error, TimeSpan? retryAfter = null, CancellationToken ct = default)
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
                SET status = @status, lease_expires_at = NULL, last_error = @error, updated_at = @now,
                    move_step = CASE WHEN @status = 'pending' THEN 'requested' ELSE NULL END,
                    move_origin = CASE WHEN @status = 'pending' THEN move_origin ELSE NULL END,
                    move_source_path = CASE WHEN @status = 'pending' THEN NULL ELSE move_source_path END,
                    move_target_path = CASE WHEN @status = 'pending' THEN NULL ELSE move_target_path END,
                    move_source_hash = CASE WHEN @status = 'pending' THEN NULL ELSE move_source_hash END,
                    move_generation = NULL, recovery_attempts = 0,
                    next_attempt_at = @nextAttempt
                WHERE id = @id AND move_step IS NOT NULL;
                """, new
            {
                id,
                status,
                error,
                now = Iso(_clock.GetUtcNow()),
                nextAttempt = status == "pending" && retryAfter is { } wait ? Iso(_clock.GetUtcNow().Add(wait)) : null,
            }, transaction) == 1;
        }, ct);
    }

    /// <summary>
    /// An automatic filing move did not work this time (not "in use": that is <see cref="DeferFilingAsync"/>). The attempt stays
    /// counted and the reason is recorded; the move goes back to waiting for the next scheduled run, never to be retried outside a run.
    /// After <paramref name="maxAttempts"/> attempts it is given up as failed, like a tag write, and appears in the failed list (it keeps
    /// the paths it was planned with, for diagnosis, and no longer counts as a move in progress). Returns false when the row is no
    /// longer a filing move (a person's move took it over).
    /// </summary>
    public Task<bool> FailFilingAttemptAsync(Guid id, string? error, int maxAttempts, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute("""
                UPDATE media_file_write_intents
                SET status = CASE WHEN attempts >= @maxAttempts THEN 'failed' ELSE 'pending' END,
                    move_step = CASE WHEN attempts >= @maxAttempts THEN NULL ELSE 'waiting' END,
                    move_origin = CASE WHEN attempts >= @maxAttempts THEN NULL ELSE 'filing' END,
                    move_source_path = CASE WHEN attempts >= @maxAttempts THEN move_source_path ELSE NULL END,
                    move_target_path = CASE WHEN attempts >= @maxAttempts THEN move_target_path ELSE NULL END,
                    move_source_hash = CASE WHEN attempts >= @maxAttempts THEN move_source_hash ELSE NULL END,
                    completed_at = CASE WHEN attempts >= @maxAttempts THEN @now ELSE completed_at END,
                    move_generation = NULL, recovery_attempts = 0, next_attempt_at = NULL, lease_expires_at = NULL,
                    last_error = @error, updated_at = @now
                WHERE id = @id AND move_origin = 'filing' AND move_step IN ('requested', 'planned', 'moved', 'checked', 'waiting');
                """, new { id, error, maxAttempts, now = Iso(_clock.GetUtcNow()) }, transaction) == 1;
        }, ct);

    /// <summary>
    /// Lets go of a row this caller claimed for a move but must not work on after all (a person's move took it over mid-claim): it is
    /// pending again with the try not counted, so the move lane takes it at once instead of waiting for the claim to run out.
    /// </summary>
    public Task ReleaseClaimAsync(Guid id, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("""
                UPDATE media_file_write_intents
                SET status = 'pending', lease_expires_at = NULL, attempts = MAX(attempts - 1, 0)
                WHERE id = @id AND status = 'writing' AND move_step IS NOT NULL;
                """, new { id }, transaction);
        }, ct);

    /// <summary>
    /// A move that is part-way could not be settled yet (a drive is offline, or the data store could not follow): its steps and paths
    /// stay on the row, the claim is released and it is looked at again after <paramref name="retryAfter"/>. When
    /// <paramref name="countAttempt"/> is true the try counts towards the cap on recovery attempts. Returns how many tries have been
    /// counted.
    /// </summary>
    public Task<int> DeferMoveAsync(
        Guid id, TimeSpan retryAfter, bool countAttempt, string? error, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("""
                UPDATE media_file_write_intents
                SET status = 'pending', lease_expires_at = NULL, last_error = @error,
                    next_attempt_at = @nextAttempt, recovery_attempts = recovery_attempts + @counted
                WHERE id = @id AND move_step IS NOT NULL;
                """, new
            {
                id,
                error,
                nextAttempt = Iso(_clock.GetUtcNow().Add(retryAfter)),
                counted = countAttempt ? 1 : 0,
            }, transaction);
            return connection.ExecuteScalar<int?>(
                "SELECT recovery_attempts FROM media_file_write_intents WHERE id = @id;", new { id }, transaction) ?? 0;
        }, ct);

    // ----- the steps of a move (D10) -----------------------------------------------------------------------------------------

    /// <summary>
    /// Planned: written before the disk is touched, so the record always says where the file might be.
    /// <paramref name="generation"/> is the generation of the row the plan was made at: finishing the move after a stop completes with
    /// it, so a change that arrived since is seen and the move is planned again.
    /// </summary>
    public Task RecordMovePlannedAsync(
        Guid id, string sourcePath, string targetPath, string? sourceHash, long? generation = null, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("""
                UPDATE media_file_write_intents
                SET move_step = 'planned', move_source_path = @sourcePath, move_target_path = @targetPath, move_source_hash = @sourceHash,
                    move_generation = COALESCE(@generation, generation)
                WHERE id = @id AND move_step IS NOT NULL;
                """, new { id, sourcePath, targetPath, sourceHash, generation }, transaction);
        }, ct);

    /// <summary>Moved: the file is at <paramref name="actualPath"/> and the old place is empty; the data store has not followed yet.</summary>
    public Task RecordMoveMovedAsync(Guid id, string actualPath, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("""
                UPDATE media_file_write_intents SET move_step = 'moved', move_target_path = @actualPath
                WHERE id = @id AND move_step IS NOT NULL;
                """, new { id, actualPath }, transaction);
        }, ct);

    /// <summary>Checked: the data store holds the new path and the file was verified there.</summary>
    public Task RecordMoveCheckedAsync(Guid id, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(
                "UPDATE media_file_write_intents SET move_step = 'checked' WHERE id = @id AND move_step IS NOT NULL;",
                new { id }, transaction);
        }, ct);

    /// <summary>
    /// Starts the move over: the file was found at its old place only, so the plan is cleared and the move lane tries again. The claim
    /// is released.
    /// </summary>
    public Task ResetMoveAsync(Guid id, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("""
                UPDATE media_file_write_intents
                SET move_step = 'requested', move_source_path = NULL, move_target_path = NULL, move_source_hash = NULL,
                    move_generation = NULL, recovery_attempts = 0, next_attempt_at = NULL,
                    status = 'pending', lease_expires_at = NULL, updated_at = @now
                WHERE id = @id AND move_step IS NOT NULL;
                """, new { id, now = Iso(_clock.GetUtcNow()) }, transaction);
        }, ct);

    /// <summary>
    /// Moves with a step recorded past <c>requested</c>: the ones a stop part-way can leave on disk in either place. With
    /// <paramref name="onlyDue"/>, only the ones nobody is working on whose next attempt time has come.
    /// </summary>
    public Task<IReadOnlyList<MediaFileWriteIntent>> GetStartedMovesAsync(bool onlyDue = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<MediaFileWriteIntent>($"""
            SELECT {SelectColumns} FROM media_file_write_intents i
            WHERE i.target_kind = 'media_asset' AND i.move_step IN ('planned', 'moved', 'checked')
              AND (@onlyDue = 0 OR (i.status = 'pending' AND (i.next_attempt_at IS NULL OR i.next_attempt_at <= @now)))
            ORDER BY i.updated_at, i.id;
            """, new { onlyDue = onlyDue ? 1 : 0, now = Iso(_clock.GetUtcNow()) }).ToList();
        return Task.FromResult<IReadOnlyList<MediaFileWriteIntent>>(rows);
    }

    public Task<MediaFileWriteIntent?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return Task.FromResult(connection.QueryFirstOrDefault<MediaFileWriteIntent>(
            $"SELECT {SelectColumns} FROM media_file_write_intents i WHERE i.id = @id;", new { id }));
    }

    /// <summary>
    /// Nothing is running when the Engine starts, so every claim on a move is stale: released, so a move cut short by the stop does not
    /// wait for its claim to run out.
    /// </summary>
    public Task ReleaseMoveClaimsAsync(CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("""
                UPDATE media_file_write_intents SET status = 'pending', lease_expires_at = NULL
                WHERE move_step IS NOT NULL AND status = 'writing';
                """, transaction: transaction);
        }, ct);

    // ----- the waiting mark (D11) --------------------------------------------------------------------------------------------

    /// <inheritdoc/>
    public Task<bool> HasMoveForAssetAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return Task.FromResult(connection.ExecuteScalar<long>(
            $"SELECT COUNT(*) FROM media_file_write_intents WHERE asset_id = @assetId AND {MoveInFlightFilter};", new { assetId }) > 0);
    }

    /// <inheritdoc/>
    public Task<IReadOnlySet<string>> GetPathsAsync(CancellationToken ct = default) => GetPathsWithMoveInProgressAsync(ct);

    /// <summary>
    /// The old and new place of every file with a move in progress. A move that is only requested has just its current place; one
    /// that has started also has the place it is going to. A filing move that is waiting for the next run (or held) has not started:
    /// its file is not in the list and is treated normally at its current path.
    /// </summary>
    public Task<IReadOnlySet<string>> GetPathsWithMoveInProgressAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<(string? Current, string? Source, string? Target)>("""
            SELECT ma.file_path_root, i.move_source_path, i.move_target_path
            FROM media_file_write_intents i
            LEFT JOIN media_assets ma ON ma.id = i.asset_id
            WHERE i.move_step IS NOT NULL AND i.move_step NOT IN ('waiting', 'held');
            """);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (current, source, target) in rows)
        {
            foreach (var path in new[] { current, source, target })
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths.Add(Normalise(path));
                }
            }
        }
        return Task.FromResult<IReadOnlySet<string>>(paths);
    }

    private static string Normalise(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path the system cannot make sense of is kept as written, so it still matches the same text.
            return path;
        }
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

    /// <summary>
    /// The file was changed outside Library and has been read back, so nothing was written: the row goes back to pending with the
    /// reason, and the attempt just spent does not count. Ignored when a newer change replaced the row meanwhile.
    /// </summary>
    public Task<bool> ReleaseForReReadAsync(Guid id, long generation, string? reason, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return connection.Execute("""
                UPDATE media_file_write_intents
                SET status = 'pending', attempts = MAX(attempts - 1, 0), lease_expires_at = NULL, last_error = @reason, updated_at = @now
                WHERE id = @id AND generation = @generation AND status = 'writing';
                """, new { id, generation, reason, now = Iso(_clock.GetUtcNow()) }, transaction) == 1;
        }, ct);

    /// <summary>
    /// Of the given media assets, the ones that have a tag write waiting (pending or being written). A row that is only waiting to file
    /// the file (trigger <c>filing</c>) is left out, so a sweep that asks for the file's tags is not held back by it.
    /// </summary>
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
            WHERE asset_id IN @ids AND status IN ('pending', 'writing') AND trigger <> 'filing';
            """, new { ids = assetIds.Select(GuidSql.ToBlob).ToArray() });
        return Task.FromResult<ISet<Guid>>(found.ToHashSet());
    }

    /// <summary>
    /// Counts what is waiting and what failed. Files in <paramref name="hiddenLibraryIds"/> are left out, so a caller who may not know
    /// about a library cannot see its files in the numbers.
    /// </summary>
    public Task<FileWriteCounts> GetCountsAsync(IReadOnlySet<string>? hiddenLibraryIds = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var hidden = hiddenLibraryIds is { Count: > 0 };
        using var connection = database.CreateConnection();
        // A tag write for a file in a folder whose first check is running or paused is waiting, but cannot be claimed yet (Library setup D61).
        return Task.FromResult(connection.QuerySingle<FileWriteCounts>($"""
            SELECT COALESCE(SUM(CASE WHEN i.status IN ('pending', 'writing') THEN 1 ELSE 0 END), 0) AS Waiting,
                   COALESCE(SUM(CASE WHEN i.status = 'failed' THEN 1 ELSE 0 END), 0) AS Failed,
                   COALESCE(SUM(CASE WHEN i.move_step IS NULL
                                      AND (i.status = 'pending' OR (i.status = 'writing' AND i.lease_expires_at < @now))
                                      AND (i.next_attempt_at IS NULL OR i.next_attempt_at <= @now)
                                      AND NOT ({FolderFirstCheckRepository.FileInHeldFolder("ma")})
                                     THEN 1 ELSE 0 END), 0)
                   + COALESCE(SUM(CASE WHEN i.move_step = 'waiting' AND i.status = 'pending' THEN 1 ELSE 0 END), 0) AS Claimable,
                   COALESCE(SUM(CASE WHEN i.move_step = 'waiting' AND i.status = 'pending' THEN 1 ELSE 0 END), 0) AS FilingWaiting
            FROM media_file_write_intents i
            LEFT JOIN media_assets ma ON ma.id = i.asset_id
            WHERE (@hidden = 0 OR ma.library_id IS NULL OR lower(ma.library_id) NOT IN @hiddenIds);
            """, new
        {
            now = Iso(_clock.GetUtcNow()),
            hidden = hidden ? 1 : 0,
            hiddenIds = hidden ? hiddenLibraryIds!.Select(id => id.ToLowerInvariant()).ToArray() : [string.Empty],
        }));
    }

    /// <summary>The files that could not be written, with the reason, newest first.</summary>
    public Task<IReadOnlyList<FailedFileWrite>> GetFailedAsync(
        int limit = 500, IReadOnlySet<string>? hiddenLibraryIds = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var hidden = hiddenLibraryIds is { Count: > 0 };
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
              AND (@hidden = 0 OR ma.library_id IS NULL OR lower(ma.library_id) NOT IN @hiddenIds)
            ORDER BY i.updated_at DESC, i.id
            LIMIT @limit;
            """, new
        {
            limit = Math.Clamp(limit, 1, 5000),
            hidden = hidden ? 1 : 0,
            hiddenIds = hidden ? hiddenLibraryIds!.Select(id => id.ToLowerInvariant()).ToArray() : [string.Empty],
        });
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
                   last_run_written AS LastRunWritten, last_run_failed AS LastRunFailed, last_run_kept AS LastRunKept,
                   last_run_filed AS LastRunFiled
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

    /// <summary>Moves the schedule's anchor forward (never back): the idle slots before <paramref name="anchor"/> are skipped.</summary>
    public Task MoveAnchorForwardAsync(DateTimeOffset anchor, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(
                "UPDATE file_write_runs SET anchor_at = @anchor WHERE id = 1 AND anchor_at < @anchor;",
                new { anchor = Iso(anchor) }, transaction);
        }, ct);

    /// <summary>
    /// Records a finished run: when it started and finished, and how many files were written, failed or kept in Library only, and how
    /// many were filed (moved) on their own, counted apart from the tag writes.
    /// </summary>
    public Task RecordRunAsync(
        DateTimeOffset startedAt, DateTimeOffset finishedAt, int written, int failed, int kept, int filed = 0, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute("""
                INSERT INTO file_write_runs
                    (id, anchor_at, last_run_started_at, last_run_finished_at, last_run_written, last_run_failed, last_run_kept, last_run_filed)
                VALUES (1, @finished, @started, @finished, @written, @failed, @kept, @filed)
                ON CONFLICT(id) DO UPDATE SET
                    last_run_started_at = excluded.last_run_started_at, last_run_finished_at = excluded.last_run_finished_at,
                    last_run_written = excluded.last_run_written, last_run_failed = excluded.last_run_failed,
                    last_run_kept = excluded.last_run_kept, last_run_filed = excluded.last_run_filed;
                """, new { started = Iso(startedAt), finished = Iso(finishedAt), written, failed, kept, filed }, transaction);
        }, ct);

    private sealed record MoveRowState(long Generation, string? Origin, string Trigger);

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

    /// <summary>Who asked for the move: <c>person</c> (done at once) or <c>filing</c> (Library's own, done in the run); null when no move.</summary>
    public string? MoveOrigin { get; init; }

    public string? MoveSourcePath { get; init; }
    public string? MoveTargetPath { get; init; }
    public string? MoveSourceHash { get; init; }

    /// <summary>The generation of the row when the move was planned; null before it is planned.</summary>
    public long? MoveGeneration { get; init; }

    /// <summary>How many starts a part-way move could not be settled.</summary>
    public int RecoveryAttempts { get; init; }

    /// <summary>The Work's media type for a media asset (Music, Audiobooks, Movies...); null for a View item.</summary>
    public string? MediaType { get; init; }
}

public sealed record FileWriteCounts
{
    /// <summary>Everything waiting for a write, including rows that cannot be taken yet (a move in progress, a retry not yet due).</summary>
    public int Waiting { get; init; }

    public int Failed { get; init; }

    /// <summary>The rows a write run could take right now (including waiting filing moves); the schedule starts a run only when this is above zero.</summary>
    public int Claimable { get; init; }

    /// <summary>Filing moves waiting for the next run. They run on the schedule even when tag writing is switched off.</summary>
    public int FilingWaiting { get; init; }
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

    /// <summary>How many files the last run filed (moved) on their own, apart from the tag writes in <see cref="LastRunWritten"/>.</summary>
    public int? LastRunFiled { get; init; }
}
