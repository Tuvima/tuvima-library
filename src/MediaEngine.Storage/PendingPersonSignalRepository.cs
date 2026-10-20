using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IPendingPersonSignalRepository"/>.
/// Stores unverified person signals between inline extraction and deferred
/// batch Wikidata verification.
/// </summary>
public sealed class PendingPersonSignalRepository : IPendingPersonSignalRepository
{
    private readonly IDatabaseConnection _db;

    public PendingPersonSignalRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public Task InsertAsync(PendingPersonSignal signal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT OR IGNORE INTO pending_person_signals
                (id, entity_id, name, role, source, pattern, media_type, created_at)
            VALUES
                (@Id, @EntityId, @Name, @Role, @Source, @Pattern, @MediaType, @CreatedAt);
            """,
            new
            {
                Id = signal.Id,
                EntityId = signal.EntityId,
                signal.Name,
                signal.Role,
                signal.Source,
                signal.Pattern,
                signal.MediaType,
                signal.CreatedAt,
            });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task InsertBatchAsync(IReadOnlyList<PendingPersonSignal> signals, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signals);

        if (signals.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            foreach (var s in signals)
            {
                conn.Execute("""
                    INSERT OR IGNORE INTO pending_person_signals
                        (id, entity_id, name, role, source, pattern, media_type, created_at)
                    VALUES
                        (@Id, @EntityId, @Name, @Role, @Source, @Pattern, @MediaType, @CreatedAt);
                    """,
                    new
                    {
                        Id = s.Id,
                        EntityId = s.EntityId,
                        s.Name,
                        s.Role,
                        s.Source,
                        s.Pattern,
                        s.MediaType,
                        s.CreatedAt,
                    },
                    tx);
            }

        }, ct);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<PendingPersonSignal>> GetAllPendingAsync(CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var rows = conn.Query<PendingSignalRow>("""
            SELECT id, entity_id, name, role, source, pattern, media_type, created_at
            FROM   pending_person_signals
            ORDER BY created_at ASC;
            """).AsList();

        return Task.FromResult<IReadOnlyList<PendingPersonSignal>>(rows.ConvertAll(MapRow));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<(string Name, string Role)>> GetUniqueNameRolePairsAsync(CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var rows = conn.Query<(string Name, string Role)>("""
            SELECT DISTINCT name, role
            FROM   pending_person_signals
            ORDER BY name ASC;
            """).AsList();

        return Task.FromResult<IReadOnlyList<(string Name, string Role)>>(rows);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<PendingPersonSignal>> GetByNameAndRoleAsync(
        string name, string role, CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var rows = conn.Query<PendingSignalRow>("""
            SELECT id, entity_id, name, role, source, pattern, media_type, created_at
            FROM   pending_person_signals
            WHERE  name = @name
              AND  role = @role
            ORDER BY created_at ASC;
            """,
            new { name, role }).AsList();

        return Task.FromResult<IReadOnlyList<PendingPersonSignal>>(rows.ConvertAll(MapRow));
    }

    /// <inheritdoc/>
    public Task DeleteByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        using var conn = _db.CreateConnection();
        conn.Execute(
            "DELETE FROM pending_person_signals WHERE id IN @Ids;",
            new { Ids = ids.Select(GuidSql.ToBlob).ToArray() });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeleteAllAsync(CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        conn.Execute("DELETE FROM pending_person_signals;");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<int> GetCountAsync(CancellationToken ct = default)
    {
        using var conn = _db.CreateConnection();
        var count = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM pending_person_signals;");
        return Task.FromResult(count);
    }

    // ── Private intermediate row type and mapper ──────────────────────────────

    /// <summary>
    /// Intermediate row type for Dapper column-name mapping.
    /// Column names use underscores; model properties use PascalCase.
    /// </summary>
    private sealed class PendingSignalRow
    {
        public Guid id { get; set; }
        public Guid entity_id { get; set; }
        public string name { get; set; } = string.Empty;
        public string role { get; set; } = string.Empty;
        public string source { get; set; } = string.Empty;
        public string? pattern { get; set; }
        public string media_type { get; set; } = string.Empty;
        public string created_at { get; set; } = string.Empty;
    }

    private static PendingPersonSignal MapRow(PendingSignalRow r) => new()
    {
        Id = r.id,
        EntityId = r.entity_id,
        Name = r.name,
        Role = r.role,
        Source = r.source,
        Pattern = r.pattern,
        MediaType = r.media_type,
        CreatedAt = r.created_at,
    };
}
