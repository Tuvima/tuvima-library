using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite ledger of the organising job: one row per file the job looked at, keyed by operation and original path.</summary>
public sealed class FolderOrganiseLogRepository : IFolderOrganiseLogRepository
{
    private const string SelectSql = """
        SELECT operation_id AS OperationId,
               source_id    AS SourceId,
               old_path     AS OldPath,
               new_path     AS NewPath,
               outcome      AS Outcome,
               at           AS At,
               detail       AS Detail
        FROM folder_organise_log
        """;

    private readonly IDatabaseConnection _db;

    public FolderOrganiseLogRepository(IDatabaseConnection db) => _db = db;

    public Task UpsertAsync(FolderOrganiseLogEntry entry, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.OldPath);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO folder_organise_log (operation_id, source_id, old_path, new_path, outcome, at, detail)
            VALUES (@OperationId, @SourceId, @OldPath, @NewPath, @Outcome, @At, @Detail)
            ON CONFLICT(operation_id, old_path) DO UPDATE SET
                source_id = excluded.source_id,
                new_path  = excluded.new_path,
                outcome   = excluded.outcome,
                at        = excluded.at,
                detail    = excluded.detail;
            """,
            new
            {
                entry.OperationId,
                entry.SourceId,
                entry.OldPath,
                NewPath = entry.NewPath ?? string.Empty,
                entry.Outcome,
                At = entry.At.ToString("O"),
                entry.Detail,
            });
        return Task.CompletedTask;
    }

    public Task<FolderOrganiseLogEntry?> GetAsync(Guid operationId, string oldPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var row = conn.QueryFirstOrDefault<Row>(
            SelectSql + " WHERE operation_id = @operationId AND old_path = @oldPath LIMIT 1;",
            new { operationId, oldPath });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task DeleteAsync(Guid operationId, string oldPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute(
            "DELETE FROM folder_organise_log WHERE operation_id = @operationId AND old_path = @oldPath;",
            new { operationId, oldPath });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FolderOrganiseLogEntry>> ListUnfinishedAsync(Guid operationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<Row>(
            SelectSql + " WHERE operation_id = @operationId AND outcome = @moving ORDER BY at, old_path;",
            new { operationId, moving = FolderOrganiseOutcomes.Moving });
        return Task.FromResult<IReadOnlyList<FolderOrganiseLogEntry>>(rows.Select(Map).ToList());
    }

    public Task<HashSet<string>> GetFinishedPathsAsync(Guid operationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<PathPair>(
            "SELECT old_path AS OldPath, new_path AS NewPath FROM folder_organise_log WHERE operation_id = @operationId AND outcome <> @moving;",
            new { operationId, moving = FolderOrganiseOutcomes.Moving });
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in rows)
        {
            paths.Add(pair.OldPath);
            if (!string.IsNullOrEmpty(pair.NewPath))
            {
                paths.Add(pair.NewPath);
            }
        }

        return Task.FromResult(paths);
    }

    public Task<IReadOnlyList<FolderOrganiseLogEntry>> ListAsync(Guid operationId, int offset, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<Row>(
            SelectSql + " WHERE operation_id = @operationId ORDER BY at, old_path LIMIT @limit OFFSET @offset;",
            new { operationId, limit = Math.Clamp(limit, 1, 5000), offset = Math.Max(0, offset) });
        return Task.FromResult<IReadOnlyList<FolderOrganiseLogEntry>>(rows.Select(Map).ToList());
    }

    public Task<int> CountAsync(Guid operationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        return Task.FromResult(conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM folder_organise_log WHERE operation_id = @operationId;",
            new { operationId }));
    }

    public Task<IReadOnlyDictionary<string, int>> CountByOutcomeAsync(Guid operationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query(
            "SELECT outcome AS Outcome, COUNT(*) AS Count FROM folder_organise_log WHERE operation_id = @operationId GROUP BY outcome;",
            new { operationId });
        IReadOnlyDictionary<string, int> counts = rows.ToDictionary(
            row => (string)row.Outcome, row => (int)row.Count, StringComparer.Ordinal);
        return Task.FromResult(counts);
    }

    private static FolderOrganiseLogEntry Map(Row row) => new(
        row.OperationId,
        row.SourceId,
        row.OldPath,
        row.NewPath,
        row.Outcome,
        DateTimeOffset.TryParse(row.At, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : DateTimeOffset.MinValue,
        row.Detail);

    private sealed class PathPair
    {
        public string OldPath { get; set; } = string.Empty;
        public string NewPath { get; set; } = string.Empty;
    }

    private sealed class Row
    {
        public Guid OperationId { get; set; }
        public Guid SourceId { get; set; }
        public string OldPath { get; set; } = string.Empty;
        public string NewPath { get; set; } = string.Empty;
        public string Outcome { get; set; } = string.Empty;
        public string At { get; set; } = string.Empty;
        public string? Detail { get; set; }
    }
}
