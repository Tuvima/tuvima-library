using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Jobs;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public sealed class EntityCapabilityStateRepository : IEntityCapabilityStateRepository
{
    private readonly IDatabaseConnection _db;

    public EntityCapabilityStateRepository(IDatabaseConnection db) => _db = db;

    public Task<EntityCapabilityState> EnsureAsync(EntityCapabilityState state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var createdAt = state.CreatedAt == default ? now : state.CreatedAt;
        var updatedAt = state.UpdatedAt == default ? now : state.UpdatedAt;
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT OR IGNORE INTO entity_capability_states
                (id, entity_id, entity_kind, media_type, capability_id, capability_kind,
                 capability_version, sub_key, status, requiredness, source, confidence,
                 artifact_count, artifact_summary, result_summary, last_operation_id,
                 first_attempted_at, last_attempted_at, succeeded_at, next_retry_at,
                 stale, needs_rerun, missing_reason, last_error, last_outcome_category, created_at, updated_at)
            VALUES
                (@Id, @EntityId, @EntityKind, @MediaType, @CapabilityId, @CapabilityKind,
                 @CapabilityVersion, @SubKey, @Status, @Requiredness, @Source, @Confidence,
                 @ArtifactCount, @ArtifactSummary, @ResultSummary, @LastOperationId,
                 @FirstAttemptedAt, @LastAttemptedAt, @SucceededAt, @NextRetryAt,
                 @Stale, @NeedsRerun, @MissingReason, @LastError, @LastOutcomeCategory, @CreatedAt, @UpdatedAt);
            """,
            new
            {
                Id = state.Id == Guid.Empty ? Guid.NewGuid() : state.Id,
                EntityId = state.EntityId,
                state.EntityKind,
                state.MediaType,
                state.CapabilityId,
                state.CapabilityKind,
                state.CapabilityVersion,
                state.SubKey,
                state.Status,
                state.Requiredness,
                state.Source,
                state.Confidence,
                state.ArtifactCount,
                state.ArtifactSummary,
                state.ResultSummary,
                LastOperationId = state.LastOperationId,
                FirstAttemptedAt = state.FirstAttemptedAt?.ToString("O"),
                LastAttemptedAt = state.LastAttemptedAt?.ToString("O"),
                SucceededAt = state.SucceededAt?.ToString("O"),
                NextRetryAt = state.NextRetryAt?.ToString("O"),
                Stale = state.Stale ? 1 : 0,
                NeedsRerun = state.NeedsRerun ? 1 : 0,
                state.MissingReason,
                state.LastError,
                state.LastOutcomeCategory,
                CreatedAt = createdAt.ToString("O"),
                UpdatedAt = updatedAt.ToString("O")
            });

        // Ensure is also the version handshake. Preserve the observed status,
        // but move existing rows onto the definition that is about to run so a
        // completed rerun can clear its stale marker.
        conn.Execute("""
            UPDATE entity_capability_states
            SET entity_kind = @EntityKind,
                media_type = @MediaType,
                capability_kind = @CapabilityKind,
                capability_version = @CapabilityVersion,
                requiredness = @Requiredness,
                updated_at = @UpdatedAt
            WHERE entity_id = @EntityId
              AND capability_id = @CapabilityId
              AND COALESCE(sub_key, '') = COALESCE(@SubKey, '');
            """,
            new
            {
                state.EntityId,
                state.EntityKind,
                state.MediaType,
                state.CapabilityId,
                state.CapabilityKind,
                state.CapabilityVersion,
                state.SubKey,
                state.Requiredness,
                UpdatedAt = updatedAt.ToString("O"),
            });

        return GetAsync(state.EntityId, state.CapabilityId, state.SubKey, ct)
            .ContinueWith(
                completed => completed.Result!,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    public Task<EntityCapabilityState?> GetAsync(Guid entityId, string capabilityId, string? subKey = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<Row>(
            SelectSql + """
             WHERE entity_id = @entityId
               AND capability_id = @capabilityId
               AND COALESCE(sub_key, '') = COALESCE(@subKey, '')
             LIMIT 1;
            """,
            new { entityId, capabilityId, subKey });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<IReadOnlyList<EntityCapabilityState>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<Row>(
            SelectSql + " WHERE entity_id = @entityId ORDER BY capability_id ASC, COALESCE(sub_key, '') ASC;",
            new { entityId });
        return Task.FromResult<IReadOnlyList<EntityCapabilityState>>(rows.Select(Map).ToList());
    }

    public Task<IReadOnlyDictionary<string, int>> GetSummaryAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        // Vault items are never counted: this summary does not know who is asking, so the Vault is absent from it for everyone.
        var rows = conn.Query($"""
            SELECT capability_id || ':' || status AS Key, COUNT(*) AS Count
            FROM entity_capability_states
            WHERE {VaultSql.NotVaultEntity("entity_id")}
            GROUP BY capability_id, status;
            """);
        IReadOnlyDictionary<string, int> summary = rows.ToDictionary(
            r => (string)r.Key,
            r => (int)r.Count,
            StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(summary);
    }

    public Task MarkQueuedAsync(Guid entityId, string capabilityId, string? subKey, Guid operationId, CancellationToken ct = default)
        => MarkAttemptAsync(entityId, capabilityId, subKey, EntityCapabilityStatus.Queued, operationId, ct);

    public Task MarkRunningAsync(Guid entityId, string capabilityId, string? subKey, Guid operationId, CancellationToken ct = default)
        => MarkAttemptAsync(entityId, capabilityId, subKey, EntityCapabilityStatus.Running, operationId, ct);

    public Task MarkSucceededAsync(Guid entityId, string capabilityId, string? subKey, CapabilityStateResult result, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE entity_capability_states
            SET status = 'succeeded',
                source = @source,
                confidence = @confidence,
                artifact_count = @artifactCount,
                artifact_summary = @artifactSummary,
                result_summary = @resultSummary,
                last_operation_id = COALESCE(@operationId, last_operation_id),
                last_attempted_at = @now,
                first_attempted_at = COALESCE(first_attempted_at, @now),
                succeeded_at = @now,
                stale = 0,
                needs_rerun = 0,
                missing_reason = NULL,
                last_error = NULL,
                updated_at = @now
            WHERE entity_id = @entityId
              AND capability_id = @capabilityId
              AND COALESCE(sub_key, '') = COALESCE(@subKey, '');
            """,
            new
            {
                entityId,
                capabilityId,
                subKey,
                source = result.Source,
                confidence = result.Confidence,
                artifactCount = result.ArtifactCount,
                artifactSummary = result.ArtifactSummary,
                resultSummary = result.ResultSummary,
                operationId = result.OperationId,
                now = DateTimeOffset.UtcNow.ToString("O")
            });
        return Task.CompletedTask;
    }

    public Task MarkNoResultAsync(Guid entityId, string capabilityId, string? subKey, string reason, CancellationToken ct = default)
        => MarkStateAsync(entityId, capabilityId, subKey, EntityCapabilityStatus.NoResult, missingReason: reason, lastError: null, ct);

    public Task MarkBlockedAsync(Guid entityId, string capabilityId, string? subKey, string reason, CancellationToken ct = default)
        => MarkStateAsync(entityId, capabilityId, subKey, EntityCapabilityStatus.Blocked, missingReason: reason, lastError: reason, ct);

    public Task MarkFailedAsync(Guid entityId, string capabilityId, string? subKey, string error, bool terminal, CancellationToken ct = default)
        => MarkStateAsync(entityId, capabilityId, subKey, terminal ? EntityCapabilityStatus.FailedTerminal : EntityCapabilityStatus.FailedRetryable, null, error, ct);

    public Task MarkFailedForOutcomeAsync(
        Guid entityId,
        string capabilityId,
        string? subKey,
        string error,
        bool terminal,
        BackgroundJobOutcomeCategory category,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE entity_capability_states
            SET status = @status,
                last_error = @error,
                last_outcome_category = @category,
                stale = 0,
                needs_rerun = 0,
                first_attempted_at = COALESCE(first_attempted_at, @now),
                last_attempted_at = @now,
                updated_at = @now
            WHERE entity_id = @entityId
              AND capability_id = @capabilityId
              AND COALESCE(sub_key, '') = COALESCE(@subKey, '');
            """, new
        {
            entityId,
            capabilityId,
            subKey,
            error,
            category = category.ToString(),
            status = terminal ? EntityCapabilityStatus.FailedTerminal : EntityCapabilityStatus.FailedRetryable,
            now = DateTimeOffset.UtcNow.ToString("O"),
        });
        return Task.CompletedTask;
    }

    public Task MarkNotApplicableAsync(Guid entityId, string capabilityId, string? subKey, string reason, CancellationToken ct = default)
        => MarkStateAsync(entityId, capabilityId, subKey, EntityCapabilityStatus.NotApplicable, reason, null, ct);

    public Task InvalidateForCapabilityVersionAsync(string capabilityId, string newVersion, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE entity_capability_states
            SET status = 'stale',
                stale = 1,
                needs_rerun = 1,
                updated_at = @now
            WHERE capability_id = @capabilityId
              AND COALESCE(capability_version, '') <> @newVersion;
            """,
            new { capabilityId, newVersion, now = DateTimeOffset.UtcNow.ToString("O") });
        return Task.CompletedTask;
    }

    private Task MarkAttemptAsync(Guid entityId, string capabilityId, string? subKey, string status, Guid operationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE entity_capability_states
            SET status = @status,
                last_operation_id = @operationId,
                first_attempted_at = COALESCE(first_attempted_at, @now),
                last_attempted_at = @now,
                stale = 0,
                updated_at = @now
            WHERE entity_id = @entityId
              AND capability_id = @capabilityId
              AND COALESCE(sub_key, '') = COALESCE(@subKey, '');
            """,
            new
            {
                entityId,
                capabilityId,
                subKey,
                status,
                operationId,
                now = DateTimeOffset.UtcNow.ToString("O")
            });
        return Task.CompletedTask;
    }

    private Task MarkStateAsync(Guid entityId, string capabilityId, string? subKey, string status, string? missingReason, string? lastError, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE entity_capability_states
            SET status = @status,
                missing_reason = @missingReason,
                last_error = @lastError,
                stale = 0,
                needs_rerun = 0,
                next_retry_at = NULL,
                first_attempted_at = COALESCE(first_attempted_at, @now),
                last_attempted_at = @now,
                updated_at = @now
            WHERE entity_id = @entityId
              AND capability_id = @capabilityId
              AND COALESCE(sub_key, '') = COALESCE(@subKey, '');
            """,
            new { entityId, capabilityId, subKey, status, missingReason, lastError, now = DateTimeOffset.UtcNow.ToString("O") });
        return Task.CompletedTask;
    }

    private const string SelectSql = """
        SELECT id AS Id,
               entity_id AS EntityId,
               entity_kind AS EntityKind,
               media_type AS MediaType,
               capability_id AS CapabilityId,
               capability_kind AS CapabilityKind,
               capability_version AS CapabilityVersion,
               sub_key AS SubKey,
               status AS Status,
               requiredness AS Requiredness,
               source AS Source,
               confidence AS Confidence,
               artifact_count AS ArtifactCount,
               artifact_summary AS ArtifactSummary,
               result_summary AS ResultSummary,
               last_operation_id AS LastOperationId,
               first_attempted_at AS FirstAttemptedAt,
               last_attempted_at AS LastAttemptedAt,
               succeeded_at AS SucceededAt,
               next_retry_at AS NextRetryAt,
               stale AS Stale,
               needs_rerun AS NeedsRerun,
               missing_reason AS MissingReason,
               last_error AS LastError,
               last_outcome_category AS LastOutcomeCategory,
               created_at AS CreatedAt,
               updated_at AS UpdatedAt
        FROM entity_capability_states
        """;

    private sealed class Row
    {
        public Guid Id { get; set; }
        public Guid EntityId { get; set; }
        public string EntityKind { get; set; } = "";
        public string? MediaType { get; set; }
        public string CapabilityId { get; set; } = "";
        public string CapabilityKind { get; set; } = "";
        public string? CapabilityVersion { get; set; }
        public string? SubKey { get; set; }
        public string Status { get; set; } = "";
        public string Requiredness { get; set; } = "";
        public string? Source { get; set; }
        public double? Confidence { get; set; }
        public int ArtifactCount { get; set; }
        public string? ArtifactSummary { get; set; }
        public string? ResultSummary { get; set; }
        public Guid? LastOperationId { get; set; }
        public string? FirstAttemptedAt { get; set; }
        public string? LastAttemptedAt { get; set; }
        public string? SucceededAt { get; set; }
        public string? NextRetryAt { get; set; }
        public int Stale { get; set; }
        public int NeedsRerun { get; set; }
        public string? MissingReason { get; set; }
        public string? LastError { get; set; }
        public string? LastOutcomeCategory { get; set; }
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
    }

    private static EntityCapabilityState Map(Row row) => new()
    {
        Id = row.Id,
        EntityId = row.EntityId,
        EntityKind = row.EntityKind,
        MediaType = row.MediaType,
        CapabilityId = row.CapabilityId,
        CapabilityKind = row.CapabilityKind,
        CapabilityVersion = row.CapabilityVersion,
        SubKey = row.SubKey,
        Status = row.Status,
        Requiredness = row.Requiredness,
        Source = row.Source,
        Confidence = row.Confidence,
        ArtifactCount = row.ArtifactCount,
        ArtifactSummary = row.ArtifactSummary,
        ResultSummary = row.ResultSummary,
        LastOperationId = row.LastOperationId,
        FirstAttemptedAt = ParseDate(row.FirstAttemptedAt),
        LastAttemptedAt = ParseDate(row.LastAttemptedAt),
        SucceededAt = ParseDate(row.SucceededAt),
        NextRetryAt = ParseDate(row.NextRetryAt),
        Stale = row.Stale != 0,
        NeedsRerun = row.NeedsRerun != 0,
        MissingReason = row.MissingReason,
        LastError = row.LastError,
        LastOutcomeCategory = row.LastOutcomeCategory,
        CreatedAt = DateTimeOffset.Parse(row.CreatedAt),
        UpdatedAt = DateTimeOffset.Parse(row.UpdatedAt)
    };

    private static DateTimeOffset? ParseDate(string? value)
        => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
}
