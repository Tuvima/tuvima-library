using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="INarrativeRootRepository"/>.
/// Uses Dapper for type-safe column-to-property mapping.
///
/// Narrative roots track the fictional universe hierarchy (Universe → Franchise → Series).
/// The QID is the primary key — upserts update label, level, and parent on conflict.
/// </summary>
public sealed class NarrativeRootRepository : INarrativeRootRepository
{
    private readonly IDatabaseConnection _db;

    private const string SelectColumns = """
        roots.qid        AS Qid,
        COALESCE(overrides.label, roots.label) AS Label,
        overrides.description AS Description,
        roots.level      AS Level,
        roots.parent_qid AS ParentQid,
        roots.created_at AS CreatedAt
        """;

    public NarrativeRootRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public Task<NarrativeRoot?> FindByQidAsync(string qid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(qid);

        using var conn = _db.CreateConnection();
        var result = conn.QueryFirstOrDefault<NarrativeRoot>($"""
            SELECT {SelectColumns}
            FROM   narrative_roots roots
            LEFT JOIN narrative_root_user_overrides overrides ON overrides.qid = roots.qid
            WHERE  roots.qid = @qid
            LIMIT  1;
            """, new { qid });

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<NarrativeRoot>> ListAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var result = conn.Query<NarrativeRoot>($"""
            SELECT {SelectColumns}
            FROM   narrative_roots roots
            LEFT JOIN narrative_root_user_overrides overrides ON overrides.qid = roots.qid
            ORDER BY roots.level, COALESCE(overrides.label, roots.label);
            """).AsList();

        return Task.FromResult<IReadOnlyList<NarrativeRoot>>(result);
    }

    /// <inheritdoc/>
    public Task UpsertAsync(NarrativeRoot root, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(root);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO narrative_roots (qid, label, level, parent_qid, created_at)
            VALUES (@qid, @label, @level, @parentQid, @createdAt)
            ON CONFLICT(qid) DO UPDATE SET
                label = excluded.label,
                level = excluded.level,
                parent_qid = COALESCE(excluded.parent_qid, narrative_roots.parent_qid);
            """,
            new
            {
                qid = root.Qid,
                label = root.Label,
                level = root.Level,
                parentQid = root.ParentQid,
                createdAt = root.CreatedAt.ToString("o"),
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task EnsureAsync(NarrativeRoot root, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(root);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO narrative_roots (qid, label, level, parent_qid, created_at)
            VALUES (@qid, @label, @level, @parentQid, @createdAt)
            ON CONFLICT(qid) DO NOTHING;
            """,
            new
            {
                qid = root.Qid,
                label = root.Label,
                level = root.Level,
                parentQid = root.ParentQid,
                createdAt = root.CreatedAt.ToString("o"),
            });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpdateUserDetailsAsync(string qid, string label, string? description, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(qid);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            conn.Execute("""
                INSERT INTO narrative_root_user_overrides (qid, label, description, updated_at)
                VALUES (@qid, @label, @description, datetime('now'))
                ON CONFLICT(qid) DO UPDATE SET
                    label = excluded.label,
                    description = excluded.description,
                    updated_at = datetime('now');
                """, new { qid, label = label.Trim(), description }, tx);
        }, ct);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Guid>> FindWorkIdsByProvenanceQidAsync(string qid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(qid);
        using var conn = _db.CreateConnection();
        var ids = conn.Query<Guid>("""
            SELECT DISTINCT w.id
            FROM works w
            INNER JOIN canonical_values provenance ON provenance.entity_id = w.id
            WHERE provenance.key IN ('narrative_root_qid', 'fictional_universe_qid')
              AND provenance.value = @qid COLLATE NOCASE;
            """, new { qid }).AsList();
        return Task.FromResult<IReadOnlyList<Guid>>(ids);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<NarrativeRoot>> GetChildrenAsync(
        string parentQid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(parentQid);

        using var conn = _db.CreateConnection();
        var result = conn.Query<NarrativeRoot>($"""
            SELECT {SelectColumns}
            FROM   narrative_roots roots
            LEFT JOIN narrative_root_user_overrides overrides ON overrides.qid = roots.qid
            WHERE  roots.parent_qid = @parentQid
            ORDER BY roots.level, COALESCE(overrides.label, roots.label);
            """, new { parentQid }).AsList();

        return Task.FromResult<IReadOnlyList<NarrativeRoot>>(result);
    }

    /// <inheritdoc/>
    public Task<int> CountAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM narrative_roots;");
        return Task.FromResult(count);
    }
}
