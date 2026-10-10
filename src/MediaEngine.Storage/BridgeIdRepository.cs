using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IBridgeIdRepository"/>.
///
/// Bridge IDs are cross-platform identifiers (ISBN, Apple Books ID, TMDB ID, etc.)
/// that link a library entity to external catalogues and Wikidata. Stored in the
/// dedicated <c>bridge_ids</c> table for clean querying and self-documenting schema.
///
/// Uses Dapper for type-safe column-to-property mapping.
/// </summary>
public sealed class BridgeIdRepository : IBridgeIdRepository
{
    private readonly IDatabaseConnection _db;

    public BridgeIdRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<BridgeIdEntry>> GetByEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var rows = conn.Query<BridgeIdRow>("""
            SELECT id                AS Id,
                   entity_id         AS EntityId,
                   id_type           AS IdType,
                   id_value          AS IdValue,
                   wikidata_property AS WikidataProperty,
                   provider_id       AS ProviderId,
                   created_at        AS CreatedAt
            FROM   bridge_ids
            WHERE  entity_id = @entityId
            ORDER BY id_type;
            """, new { entityId });

        IReadOnlyList<BridgeIdEntry> result = rows.Select(MapRow).ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<BridgeIdEntry>>> GetByEntitiesAsync(
        IReadOnlyList<Guid> entityIds, CancellationToken ct = default)
    {
        if (entityIds.Count == 0)
        {
            IReadOnlyDictionary<Guid, IReadOnlyList<BridgeIdEntry>> empty =
                new Dictionary<Guid, IReadOnlyList<BridgeIdEntry>>();
            return Task.FromResult(empty);
        }

        using var conn = _db.CreateConnection();
        var parameters = new DynamicParameters();
        var placeholders = new string[entityIds.Count];
        for (var i = 0; i < entityIds.Count; i++)
        {
            var name = $"entityId{i}";
            placeholders[i] = "@" + name;
            parameters.Add(name, entityIds[i]);
        }

        var rows = conn.Query<BridgeIdRow>("""
            SELECT id                AS Id,
                   entity_id         AS EntityId,
                   id_type           AS IdType,
                   id_value          AS IdValue,
                   wikidata_property AS WikidataProperty,
                   provider_id       AS ProviderId,
                   created_at        AS CreatedAt
            FROM   bridge_ids
            WHERE  entity_id IN (
            """ + string.Join(", ", placeholders) + """
            )
            ORDER BY entity_id, id_type;
            """, parameters);

        var grouped = rows
            .GroupBy(r => r.EntityId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<BridgeIdEntry>)g.Select(MapRow).ToList());

        IReadOnlyDictionary<Guid, IReadOnlyList<BridgeIdEntry>> result = grouped;
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<BridgeIdEntry?> FindAsync(Guid entityId, string idType, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idType);

        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<BridgeIdRow>("""
            SELECT id                AS Id,
                   entity_id         AS EntityId,
                   id_type           AS IdType,
                   id_value          AS IdValue,
                   wikidata_property AS WikidataProperty,
                   provider_id       AS ProviderId,
                   created_at        AS CreatedAt
            FROM   bridge_ids
            WHERE  entity_id = @entityId
            AND    id_type   = @idType
            LIMIT 1;
            """, new { entityId, idType });

        return Task.FromResult(row is null ? null : MapRow(row));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<BridgeIdEntry>> FindByValueAsync(string idType, string idValue, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idType);
        ArgumentException.ThrowIfNullOrWhiteSpace(idValue);

        using var conn = _db.CreateConnection();
        var rows = conn.Query<BridgeIdRow>("""
            SELECT id                AS Id,
                   entity_id         AS EntityId,
                   id_type           AS IdType,
                   id_value          AS IdValue,
                   wikidata_property AS WikidataProperty,
                   provider_id       AS ProviderId,
                   created_at        AS CreatedAt
            FROM   bridge_ids
            WHERE  id_type  = @idType
            AND    id_value = @idValue;
            """, new { idType, idValue });

        IReadOnlyList<BridgeIdEntry> result = rows.Select(MapRow).ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task UpsertAsync(BridgeIdEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO bridge_ids
                (id, entity_id, id_type, id_value, wikidata_property, provider_id, created_at)
            VALUES
                (@id, @entityId, @idType, @idValue, @wikidataProperty, @providerId, @createdAt)
            ON CONFLICT(entity_id, id_type) DO UPDATE SET
                id_value          = excluded.id_value,
                wikidata_property = excluded.wikidata_property,
                provider_id       = excluded.provider_id;
            """,
            new
            {
                id = entry.Id,
                entityId = entry.EntityId,
                idType = entry.IdType,
                idValue = entry.IdValue,
                wikidataProperty = entry.WikidataProperty,
                providerId = entry.ProviderId,
                createdAt = entry.CreatedAt.ToString("O"),
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpsertBatchAsync(IReadOnlyList<BridgeIdEntry> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _db.ExecuteWriteAsync(
            (conn, tx, innerCt) => UpsertBatchInTransaction(conn, tx, entries),
            ct);
    }

    /// <summary>
    /// Upserts bridge ids on a caller-owned write transaction, so a multi-step change can commit
    /// or roll back with its bridge ids.
    /// </summary>
    internal static void UpsertBatchInTransaction(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        IReadOnlyList<BridgeIdEntry> entries)
    {
        foreach (var entry in entries)
        {
            conn.Execute("""
                INSERT INTO bridge_ids
                    (id, entity_id, id_type, id_value, wikidata_property, provider_id, created_at)
                VALUES
                    (@id, @entityId, @idType, @idValue, @wikidataProperty, @providerId, @createdAt)
                ON CONFLICT(entity_id, id_type) DO UPDATE SET
                    id_value          = excluded.id_value,
                    wikidata_property = excluded.wikidata_property,
                    provider_id       = excluded.provider_id;
                """,
                new
                {
                    id = entry.Id,
                    entityId = entry.EntityId,
                    idType = entry.IdType,
                    idValue = entry.IdValue,
                    wikidataProperty = entry.WikidataProperty,
                    providerId = entry.ProviderId,
                    createdAt = entry.CreatedAt.ToString("O"),
                },
                transaction: tx);
        }
    }

    /// <inheritdoc/>
    public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        conn.Execute("""
            DELETE FROM bridge_ids
            WHERE entity_id = @entityId;
            """, new { entityId });

        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private static BridgeIdEntry MapRow(BridgeIdRow row) => new()
    {
        Id = row.Id,
        EntityId = row.EntityId,
        IdType = row.IdType,
        IdValue = row.IdValue,
        WikidataProperty = row.WikidataProperty,
        ProviderId = row.ProviderId,
        CreatedAt = DateTimeOffset.TryParse(row.CreatedAt, out var dt) ? dt : DateTimeOffset.UtcNow,
    };

    /// <summary>Internal DTO for raw Dapper mapping (all fields as strings for SQLite compatibility).</summary>
    private sealed class BridgeIdRow
    {
        public Guid Id { get; set; }
        public Guid EntityId { get; set; }
        public string IdType { get; set; } = "";
        public string IdValue { get; set; } = "";
        public string? WikidataProperty { get; set; }
        public string? ProviderId { get; set; }
        public string CreatedAt { get; set; } = "";
    }
}
