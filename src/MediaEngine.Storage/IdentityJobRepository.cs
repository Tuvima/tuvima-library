using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Jobs;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IIdentityJobRepository"/>.
/// Manages durable identity pipeline jobs that survive engine restarts.
/// </summary>
public sealed class IdentityJobRepository : IIdentityJobRepository
{
    private readonly IDatabaseConnection _db;

    public IdentityJobRepository(IDatabaseConnection db) => _db = db;

    public Task CreateAsync(IdentityJob job, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT OR IGNORE INTO identity_jobs
                (id, entity_id, entity_type, media_type, ingestion_run_id,
                 state, pass, attempt_count, poison_attempt_count, last_outcome_category, lease_owner, lease_expires_at,
                 selected_candidate_id, resolved_qid, last_error, next_retry_at,
                 created_at, updated_at)
            SELECT
                @Id, @EntityId, @EntityType, @MediaType, @IngestionRunId,
                @State, @Pass, @AttemptCount, @PoisonAttemptCount, @LastOutcomeCategory, @LeaseOwner, @LeaseExpiresAt,
                @SelectedCandidateId, @ResolvedQid, @LastError, @NextRetryAt,
                @CreatedAt, @UpdatedAt
            WHERE NOT EXISTS (
                SELECT 1
                FROM   identity_jobs
                WHERE  entity_id = @EntityId
                  AND  pass = @Pass
                  AND  state NOT IN ('Ready', 'ReadyWithoutUniverse', 'Failed', 'RetailNoMatch', 'QidNoMatch', 'QidNeedsReview')
            ) AND NOT EXISTS (
                SELECT 1 FROM identity_jobs sibling
                JOIN media_assets sa ON sa.id=sibling.entity_id
                JOIN editions se ON se.id=sa.edition_id
                JOIN editions current_edition ON current_edition.work_id=se.work_id
                JOIN media_assets current_asset ON current_asset.edition_id=current_edition.id
                JOIN works recording ON recording.id=se.work_id
                WHERE @MediaType='Audiobooks' AND current_asset.id=@EntityId
                  AND sibling.entity_id!=@EntityId AND recording.parent_key LIKE 'source-folder:%'
            );
            """,
            new
            {
                job.Id,
                job.EntityId,
                job.EntityType,
                job.MediaType,
                job.IngestionRunId,
                job.State,
                job.Pass,
                job.AttemptCount,
                job.PoisonAttemptCount,
                job.LastOutcomeCategory,
                job.LeaseOwner,
                LeaseExpiresAt = job.LeaseExpiresAt?.ToString("O"),
                job.SelectedCandidateId,
                job.ResolvedQid,
                job.LastError,
                NextRetryAt = job.NextRetryAt?.ToString("O"),
                CreatedAt = job.CreatedAt.ToString("O"),
                UpdatedAt = job.UpdatedAt.ToString("O"),
            });
        return Task.CompletedTask;
    }

    public Task<Guid> CreateOrResumeUserResolutionAsync(IdentityJob job, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var activeJobId = conn.QueryFirstOrDefault<Guid?>("""
                SELECT id
                FROM identity_jobs
                WHERE entity_id = @EntityId
                  AND pass = @Pass
                  AND state NOT IN ('Ready', 'ReadyWithoutUniverse', 'Failed', 'RetailNoMatch', 'QidNoMatch', 'QidNeedsReview')
                ORDER BY updated_at DESC, created_at DESC
                LIMIT 1;
                """, new { job.EntityId, job.Pass }, tx);

            if (activeJobId.HasValue)
            {
                conn.Execute("""
                    UPDATE identity_jobs
                    SET entity_type          = @EntityType,
                        media_type           = @MediaType,
                        ingestion_run_id     = NULL,
                        state                = @State,
                        selected_candidate_id = @SelectedCandidateId,
                        resolved_qid         = @ResolvedQid,
                        last_error           = NULL,
                        next_retry_at        = NULL,
                        lease_owner          = NULL,
                        lease_expires_at     = NULL,
                        updated_at           = @UpdatedAt
                    WHERE id = @Id;
                    """, new
                {
                    Id = activeJobId.Value,
                    job.EntityType,
                    job.MediaType,
                    job.State,
                    job.SelectedCandidateId,
                    job.ResolvedQid,
                    UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
                }, tx);
                return activeJobId.Value;
            }

            conn.Execute("""
                INSERT INTO identity_jobs
                    (id, entity_id, entity_type, media_type, ingestion_run_id,
                     state, pass, attempt_count, poison_attempt_count, last_outcome_category, lease_owner, lease_expires_at,
                     selected_candidate_id, resolved_qid, last_error, next_retry_at,
                     created_at, updated_at)
                VALUES
                    (@Id, @EntityId, @EntityType, @MediaType, NULL,
                     @State, @Pass, @AttemptCount, @PoisonAttemptCount, @LastOutcomeCategory, NULL, NULL,
                     @SelectedCandidateId, @ResolvedQid, NULL, NULL,
                     @CreatedAt, @UpdatedAt);
                """, new
            {
                job.Id,
                job.EntityId,
                job.EntityType,
                job.MediaType,
                job.State,
                job.Pass,
                job.AttemptCount,
                job.PoisonAttemptCount,
                job.LastOutcomeCategory,
                job.SelectedCandidateId,
                job.ResolvedQid,
                CreatedAt = job.CreatedAt.ToString("O"),
                UpdatedAt = job.UpdatedAt.ToString("O"),
            }, tx);
            return job.Id;
        }, ct);
    }

    public Task<IdentityJob?> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<IdentityJobRow>(
            SelectSql + """
             WHERE entity_id = @entityId OR entity_id IN (
                 SELECT sibling.id FROM media_assets current
                 JOIN editions ce ON ce.id = current.edition_id
                 JOIN works w ON w.id = ce.work_id
                 JOIN editions se ON se.work_id = w.id
                 JOIN media_assets sibling ON sibling.edition_id = se.id
                 WHERE current.id = @entityId AND w.media_type = 'Audiobooks'
                   AND w.parent_key LIKE 'source-folder:%' AND w.work_kind != 'parent'
             )
             ORDER BY CASE
                          WHEN state IN ('Ready', 'ReadyWithoutUniverse', 'Failed', 'RetailNoMatch', 'QidNoMatch', 'QidNeedsReview') THEN 1
                          ELSE 0
                      END,
                      updated_at DESC,
                      created_at DESC
             LIMIT 1;
            """,
            new { entityId });
        return Task.FromResult(row is null ? null : MapRow(row));
    }

    public Task<IdentityJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<IdentityJobRow>(SelectSql + " WHERE id = @jobId LIMIT 1;",
            new { jobId });
        return Task.FromResult(row is null ? null : MapRow(row));
    }

    public Task<IReadOnlyList<IdentityJob>> LeaseNextAsync(
        string workerName,
        IReadOnlyList<IdentityJobState> states,
        int batchSize,
        TimeSpan leaseDuration,
        IReadOnlyList<string>? excludeRunIds = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow.ToString("O");
        var leaseExpiry = DateTimeOffset.UtcNow.Add(leaseDuration).ToString("O");
        // Build IN clause from enum values (safe — not user input).
        var stateList = string.Join(", ", states.Select(s => $"'{s}'"));

        // Validate all run ID strings are valid GUIDs before interpolating into SQL.
        // Values are written by this codebase but validated defensively.
        var validExcludeRunIds = excludeRunIds?
            .Select(id => Guid.TryParse(id, out var parsed) ? parsed : (Guid?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();

        // Build optional exclusion clause. Jobs with NULL ingestion_run_id
        // (ad-hoc / manual) always pass through regardless of the gate.
        var excludeClause = validExcludeRunIds is { Count: > 0 }
            ? "AND (ingestion_run_id IS NULL OR ingestion_run_id NOT IN @excludeRunIds)"
            : "";

        var sql = $"""
            UPDATE identity_jobs
            SET    lease_owner = @workerName,
                   lease_expires_at = @leaseExpiry,
                   updated_at = @now
            WHERE  id IN (
                SELECT id FROM identity_jobs
                WHERE  state IN ({stateList})
                  AND  (lease_owner IS NULL OR lease_expires_at < @now)
                  AND  (next_retry_at IS NULL OR next_retry_at <= @now)
                  {excludeClause}
                ORDER BY created_at ASC
                LIMIT  @batchSize
            )
            RETURNING id              AS Id,
                      entity_id       AS EntityId,
                      entity_type     AS EntityType,
                      media_type      AS MediaType,
                      ingestion_run_id AS IngestionRunId,
                      state           AS State,
                      pass            AS Pass,
                      attempt_count   AS AttemptCount,
                      poison_attempt_count AS PoisonAttemptCount,
                      last_outcome_category AS LastOutcomeCategory,
                      lease_owner     AS LeaseOwner,
                      lease_expires_at AS LeaseExpiresAt,
                      selected_candidate_id AS SelectedCandidateId,
                      resolved_qid    AS ResolvedQid,
                      last_error      AS LastError,
                      next_retry_at   AS NextRetryAt,
                      created_at      AS CreatedAt,
                      updated_at      AS UpdatedAt;
            """;

        using var conn = _db.CreateConnection();
        var rows = conn.Query<IdentityJobRow>(sql, new { workerName, leaseExpiry, now, batchSize, excludeRunIds = validExcludeRunIds });
        return Task.FromResult<IReadOnlyList<IdentityJob>>(rows.Select(MapRow).ToList());
    }

    public Task UpdateStateAsync(Guid jobId, IdentityJobState newState, string? error = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var preserveLease = newState is
            IdentityJobState.RetailSearching or
            IdentityJobState.BridgeSearching or
            IdentityJobState.Hydrating;

        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE identity_jobs
            SET    state            = @state,
                   last_error       = @error,
                   lease_owner      = CASE WHEN @preserveLease = 1 THEN lease_owner ELSE NULL END,
                   lease_expires_at = CASE WHEN @preserveLease = 1 THEN lease_expires_at ELSE NULL END,
                   attempt_count    = attempt_count + @attemptIncrement,
                   updated_at       = @now
            WHERE  id = @jobId;
            """,
            new
            {
                jobId,
                state = newState.ToString(),
                error,
                preserveLease = preserveLease ? 1 : 0,
                attemptIncrement = preserveLease ? 1 : 0,
                now = DateTimeOffset.UtcNow.ToString("O"),
            });
        return Task.CompletedTask;
    }

    public Task ScheduleRetryAsync(Guid jobId, IdentityJobState retryState, DateTimeOffset nextRetryAt, string error, CancellationToken ct = default)
        => ScheduleRetryForOutcomeAsync(
            jobId,
            retryState,
            nextRetryAt,
            error,
            BackgroundJobOutcomeCategory.ContentFailure,
            ct);

    public Task ScheduleRetryForOutcomeAsync(
        Guid jobId,
        IdentityJobState retryState,
        DateTimeOffset nextRetryAt,
        string error,
        BackgroundJobOutcomeCategory category,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var policy = BackgroundJobOutcomePolicy.For(category);
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE identity_jobs
            SET    state            = @state,
                   last_error       = @error,
                   next_retry_at    = @nextRetryAt,
                   lease_owner      = NULL,
                   lease_expires_at = NULL,
                   attempt_count    = attempt_count + 1,
                   poison_attempt_count = poison_attempt_count + @poisonIncrement,
                   last_outcome_category = @category,
                   updated_at       = @now
            WHERE  id = @jobId;
            """,
            new
            {
                jobId,
                state = retryState.ToString(),
                error,
                poisonIncrement = policy.ConsumesPoisonBudget ? 1 : 0,
                category = category.ToString(),
                nextRetryAt = nextRetryAt.ToString("O"),
                now = DateTimeOffset.UtcNow.ToString("O"),
            });
        return Task.CompletedTask;
    }

    public Task MarkDeadLetteredAsync(Guid jobId, string error, CancellationToken ct = default)
        => MarkDeadLetteredForOutcomeAsync(
            jobId,
            error,
            BackgroundJobOutcomeCategory.ContentFailure,
            ct);

    public Task MarkDeadLetteredForOutcomeAsync(
        Guid jobId,
        string error,
        BackgroundJobOutcomeCategory category,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var policy = BackgroundJobOutcomePolicy.For(category);
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE identity_jobs
            SET state = @state,
                last_error = @error,
                last_outcome_category = @category,
                poison_attempt_count = poison_attempt_count + @poisonIncrement,
                next_retry_at = NULL,
                lease_owner = NULL,
                lease_expires_at = NULL,
                updated_at = @now
            WHERE id = @jobId;
            """, new
        {
            jobId,
            state = IdentityJobState.Failed.ToString(),
            error,
            category = category.ToString(),
            poisonIncrement = policy.ConsumesPoisonBudget ? 1 : 0,
            now = DateTimeOffset.UtcNow.ToString("O"),
        });
        return Task.CompletedTask;
    }

    public Task SetSelectedCandidateAsync(Guid jobId, Guid candidateId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE identity_jobs
            SET    selected_candidate_id = @candidateId,
                   updated_at           = @now
            WHERE  id = @jobId;
            """,
            new
            {
                jobId,
                candidateId,
                now = DateTimeOffset.UtcNow.ToString("O"),
            });
        return Task.CompletedTask;
    }

    public Task SetResolvedQidAsync(Guid jobId, string qid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE identity_jobs
            SET    resolved_qid = @qid,
                   updated_at   = @now
            WHERE  id = @jobId;
            """,
            new
            {
                jobId,
                qid,
                now = DateTimeOffset.UtcNow.ToString("O"),
            });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IdentityJob>> GetStaleAsync(TimeSpan age, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var cutoff = DateTimeOffset.UtcNow.Subtract(age).ToString("O");
        using var conn = _db.CreateConnection();
        var rows = conn.Query<IdentityJobRow>(
            SelectSql + """
                 WHERE state NOT IN ('Ready', 'ReadyWithoutUniverse', 'Failed', 'RetailNoMatch', 'QidNoMatch', 'QidNeedsReview')
                   AND updated_at < @cutoff
                 ORDER BY updated_at ASC
                 LIMIT @limit;
                """,
            new { cutoff, limit });
        return Task.FromResult<IReadOnlyList<IdentityJob>>(rows.Select(MapRow).ToList());
    }

    public Task<int> ReclaimStuckJobsAsync(
        IdentityJobState processingState,
        TimeSpan stuckThreshold,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var resumeState = processingState switch
        {
            IdentityJobState.RetailSearching => IdentityJobState.Queued,
            IdentityJobState.BridgeSearching => IdentityJobState.RetailMatched,
            IdentityJobState.Hydrating => IdentityJobState.QidResolved,
            IdentityJobState.UniverseEnriching => IdentityJobState.QidResolved,
            _ => throw new ArgumentOutOfRangeException(
                nameof(processingState),
                processingState,
                "Only intermediate identity job states can be reclaimed."),
        };
        var cutoff = DateTimeOffset.UtcNow.Subtract(stuckThreshold).ToString("O");
        var now = DateTimeOffset.UtcNow.ToString("O");
        var processingStateName = processingState.ToString();
        using var conn = _db.CreateConnection();
        var affected = conn.Execute("""
            UPDATE identity_jobs
            SET    state            = 'Ready',
                   lease_owner      = NULL,
                   lease_expires_at = NULL,
                   last_error       = NULL,
                   updated_at       = @now
            WHERE  state = 'UniverseEnriching'
              AND  @processingState = 'UniverseEnriching'
              AND  EXISTS (
                   SELECT 1
                   FROM canonical_values cv
                   WHERE cv.entity_id = identity_jobs.entity_id
                     AND cv.key = 'stage3_enhanced_at'
              );

            UPDATE identity_jobs
            SET    state            = 'QidResolved',
                   attempt_count    = 0,
                   next_retry_at    = NULL,
                   lease_owner      = NULL,
                   lease_expires_at = NULL,
                   last_error       = 'Recovered for Stage 3 artwork/enhancer retry',
                   updated_at       = @now
            WHERE  state = 'Failed'
              AND  @processingState = 'UniverseEnriching'
              AND  last_error = 'Stuck intermediate state exceeded retry limit'
              AND  EXISTS (
                   SELECT 1
                   FROM canonical_values cv
                   WHERE cv.entity_id = identity_jobs.entity_id
                     AND cv.key = 'wikidata_qid'
                     AND cv.value IS NOT NULL
                     AND cv.value <> ''
              )
              AND  EXISTS (
                   SELECT 1
                   FROM canonical_values cv
                   WHERE cv.entity_id = identity_jobs.entity_id
                     AND cv.key IN ('tmdb_id', 'tmdb_movie_id', 'tmdb_tv_id', 'tvdb_id', 'musicbrainz_id', 'musicbrainz_artist_id', 'musicbrainz_release_group_id')
                     AND cv.value IS NOT NULL
                     AND cv.value <> ''
              )
              AND  NOT EXISTS (
                   SELECT 1
                   FROM canonical_values cv
                   WHERE cv.entity_id = identity_jobs.entity_id
                     AND cv.key = 'stage3_enhanced_at'
              );

            UPDATE identity_jobs
            SET    state            = @resumeState,
                   lease_owner      = NULL,
                   lease_expires_at = NULL,
                   last_error       = 'Reclaimed from stuck intermediate state',
                   updated_at       = @now
            WHERE  state = @processingState
              AND  (lease_owner IS NULL OR lease_expires_at < @now)
              AND  updated_at < @cutoff
              AND  attempt_count < 5;

            UPDATE identity_jobs
            SET    state            = 'Failed',
                   lease_owner      = NULL,
                   lease_expires_at = NULL,
                   last_error       = 'Stuck intermediate state exceeded retry limit',
                   updated_at       = @now
            WHERE  state = @processingState
              AND  (lease_owner IS NULL OR lease_expires_at < @now)
              AND  updated_at < @cutoff
              AND  attempt_count >= 5;
            """,
            new
            {
                cutoff,
                now,
                processingState = processingStateName,
                resumeState = resumeState.ToString(),
            });
        return Task.FromResult(affected);
    }

    public Task<int> RecoverInterruptedJobsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow.ToString("O");
        // Runs before workers start: even an unleased Stage 3 job may have been
        // waiting in the previous process's in-memory enrichment queue.
        return _db.ExecuteWriteAsync((conn, tx, _) => conn.Execute("""
            UPDATE identity_jobs
            SET    state = CASE state
                       WHEN 'RetailSearching' THEN 'Queued'
                       WHEN 'BridgeSearching' THEN 'RetailMatched'
                       WHEN 'Hydrating' THEN 'QidResolved'
                       WHEN 'UniverseEnriching' THEN 'QidResolved'
                   END,
                   lease_owner      = NULL,
                   lease_expires_at = NULL,
                   next_retry_at    = NULL,
                   last_error       = 'Recovered after engine restart',
                   updated_at       = @now
            WHERE  state IN ('RetailSearching', 'BridgeSearching', 'Hydrating', 'UniverseEnriching');
            """,
            new { now }, transaction: tx), ct);
    }

    public Task<IReadOnlyList<IdentityJob>> GetByStateAsync(IdentityJobState state, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<IdentityJobRow>(
            SelectSql + " WHERE state = @state ORDER BY created_at ASC LIMIT @limit;",
            new { state = state.ToString(), limit });
        return Task.FromResult<IReadOnlyList<IdentityJob>>(rows.Select(MapRow).ToList());
    }

    public Task<IReadOnlyDictionary<string, int>> GetStateCountsByRunAsync(Guid ingestionRunId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query("""
            SELECT state, COUNT(*) AS cnt
            FROM   identity_jobs
            WHERE  ingestion_run_id = @runId
            GROUP BY state;
            """,
            new { runId = ingestionRunId });
        IReadOnlyDictionary<string, int> counts = rows.ToDictionary(
            r => (string)r.state,
            r => (int)r.cnt);
        return Task.FromResult(counts);
    }

    public Task<int> CountActiveAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var count = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM identity_jobs WHERE state NOT IN ('Ready', 'ReadyWithoutUniverse', 'Failed', 'RetailNoMatch', 'QidNoMatch', 'QidNeedsReview')");
        return Task.FromResult(count);
    }

    public Task<IReadOnlyDictionary<string, int>> GetPendingStage1CountsByRunAsync(
        IReadOnlyList<string> ingestionRunIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (ingestionRunIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
        }

        // Validate all run ID strings are valid GUIDs before interpolating into SQL.
        var validRunIds = ingestionRunIds
            .Select(id => Guid.TryParse(id, out var parsed) ? parsed : (Guid?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
        if (validRunIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
        }

        // Build IN clause from validated GUID strings.
        const string sql = """
            SELECT ingestion_run_id AS IngestionRunId, COUNT(*) AS Count
            FROM   identity_jobs
            WHERE  ingestion_run_id IN @validRunIds
              AND  state IN ('Queued', 'RetailSearching')
            GROUP BY ingestion_run_id;
            """;

        using var conn = _db.CreateConnection();
        var rows = conn.Query<PendingStageCountRow>(sql, new { validRunIds });
        IReadOnlyDictionary<string, int> counts = rows.ToDictionary(
            r => r.IngestionRunId.ToString("D"),
            r => r.Count);
        return Task.FromResult(counts);
    }

    public Task ReleaseLeaseAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE identity_jobs
            SET    lease_owner      = NULL,
                   lease_expires_at = NULL,
                   updated_at       = @now
            WHERE  id = @jobId;
            """,
            new
            {
                jobId,
                now = DateTimeOffset.UtcNow.ToString("O"),
            });
        return Task.CompletedTask;
    }

    // ── Shared SELECT prefix ─────────────────────────────────────────────────

    private const string SelectSql = """
        SELECT id               AS Id,
               entity_id        AS EntityId,
               entity_type      AS EntityType,
               media_type       AS MediaType,
               ingestion_run_id AS IngestionRunId,
               state            AS State,
               pass             AS Pass,
               attempt_count    AS AttemptCount,
               poison_attempt_count AS PoisonAttemptCount,
               last_outcome_category AS LastOutcomeCategory,
               lease_owner      AS LeaseOwner,
               lease_expires_at AS LeaseExpiresAt,
               selected_candidate_id AS SelectedCandidateId,
               resolved_qid     AS ResolvedQid,
               last_error       AS LastError,
               next_retry_at    AS NextRetryAt,
               created_at       AS CreatedAt,
               updated_at       AS UpdatedAt
        FROM   identity_jobs
        """;

    // ── Private intermediate row type and mapper ─────────────────────────────

    private sealed class IdentityJobRow
    {
        public Guid Id { get; set; }
        public Guid EntityId { get; set; }
        public string EntityType { get; set; } = "";
        public string MediaType { get; set; } = "";
        public Guid? IngestionRunId { get; set; }
        public string State { get; set; } = "";
        public string Pass { get; set; } = "";
        public int AttemptCount { get; set; }
        public int PoisonAttemptCount { get; set; }
        public string? LastOutcomeCategory { get; set; }
        public string? LeaseOwner { get; set; }
        public string? LeaseExpiresAt { get; set; }
        public Guid? SelectedCandidateId { get; set; }
        public string? ResolvedQid { get; set; }
        public string? LastError { get; set; }
        public string? NextRetryAt { get; set; }
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
    }

    private static IdentityJob MapRow(IdentityJobRow r) => new()
    {
        Id = r.Id,
        EntityId = r.EntityId,
        EntityType = r.EntityType,
        MediaType = r.MediaType,
        IngestionRunId = r.IngestionRunId,
        State = r.State,
        Pass = r.Pass,
        AttemptCount = r.AttemptCount,
        PoisonAttemptCount = r.PoisonAttemptCount,
        LastOutcomeCategory = r.LastOutcomeCategory,
        LeaseOwner = r.LeaseOwner,
        LeaseExpiresAt = r.LeaseExpiresAt is not null ? DateTimeOffset.Parse(r.LeaseExpiresAt) : null,
        SelectedCandidateId = r.SelectedCandidateId,
        ResolvedQid = r.ResolvedQid,
        LastError = r.LastError,
        NextRetryAt = r.NextRetryAt is not null ? DateTimeOffset.Parse(r.NextRetryAt) : null,
        CreatedAt = DateTimeOffset.Parse(r.CreatedAt),
        UpdatedAt = DateTimeOffset.Parse(r.UpdatedAt),
    };

    private sealed class PendingStageCountRow
    {
        public Guid IngestionRunId { get; set; }
        public int Count { get; set; }
    }
}
