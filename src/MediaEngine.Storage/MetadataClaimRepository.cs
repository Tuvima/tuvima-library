using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IMetadataClaimRepository"/>.
/// Uses Dapper for type-safe column-to-property mapping.
///
/// The <c>metadata_claims</c> table retains append-only observation history.
/// Refreshes supersede old provider observations rather than deleting them.
///
/// Spec: Phase 4 – Invariants § Claim History;
///       Phase 9 – External Metadata Adapters § Claim Persistence.
/// </summary>
public sealed class MetadataClaimRepository : IMetadataClaimRepository
{
    private readonly IDatabaseConnection _db;

    public MetadataClaimRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    // -------------------------------------------------------------------------
    // IMetadataClaimRepository
    // -------------------------------------------------------------------------

    /// <inheritdoc/>
    public async Task InsertBatchAsync(
        IReadOnlyList<MetadataClaim> claims,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (claims.Count == 0)
        {
            return;
        }

        await _db.ExecuteWriteAsync(
            (conn, tx, innerCt) => InsertBatchInTransaction(conn, tx, claims),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends claims on a caller-owned write transaction, so a multi-step change can commit or
    /// roll back with its claims.
    /// </summary>
    internal static void InsertBatchInTransaction(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        IReadOnlyList<MetadataClaim> claims)
    {
        if (claims.Count == 0)
        {
            return;
        }

        EnsureBuiltInProvidersExist(conn, tx, claims);

        const string sql = """
            INSERT INTO metadata_claims
                (id, entity_id, provider_id, decision_source_provider_id, observation_set_id,
                 claim_key, claim_value, confidence, claimed_at, is_user_locked, is_current, superseded_at)
            VALUES
                (@Id, @EntityId, @ProviderId, @DecisionSourceProviderId, @ObservationSetId,
                 @ClaimKey, @ClaimValue, @Confidence, @ClaimedAt, @IsUserLocked, @IsCurrent, @SupersededAt);
            """;

        // Build the batch parameter list — Dapper executes one INSERT per item.
        var rows = claims.Select(c => new
        {
            c.Id,
            c.EntityId,
            c.ProviderId,
            c.DecisionSourceProviderId,
            c.ObservationSetId,
            c.ClaimKey,
            c.ClaimValue,
            c.Confidence,
            ClaimedAt = c.ClaimedAt.ToString("o"),
            IsUserLocked = c.IsUserLocked ? 1 : 0,
            IsCurrent = c.IsCurrent ? 1 : 0,
            SupersededAt = c.SupersededAt?.ToString("O"),
        });

        conn.Execute(sql, rows, transaction: tx);
    }

    /// <inheritdoc/>
    public async Task ReplaceCurrentProviderClaimsAsync(
        Guid entityId,
        Guid providerId,
        IReadOnlyCollection<string> fieldKeys,
        IReadOnlyList<MetadataClaim> claims,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var keys = fieldKeys.Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (keys.Length == 0)
        {
            return;
        }

        var observationSetId = Guid.NewGuid();
        var observedAt = DateTimeOffset.UtcNow;
        await _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            EnsureBuiltInProvidersExist(conn, tx, claims);
            conn.Execute("""
                UPDATE metadata_claims
                SET is_current = 0,
                    superseded_at = @supersededAt
                WHERE entity_id = @entityId
                  AND provider_id = @providerId
                  AND is_current = 1
                  AND is_user_locked = 0
                  AND claim_key IN @keys;
                """, new
            {
                entityId,
                providerId,
                keys,
                supersededAt = observedAt.ToString("O"),
            }, tx);

            if (claims.Count == 0)
            {
                return;
            }

            conn.Execute("""
                INSERT INTO metadata_claims
                    (id, entity_id, provider_id, decision_source_provider_id, observation_set_id,
                     claim_key, claim_value, confidence, claimed_at, is_user_locked, is_current, superseded_at)
                VALUES
                    (@Id, @EntityId, @ProviderId, @DecisionSourceProviderId, @ObservationSetId,
                     @ClaimKey, @ClaimValue, @Confidence, @ClaimedAt, @IsUserLocked, 1, NULL);
                """, claims.Select(claim => new
            {
                Id = claim.Id == Guid.Empty ? Guid.NewGuid() : claim.Id,
                EntityId = entityId,
                ProviderId = providerId,
                claim.DecisionSourceProviderId,
                ObservationSetId = observationSetId,
                claim.ClaimKey,
                claim.ClaimValue,
                claim.Confidence,
                ClaimedAt = observedAt.ToString("O"),
                IsUserLocked = claim.IsUserLocked ? 1 : 0,
            }), tx);
        }, ct).ConfigureAwait(false);
    }

    private static void EnsureBuiltInProvidersExist(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        IReadOnlyList<MetadataClaim> claims)
    {
        if (!claims.Any(c => c.ProviderId == WellKnownProviders.UserManual))
        {
            return;
        }

        conn.Execute("""
            INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled)
            VALUES (@Id, 'user_manual', '1.0', 1);
            """,
            new { Id = WellKnownProviders.UserManual },
            transaction: tx);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<MetadataClaim>> GetByEntityAsync(
        Guid entityId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var results = conn.Query<MetadataClaim>("""
            SELECT id             AS Id,
                   entity_id      AS EntityId,
                   provider_id    AS ProviderId,
                   decision_source_provider_id AS DecisionSourceProviderId,
                   observation_set_id AS ObservationSetId,
                   claim_key      AS ClaimKey,
                   claim_value    AS ClaimValue,
                   confidence     AS Confidence,
                   claimed_at     AS ClaimedAt,
                   is_user_locked AS IsUserLocked,
                   is_current AS IsCurrent,
                   superseded_at AS SupersededAt
            FROM   metadata_claims
            WHERE  entity_id = @entityId
              AND  is_current = 1
            ORDER  BY claimed_at ASC, rowid ASC;
            """, new { entityId }).AsList();

        return Task.FromResult<IReadOnlyList<MetadataClaim>>(results);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<MetadataClaim>>> GetByEntitiesAsync(
        IReadOnlyList<Guid> entityIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (entityIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<MetadataClaim>>>(
                new Dictionary<Guid, IReadOnlyList<MetadataClaim>>());
        }

        using var conn = _db.CreateConnection();
        var results = new List<MetadataClaim>();
        foreach (var batch in entityIds.Where(id => id != Guid.Empty).Distinct().Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            var parameters = new DynamicParameters();
            var placeholders = new string[batch.Length];
            for (var index = 0; index < batch.Length; index++)
            {
                var name = $"entityId{index}";
                placeholders[index] = "@" + name;
                parameters.Add(name, GuidSql.ToBlob(batch[index]));
            }

            results.AddRange(conn.Query<MetadataClaim>("""
                SELECT id             AS Id,
                       entity_id      AS EntityId,
                       provider_id    AS ProviderId,
                       decision_source_provider_id AS DecisionSourceProviderId,
                       observation_set_id AS ObservationSetId,
                       claim_key      AS ClaimKey,
                       claim_value    AS ClaimValue,
                       confidence     AS Confidence,
                       claimed_at     AS ClaimedAt,
                       is_user_locked AS IsUserLocked,
                       is_current AS IsCurrent,
                       superseded_at AS SupersededAt
                FROM metadata_claims
                WHERE entity_id IN (
                """ + string.Join(", ", placeholders) + """
                ) AND is_current = 1
                ORDER BY entity_id, claimed_at;
                """, parameters));
        }

        IReadOnlyDictionary<Guid, IReadOnlyList<MetadataClaim>> grouped = results
            .GroupBy(claim => claim.EntityId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<MetadataClaim>)group.ToList());
        return Task.FromResult(grouped);
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
                "DELETE FROM metadata_claims WHERE entity_id = @entityId;",
                new { entityId });
        }
        finally
        {
            _db.ReleaseWriteLock();
        }
    }
}
