using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Jobs;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="ICanonicalValueRepository"/>.
///
/// Canonical values use a composite primary key (entity_id, key), so each
/// upsert replaces the previous winner for a given field.  The full scoring
/// history is preserved in <c>metadata_claims</c>; only the current winner
/// lives here.
///
/// Spec: Phase 4 – Canonical Integrity invariant;
///       Phase 9 – External Metadata Adapters § Canonical Persistence;
///       Phase B – Conflict Surfacing (B-05).
/// </summary>
public sealed class CanonicalValueRepository : ICanonicalValueRepository, IAiFeaturePersistenceRepository
{
    private readonly IDatabaseConnection _db;

    public CanonicalValueRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    // -------------------------------------------------------------------------
    // ICanonicalValueRepository
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task UpsertBatchAsync(
        IReadOnlyList<CanonicalValue> values,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var scalarValues = values
            .Where(cv => !MetadataFieldConstants.IsMultiValued(cv.Key))
            .ToList();

        if (scalarValues.Count == 0)
        {
            return;
        }

        // Single transaction: atomicity + significant write-performance gain.
        await _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();

            // Update in place so the winner changes atomically without the
            // delete/reinsert side effects of INSERT OR REPLACE.
            conn.Execute("""
                INSERT INTO canonical_values
                    (entity_id, key, value, last_scored_at, is_conflicted, winning_provider_id, needs_review)
                VALUES
                    (@EntityId, @Key, @Value, @LastScoredAt, @IsConflicted, @WinningProviderId, @NeedsReview)
                ON CONFLICT(entity_id, key) DO UPDATE SET
                    value = excluded.value,
                    last_scored_at = excluded.last_scored_at,
                    is_conflicted = excluded.is_conflicted,
                    winning_provider_id = excluded.winning_provider_id,
                    needs_review = excluded.needs_review;
                """,
                scalarValues.Select(cv => new
                {
                    cv.EntityId,
                    cv.Key,
                    Value = DescriptionText.IsDescription(cv.Key) ? DescriptionText.Normalize(cv.Value) : cv.Value,
                    LastScoredAt = cv.LastScoredAt.ToString("o"),
                    IsConflicted = cv.IsConflicted ? 1 : 0,
                    cv.WinningProviderId,
                    NeedsReview = cv.NeedsReview ? 1 : 0,
                }),
                transaction: tx);

        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CanonicalValue>> GetByEntityAsync(
        Guid entityId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<CanonicalValueRow>("""
            SELECT entity_id           AS EntityId,
                   key                 AS Key,
                   value               AS Value,
                   last_scored_at      AS LastScoredAt,
                   is_conflicted       AS IsConflicted,
                   winning_provider_id AS WinningProviderId,
                   needs_review        AS NeedsReview
            FROM   canonical_values
            WHERE  entity_id = @entityId
            ORDER  BY key ASC;
            """, new { entityId }).AsList();

        return Task.FromResult<IReadOnlyList<CanonicalValue>>(rows.ConvertAll(MapRow));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>>> GetByEntitiesAsync(
        IReadOnlyList<Guid> entityIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (entityIds.Count == 0)
        {
            IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>> empty =
                new Dictionary<Guid, IReadOnlyList<CanonicalValue>>();
            return Task.FromResult(empty);
        }

        using var conn = _db.CreateConnection();
        var rows = new List<CanonicalValueRow>();
        foreach (var batch in entityIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();

            var parameters = new DynamicParameters();
            var placeholders = new string[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                var name = $"entityId{i}";
                placeholders[i] = "@" + name;
                parameters.Add(name, GuidSql.ToBlob(batch[i]));
            }

            rows.AddRange(conn.Query<CanonicalValueRow>("""
                SELECT entity_id           AS EntityId,
                       key                 AS Key,
                       value               AS Value,
                       last_scored_at      AS LastScoredAt,
                       is_conflicted       AS IsConflicted,
                       winning_provider_id AS WinningProviderId,
                       needs_review        AS NeedsReview
                FROM   canonical_values
                WHERE  entity_id IN (
                """ + string.Join(", ", placeholders) + """
                )
                ORDER  BY entity_id, key ASC;
                """, parameters));
        }

        var grouped = rows
            .GroupBy(r => r.EntityId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<CanonicalValue>)g.Select(MapRow).ToList());

        IReadOnlyDictionary<Guid, IReadOnlyList<CanonicalValue>> result = grouped;
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CanonicalValue>> GetConflictedAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<CanonicalValueRow>("""
            SELECT entity_id           AS EntityId,
                   key                 AS Key,
                   value               AS Value,
                   last_scored_at      AS LastScoredAt,
                   is_conflicted       AS IsConflicted,
                   winning_provider_id AS WinningProviderId,
                   needs_review        AS NeedsReview
            FROM   canonical_values
            WHERE  is_conflicted = 1
            ORDER  BY last_scored_at DESC;
            """).AsList();

        return Task.FromResult<IReadOnlyList<CanonicalValue>>(rows.ConvertAll(MapRow));
    }

    /// <inheritdoc/>
    public async Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await _db.AcquireWriteLockAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = _db.CreateConnection();
            conn.Execute(
                "DELETE FROM canonical_values WHERE entity_id = @entityId;",
                new { entityId });
        }
        finally
        {
            _db.ReleaseWriteLock();
        }
    }

    /// <inheritdoc/>
    public async Task DeleteByKeyAsync(Guid entityId, string key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await _db.AcquireWriteLockAsync(ct).ConfigureAwait(false);
        try
        {
            using var conn = _db.CreateConnection();
            conn.Execute(
                "DELETE FROM canonical_values WHERE entity_id = @entityId AND key = @key;",
                new { entityId, key });
        }
        finally
        {
            _db.ReleaseWriteLock();
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Guid>> FindByValueAsync(
        string key,
        string value,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        using var conn = _db.CreateConnection();
        var ids = conn.Query<Guid>("""
            SELECT entity_id
            FROM   canonical_values
            WHERE  key   = @key   COLLATE NOCASE
              AND  value = @value COLLATE NOCASE;
            """, new { key, value })
            .ToList();

        return Task.FromResult<IReadOnlyList<Guid>>(ids);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<CanonicalValue>> FindByKeyAndPrefixAsync(
        string key,
        string prefix,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(prefix);

        using var conn = _db.CreateConnection();
        var rows = conn.Query<CanonicalValueRow>("""
            SELECT entity_id           AS EntityId,
                   key                 AS Key,
                   value               AS Value,
                   last_scored_at      AS LastScoredAt,
                   is_conflicted       AS IsConflicted,
                   winning_provider_id AS WinningProviderId,
                   needs_review        AS NeedsReview
            FROM   canonical_values
            WHERE  key   = @key   COLLATE NOCASE
              AND  value LIKE @prefix || '%';
            """, new { key, prefix }).AsList();

        return Task.FromResult<IReadOnlyList<CanonicalValue>>(rows.ConvertAll(MapRow));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Guid>> GetEntitiesNeedingEnrichmentAsync(
        string hasField,
        string missingField,
        int limit,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(hasField);
        ArgumentException.ThrowIfNullOrWhiteSpace(missingField);

        using var conn = _db.CreateConnection();
        var ids = conn.Query<Guid>("""
            SELECT DISTINCT cv1.entity_id
            FROM   canonical_values cv1
            WHERE  cv1.key IN (@HasField1, @HasField2)
              AND  NOT EXISTS (
                       SELECT 1
                       FROM canonical_values existing_scalar
                       WHERE existing_scalar.entity_id = cv1.entity_id
                         AND existing_scalar.key = @MissingField
                   )
              AND  NOT EXISTS (
                       SELECT 1
                       FROM canonical_value_arrays existing_array
                       WHERE existing_array.entity_id = cv1.entity_id
                         AND existing_array.key = @MissingField
                   )
            LIMIT  @Limit;
            """, new
        {
            HasField1 = hasField,
            HasField2 = "plot_summary",
            MissingField = missingField,
            Limit = limit,
        })
            .ToList();

        return Task.FromResult<IReadOnlyList<Guid>>(ids);
    }

    public async Task<AiFeatureWriteResult> ReplaceAiFeatureAsync(
        AiFeatureWriteRequest request,
        CancellationToken ct = default)
    {
        ValidateAiFeatureWrite(request);
        ct.ThrowIfCancellationRequested();

        var normalizedArrays = request.ArrayValues.ToDictionary(
            pair => pair.Key.Trim(),
            pair => (IReadOnlyList<string>)pair.Value
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            StringComparer.OrdinalIgnoreCase);
        var normalizedScalars = request.ScalarValues.ToDictionary(
            pair => pair.Key.Trim(),
            pair => string.IsNullOrWhiteSpace(pair.Value) ? null : pair.Value!.Trim(),
            StringComparer.OrdinalIgnoreCase);
        var outputFingerprint = ComputeOutputFingerprint(normalizedArrays, normalizedScalars);
        var now = DateTimeOffset.UtcNow;

        return await _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            var previous = LoadAiFeatureState(conn, tx, request.EntityId, request.FeatureKey);
            if (previous is not null
                && previous.Attempts == 0
                && previous.Status is AiFeatureStatus.Published or AiFeatureStatus.ReviewRequired or AiFeatureStatus.Protected
                && string.Equals(previous.InputFingerprint, request.InputFingerprint, StringComparison.Ordinal)
                && string.Equals(previous.OutputFingerprint, outputFingerprint, StringComparison.Ordinal)
                && ArePublishedValuesCurrent(conn, tx, request.EntityId, previous))
            {
                return new AiFeatureWriteResult(
                    previous.Status,
                    previous.PublishedFields,
                    previous.ProtectedFields,
                    IsUnchanged: true);
            }

            var published = new List<string>();
            var protectedFields = new List<string>();
            var publishedValues = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (request.Confidence >= request.PublishThreshold)
            {
                foreach (var (key, values) in normalizedArrays)
                {
                    innerCt.ThrowIfCancellationRequested();
                    var existing = conn.Query<ExistingArrayValueRow>(
                        """
                        SELECT value AS Value, value_qid AS ValueQid
                        FROM canonical_value_arrays
                        WHERE entity_id = @entityId AND key = @key
                        ORDER BY ordinal;
                        """,
                        new { entityId = request.EntityId, key },
                        transaction: tx).ToList();

                    IReadOnlyList<string>? priorValues = null;
                    var hasPriorOutput = previous is not null
                        && previous.PublishedValues.TryGetValue(key, out priorValues);
                    var stillAiOwned = hasPriorOutput
                        && existing.All(row => row.ValueQid is null)
                        && existing.Select(row => row.Value).SequenceEqual(priorValues!, StringComparer.Ordinal);
                    if ((existing.Count > 0 && !stillAiOwned)
                        || (hasPriorOutput && !stillAiOwned))
                    {
                        protectedFields.Add(key);
                        continue;
                    }

                    conn.Execute(
                        "DELETE FROM canonical_value_arrays WHERE entity_id = @entityId AND key = @key;",
                        new { entityId = request.EntityId, key },
                        transaction: tx);

                    if (values.Count > 0)
                    {
                        conn.Execute(
                            """
                            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value, value_qid)
                            VALUES (@EntityId, @Key, @Ordinal, @Value, NULL);
                            """,
                            values.Select((value, ordinal) => new
                            {
                                EntityId = request.EntityId,
                                Key = key,
                                Ordinal = ordinal,
                                Value = value,
                            }),
                            transaction: tx);
                    }
                    published.Add(key);
                    publishedValues[key] = values;
                }

                var scalarNeedsReview = request.Confidence < request.ReviewThreshold;
                foreach (var (key, value) in normalizedScalars)
                {
                    innerCt.ThrowIfCancellationRequested();
                    var existing = conn.QueryFirstOrDefault<ExistingCanonicalOwnerRow>(
                        """
                        SELECT value AS Value, winning_provider_id AS WinningProviderId
                        FROM canonical_values
                        WHERE entity_id = @entityId AND key = @key
                        LIMIT 1;
                        """,
                        new { entityId = request.EntityId, key },
                        transaction: tx);

                    IReadOnlyList<string>? priorValues = null;
                    var hasPriorOutput = previous is not null
                        && previous.PublishedValues.TryGetValue(key, out priorValues);
                    var expectedPrior = hasPriorOutput && priorValues!.Count == 1 ? priorValues[0] : null;
                    var stillAiOwned = hasPriorOutput
                        && (existing is null
                            ? priorValues!.Count == 0
                            : existing.WinningProviderId == request.ProviderId
                              && priorValues!.Count == 1
                              && string.Equals(existing.Value, expectedPrior, StringComparison.Ordinal));
                    if ((existing is not null && !stillAiOwned)
                        || (hasPriorOutput && !stillAiOwned))
                    {
                        protectedFields.Add(key);
                        continue;
                    }

                    if (value is null)
                    {
                        if (existing is not null)
                        {
                            conn.Execute(
                                "DELETE FROM canonical_values WHERE entity_id = @entityId AND key = @key;",
                                new { entityId = request.EntityId, key },
                                transaction: tx);
                        }
                        published.Add(key);
                        publishedValues[key] = [];
                        continue;
                    }

                    conn.Execute(
                        """
                        INSERT INTO canonical_values
                            (entity_id, key, value, last_scored_at, is_conflicted, winning_provider_id, needs_review)
                        VALUES
                            (@EntityId, @Key, @Value, @LastScoredAt, 0, @WinningProviderId, @NeedsReview)
                        ON CONFLICT(entity_id, key) DO UPDATE SET
                            value = excluded.value,
                            last_scored_at = excluded.last_scored_at,
                            is_conflicted = 0,
                            winning_provider_id = excluded.winning_provider_id,
                            needs_review = excluded.needs_review;
                        """,
                        new
                        {
                            request.EntityId,
                            Key = key,
                            Value = value,
                            LastScoredAt = now.ToString("O"),
                            WinningProviderId = request.ProviderId,
                            NeedsReview = scalarNeedsReview ? 1 : 0,
                        },
                        transaction: tx);
                    published.Add(key);
                    publishedValues[key] = [value];
                }
            }

            var status = request.Confidence < request.PublishThreshold
                ? AiFeatureStatus.ReviewRequired
                : published.Count == 0 && protectedFields.Count > 0
                    ? AiFeatureStatus.Protected
                    : request.Confidence < request.ReviewThreshold || protectedFields.Count > 0
                        ? AiFeatureStatus.ReviewRequired
                        : AiFeatureStatus.Published;

            UpsertAiFeatureState(
                conn,
                tx,
                request.EntityId,
                request.FeatureKey,
                request.ProviderId,
                status,
                request.Confidence,
                request.ModelId,
                request.PromptVersion,
                request.InputFingerprint,
                outputFingerprint,
                attempts: 0,
                nextRetryAt: null,
                lastError: null,
                lastOutcomeCategory: null,
                outcomeReason: null,
                published,
                protectedFields,
                publishedValues,
                now);

            return new AiFeatureWriteResult(status, published, protectedFields, IsUnchanged: false);
        }, ct).ConfigureAwait(false);
    }

    public Task<AiFeatureState?> GetAiFeatureStateAsync(
        Guid entityId,
        string featureKey,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(featureKey);

        using var conn = _db.CreateConnection();
        return Task.FromResult(LoadAiFeatureState(conn, transaction: null, entityId, featureKey));
    }

    public async Task<AiFeatureState> RecordAiFeatureFailureAsync(
        AiFeatureFailureRequest request,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FeatureKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PromptVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Error);
        if (request.MaxAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Maximum attempts must be positive.");
        }

        ct.ThrowIfCancellationRequested();
        return await _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            var previous = LoadAiFeatureState(conn, tx, request.EntityId, request.FeatureKey);
            var policy = BackgroundJobOutcomePolicy.For(request.Category);
            var attempts = checked((previous?.Attempts ?? 0) + (policy.ConsumesPoisonBudget ? 1 : 0));
            var status = policy.IsTerminal || (policy.ConsumesPoisonBudget && attempts >= request.MaxAttempts)
                ? AiFeatureStatus.Poisoned
                : AiFeatureStatus.RetryPending;
            var retryDelay = request.InitialRetryDelay
                ?? (request.Category == BackgroundJobOutcomeCategory.UnavailableCapability
                    ? TimeSpan.FromMinutes(30)
                    : TimeSpan.FromMinutes(5));
            var retryAttempt = Math.Max(attempts, 1);
            var nextRetry = status == AiFeatureStatus.RetryPending
                ? DateTimeOffset.UtcNow.Add(TimeSpan.FromTicks(Math.Min(
                    retryDelay.Ticks * (long)Math.Pow(2, retryAttempt - 1),
                    TimeSpan.FromHours(6).Ticks)))
                : (DateTimeOffset?)null;
            var now = DateTimeOffset.UtcNow;

            UpsertAiFeatureState(
                conn,
                tx,
                request.EntityId,
                request.FeatureKey,
                request.ProviderId,
                status,
                previous?.Confidence,
                request.ModelId,
                request.PromptVersion,
                request.InputFingerprint,
                previous?.OutputFingerprint,
                attempts,
                nextRetry,
                request.Error.Length > 2000 ? request.Error[..2000] : request.Error,
                request.Category,
                outcomeReason: null,
                previous?.PublishedFields ?? [],
                previous?.ProtectedFields ?? [],
                previous?.PublishedValues ?? new Dictionary<string, IReadOnlyList<string>>(),
                now);

            return new AiFeatureState(
                request.EntityId,
                request.FeatureKey,
                request.ProviderId,
                status,
                previous?.Confidence,
                request.ModelId,
                request.PromptVersion,
                request.InputFingerprint,
                previous?.OutputFingerprint,
                attempts,
                nextRetry,
                request.Error,
                OutcomeReason: null,
                request.Category,
                previous?.PublishedFields ?? [],
                previous?.ProtectedFields ?? [],
                previous?.PublishedValues ?? new Dictionary<string, IReadOnlyList<string>>(),
                now);
        }, ct).ConfigureAwait(false);
    }

    public Task<int> RecoverUnavailableCapabilityFailuresAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow.ToString("O");
            return conn.Execute(
                """
                UPDATE ai_feature_artifacts
                SET status = 'RetryPending',
                    attempts = 0,
                    next_retry_at = @now,
                    last_outcome_category = 'UnavailableCapability',
                    outcome_reason = CASE
                        WHEN outcome_reason IS NULL OR trim(outcome_reason) = ''
                            THEN 'Released at launch: the configured AI model was unavailable.'
                        ELSE outcome_reason || ' Released at launch: the configured AI model was unavailable.'
                    END,
                    updated_at = @now
                WHERE status = 'Poisoned'
                  AND (
                      last_outcome_category = 'UnavailableCapability'
                      OR (
                          last_outcome_category IS NULL
                          AND (
                              lower(last_error) LIKE '%model%unavailable%'
                              OR lower(last_error) LIKE '%model file%not found%'
                              OR lower(last_error) LIKE '%model%not downloaded%'
                              OR lower(last_error) LIKE '%requested ai model%unavailable%'
                          )
                      )
                  );
                """,
                new { now },
                tx);
        }, ct);
    }

    private static void ValidateAiFeatureWrite(AiFeatureWriteRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FeatureKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PromptVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputFingerprint);
        ArgumentNullException.ThrowIfNull(request.ArrayValues);
        ArgumentNullException.ThrowIfNull(request.ScalarValues);

        if (request.Confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Confidence must be between zero and one.");
        }

        if (request.PublishThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Publish threshold must be between zero and one.");
        }

        if (request.ReviewThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Review threshold must be between zero and one.");
        }

        if (request.ArrayValues.Keys.Any(key => !MetadataFieldConstants.IsMultiValued(key)))
        {
            throw new ArgumentException("AI array output contains a scalar canonical key.", nameof(request));
        }

        if (request.ScalarValues.Keys.Any(MetadataFieldConstants.IsMultiValued))
        {
            throw new ArgumentException("AI scalar output contains a multi-valued canonical key.", nameof(request));
        }
    }

    private static string ComputeOutputFingerprint(
        IReadOnlyDictionary<string, IReadOnlyList<string>> arrays,
        IReadOnlyDictionary<string, string?> scalars)
    {
        var payload = new StringBuilder();
        foreach (var (key, values) in arrays.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            payload.Append("a:").Append(key.ToLowerInvariant()).Append('\n');
            foreach (var value in values)
            {
                payload.Append(value).Append('\n');
            }
        }
        foreach (var (key, value) in scalars.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            payload.Append("s:").Append(key.ToLowerInvariant()).Append('=').Append(value).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    private static AiFeatureState? LoadAiFeatureState(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction,
        Guid entityId,
        string featureKey)
    {
        var row = conn.QueryFirstOrDefault<AiFeatureStateRow>(
            """
            SELECT entity_id AS EntityId,
                   feature_key AS FeatureKey,
                   source_provider_id AS ProviderId,
                   status AS Status,
                   confidence AS Confidence,
                   model_id AS ModelId,
                   prompt_version AS PromptVersion,
                   input_fingerprint AS InputFingerprint,
                   output_fingerprint AS OutputFingerprint,
                   attempts AS Attempts,
                   next_retry_at AS NextRetryAt,
                   last_error AS LastError,
                   last_outcome_category AS LastOutcomeCategory,
                   outcome_reason AS OutcomeReason,
                   published_fields_json AS PublishedFieldsJson,
                   protected_fields_json AS ProtectedFieldsJson,
                   published_values_json AS PublishedValuesJson,
                   updated_at AS UpdatedAt
            FROM ai_feature_artifacts
            WHERE entity_id = @entityId AND feature_key = @featureKey
            LIMIT 1;
            """,
            new { entityId, featureKey },
            transaction: transaction);
        return row is null ? null : MapAiFeatureState(row);
    }

    private static void UpsertAiFeatureState(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        Guid entityId,
        string featureKey,
        Guid providerId,
        AiFeatureStatus status,
        double? confidence,
        string? modelId,
        string? promptVersion,
        string? inputFingerprint,
        string? outputFingerprint,
        int attempts,
        DateTimeOffset? nextRetryAt,
        string? lastError,
        BackgroundJobOutcomeCategory? lastOutcomeCategory,
        string? outcomeReason,
        IReadOnlyList<string> publishedFields,
        IReadOnlyList<string> protectedFields,
        IReadOnlyDictionary<string, IReadOnlyList<string>> publishedValues,
        DateTimeOffset updatedAt)
    {
        conn.Execute(
            """
            INSERT INTO ai_feature_artifacts
                (entity_id, feature_key, source_provider_id, status, confidence,
                 model_id, prompt_version, input_fingerprint, output_fingerprint,
                 attempts, last_outcome_category, next_retry_at, last_error, outcome_reason, published_fields_json,
                 protected_fields_json, published_values_json, updated_at)
            VALUES
                (@EntityId, @FeatureKey, @ProviderId, @Status, @Confidence,
                 @ModelId, @PromptVersion, @InputFingerprint, @OutputFingerprint,
                 @Attempts, @LastOutcomeCategory, @NextRetryAt, @LastError, @OutcomeReason, @PublishedFieldsJson,
                 @ProtectedFieldsJson, @PublishedValuesJson, @UpdatedAt)
            ON CONFLICT(entity_id, feature_key) DO UPDATE SET
                source_provider_id = excluded.source_provider_id,
                status = excluded.status,
                confidence = excluded.confidence,
                model_id = excluded.model_id,
                prompt_version = excluded.prompt_version,
                input_fingerprint = excluded.input_fingerprint,
                output_fingerprint = excluded.output_fingerprint,
                attempts = excluded.attempts,
                last_outcome_category = excluded.last_outcome_category,
                next_retry_at = excluded.next_retry_at,
                last_error = excluded.last_error,
                outcome_reason = excluded.outcome_reason,
                published_fields_json = excluded.published_fields_json,
                protected_fields_json = excluded.protected_fields_json,
                published_values_json = excluded.published_values_json,
                updated_at = excluded.updated_at;
            """,
            new
            {
                EntityId = entityId,
                FeatureKey = featureKey,
                ProviderId = providerId,
                Status = status.ToString(),
                Confidence = confidence,
                ModelId = modelId,
                PromptVersion = promptVersion,
                InputFingerprint = inputFingerprint,
                OutputFingerprint = outputFingerprint,
                Attempts = attempts,
                LastOutcomeCategory = lastOutcomeCategory?.ToString(),
                NextRetryAt = nextRetryAt?.ToString("O"),
                LastError = lastError,
                OutcomeReason = outcomeReason,
                PublishedFieldsJson = JsonSerializer.Serialize(publishedFields),
                ProtectedFieldsJson = JsonSerializer.Serialize(protectedFields),
                PublishedValuesJson = JsonSerializer.Serialize(publishedValues),
                UpdatedAt = updatedAt.ToString("O"),
            },
            transaction: transaction);
    }

    private static AiFeatureState MapAiFeatureState(AiFeatureStateRow row)
    {
        return new AiFeatureState(
            row.EntityId,
            row.FeatureKey,
            row.ProviderId,
            Enum.TryParse<AiFeatureStatus>(row.Status, out var status) ? status : AiFeatureStatus.Poisoned,
            row.Confidence,
            row.ModelId,
            row.PromptVersion,
            row.InputFingerprint,
            row.OutputFingerprint,
            row.Attempts,
            DateTimeOffset.TryParse(row.NextRetryAt, out var nextRetry) ? nextRetry : null,
            row.LastError,
            row.OutcomeReason,
            Enum.TryParse<BackgroundJobOutcomeCategory>(row.LastOutcomeCategory, out var category) ? category : null,
            DeserializeFieldList(row.PublishedFieldsJson),
            DeserializeFieldList(row.ProtectedFieldsJson),
            DeserializePublishedValues(row.PublishedValuesJson),
            DateTimeOffset.TryParse(row.UpdatedAt, out var updatedAt) ? updatedAt : DateTimeOffset.MinValue);
    }

    private static IReadOnlyList<string> DeserializeFieldList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Stored AI feature provenance contains malformed field metadata JSON.",
                ex);
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> DeserializePublishedValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json)
                ?? new Dictionary<string, string[]>();
            return values.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Stored AI feature provenance contains malformed published-value JSON.",
                ex);
        }
    }

    private static bool ArePublishedValuesCurrent(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        Guid entityId,
        AiFeatureState state)
    {
        foreach (var (key, expected) in state.PublishedValues)
        {
            if (MetadataFieldConstants.IsMultiValued(key))
            {
                var current = conn.Query<ExistingArrayValueRow>(
                    """
                    SELECT value AS Value, value_qid AS ValueQid
                    FROM canonical_value_arrays
                    WHERE entity_id = @entityId AND key = @key
                    ORDER BY ordinal;
                    """,
                    new { entityId, key },
                    transaction: transaction).ToList();
                if (current.Any(row => row.ValueQid is not null)
                    || !current.Select(row => row.Value).SequenceEqual(expected, StringComparer.Ordinal))
                {
                    return false;
                }

                continue;
            }

            var currentScalar = conn.QueryFirstOrDefault<ExistingCanonicalOwnerRow>(
                """
                SELECT value AS Value, winning_provider_id AS WinningProviderId
                FROM canonical_values
                WHERE entity_id = @entityId AND key = @key
                LIMIT 1;
                """,
                new { entityId, key },
                transaction: transaction);
            if (expected.Count == 0)
            {
                if (currentScalar is not null)
                {
                    return false;
                }
            }
            else if (expected.Count != 1
                     || currentScalar?.WinningProviderId != state.ProviderId
                     || !string.Equals(currentScalar.Value, expected[0], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // -------------------------------------------------------------------------
    // Private intermediate row type and mapper
    // -------------------------------------------------------------------------

    private sealed class ExistingCanonicalOwnerRow
    {
        public string Value { get; init; } = string.Empty;
        public Guid? WinningProviderId { get; init; }
    }

    private sealed class ExistingArrayValueRow
    {
        public string Value { get; init; } = string.Empty;
        public string? ValueQid { get; init; }
    }

    private sealed class AiFeatureStateRow
    {
        public Guid EntityId { get; init; }
        public string FeatureKey { get; init; } = string.Empty;
        public Guid ProviderId { get; init; }
        public string Status { get; init; } = string.Empty;
        public double? Confidence { get; init; }
        public string? ModelId { get; init; }
        public string? PromptVersion { get; init; }
        public string? InputFingerprint { get; init; }
        public string? OutputFingerprint { get; init; }
        public int Attempts { get; init; }
        public string? NextRetryAt { get; init; }
        public string? LastError { get; init; }
        public string? LastOutcomeCategory { get; init; }
        public string? OutcomeReason { get; init; }
        public string? PublishedFieldsJson { get; init; }
        public string? ProtectedFieldsJson { get; init; }
        public string? PublishedValuesJson { get; init; }
        public string? UpdatedAt { get; init; }
    }

    /// <summary>
    /// Intermediate row type for Dapper mapping.
    /// <see cref="IsConflicted"/> and <see cref="NeedsReview"/> are integers (0/1) in SQLite;
    /// <see cref="WinningProviderId"/> is a nullable BLOB Guid;
    /// <see cref="LastScoredAt"/> is TEXT ISO-8601.
    /// </summary>
    private sealed class CanonicalValueRow
    {
        public Guid EntityId { get; set; }
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string LastScoredAt { get; set; } = string.Empty;
        public int IsConflicted { get; set; }
        public Guid? WinningProviderId { get; set; }
        public int NeedsReview { get; set; }
    }

    private static CanonicalValue MapRow(CanonicalValueRow r)
    {
        return new CanonicalValue
        {
            EntityId = r.EntityId,
            Key = r.Key,
            Value = r.Value,
            LastScoredAt = DateTimeOffset.Parse(r.LastScoredAt),
            IsConflicted = r.IsConflicted == 1,
            WinningProviderId = r.WinningProviderId,
            NeedsReview = r.NeedsReview == 1,
        };
    }
}
