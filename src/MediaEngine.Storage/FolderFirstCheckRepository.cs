using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite state of the first check of a newly added folder (<c>folder_first_checks</c>, <c>folder_first_check_files</c>; Library setup spec
/// D35, D61). Files are matched to a folder by path: a file is in the folder when its path, with every separator as <c>/</c> and ASCII
/// letters lower-cased, starts with the folder's <c>path_key</c> and a <c>/</c>. Every call uses a short-lived connection; times are stored
/// as UTC ISO-8601 text.
/// </summary>
public sealed class FolderFirstCheckRepository(IDatabaseConnection db, FolderFirstCheckSignal? signal = null) : IFolderFirstCheckRepository
{
    /// <summary>The held states: nothing in the folder is renamed, moved or tagged, and a paused folder claims no identity work.</summary>
    internal const string HeldStatesSql = "('checking', 'paused')";

    // The statuses of a file's intake that are still going (the file is not yet registered, or its lookup not yet queued).
    private const string OpenIntakeStatuses = "('pending', 'queued', 'leased', 'running', 'retry_waiting')";

    // The identity job states from which a file has not yet reached a retail outcome.
    private const string PreOutcomeStates = "('Queued', 'RetailSearching')";

    /// <summary>
    /// SQL that is true when the file at <paramref name="assetAlias"/> (a <c>media_assets</c> row) is in the folder of the check at
    /// <paramref name="checkAlias"/> (a <c>folder_first_checks</c> row).
    /// </summary>
    internal static string FileInFolder(string assetAlias, string checkAlias) =>
        PathInFolder($"{assetAlias}.file_path_root", checkAlias);

    /// <summary>SQL that is true when the path held by the expression <paramref name="pathExpression"/> is in the folder of the check at <paramref name="checkAlias"/>.</summary>
    internal static string PathInFolder(string pathExpression, string checkAlias) =>
        $"substr(lower(replace({pathExpression}, '\\', '/')), 1, length({checkAlias}.path_key) + 1) = {checkAlias}.path_key || '/'";

    /// <summary>
    /// SQL that is true when the file at <paramref name="assetAlias"/> is in a folder whose files must not be touched: a check that is
    /// checking or paused covers it.
    /// </summary>
    internal static string FileInHeldFolder(string assetAlias) => $"""
        EXISTS (SELECT 1 FROM folder_first_checks held_check
                WHERE held_check.state IN {HeldStatesSql} AND {FileInFolder(assetAlias, "held_check")})
        """;

    public Task<bool> StartAsync(string libraryId, string sourceId, string path, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var started = connection.Execute("""
            INSERT OR IGNORE INTO folder_first_checks (source_id, library_id, path, path_key, state, tried, unmatched, started_at)
            VALUES (@sourceId, @libraryId, @path, @pathKey, 'checking', 0, 0, @now);
            """,
            new { sourceId, libraryId, path, pathKey = FolderFirstCheckPaths.Key(path), now = Format(now) }) > 0;
        return Task.FromResult(started);
    }

    public Task<FolderFirstCheck?> GetAsync(string sourceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var row = connection.QuerySingleOrDefault<Row>($"{SelectSql} WHERE source_id = @sourceId;", new { sourceId });
        return Task.FromResult(row?.ToModel());
    }

    public Task<IReadOnlyList<FolderFirstCheck>> ListCheckingAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var rows = connection.Query<Row>($"{SelectSql} WHERE state = 'checking' ORDER BY started_at, source_id;");
        return Task.FromResult<IReadOnlyList<FolderFirstCheck>>(rows.Select(row => row.ToModel()).ToList());
    }

    public Task<IReadOnlyList<FolderFirstCheckSnapshot>> ListOpenAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var rows = connection.Query<Row>($"{SelectSql} WHERE state IN {HeldStatesSql} ORDER BY started_at, source_id;").ToList();
        var result = new List<FolderFirstCheckSnapshot>(rows.Count);
        foreach (var row in rows)
        {
            var check = row.ToModel();
            if (check.State == FolderFirstCheckStates.Checking)
            {
                // The counts of a check that is still running are the live ones.
                var live = connection.QuerySingle<(int Tried, int Unmatched)>("""
                    SELECT COUNT(*) AS Tried, COALESCE(SUM(CASE WHEN outcome = 'unmatched' THEN 1 ELSE 0 END), 0) AS Unmatched
                    FROM folder_first_check_files WHERE source_id = @sourceId;
                    """, new { sourceId = check.SourceId });
                check = check with { Tried = live.Tried, Unmatched = live.Unmatched };
            }

            result.Add(new FolderFirstCheckSnapshot(check, CountFiles(connection, check.SourceId)));
        }

        return Task.FromResult<IReadOnlyList<FolderFirstCheckSnapshot>>(result);
    }

    public Task<FolderFirstCheckProgress> GetProgressAsync(string sourceId, int firstFiles, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        var counted = connection.QuerySingle<(int Tried, int Unmatched)>("""
            SELECT COUNT(*) AS Tried, COALESCE(SUM(CASE WHEN outcome = 'unmatched' THEN 1 ELSE 0 END), 0) AS Unmatched
            FROM (SELECT outcome FROM folder_first_check_files WHERE source_id = @sourceId ORDER BY seq LIMIT @firstFiles);
            """, new { sourceId, firstFiles = Math.Max(1, firstFiles) });

        var waiting = connection.ExecuteScalar<int>($"""
            SELECT COUNT(*) FROM media_assets a
            JOIN folder_first_checks c ON c.source_id = @sourceId
            WHERE {FileInFolder("a", "c")}
              AND NOT EXISTS (SELECT 1 FROM folder_first_check_files f WHERE f.source_id = c.source_id AND f.asset_id = a.id)
              AND EXISTS (SELECT 1 FROM identity_jobs j WHERE j.entity_id = a.id AND j.state IN {PreOutcomeStates});
            """, new { sourceId });

        var settled = !connection.ExecuteScalar<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM media_operations o
                JOIN folder_first_checks c ON c.source_id = @sourceId
                WHERE o.operation_type = 'ingestion.file' AND o.status IN {OpenIntakeStatuses}
                  AND {PathInFolder("o.source_path", "c")});
            """, new { sourceId });

        return Task.FromResult(new FolderFirstCheckProgress(counted.Tried, counted.Unmatched, waiting, settled, CountFiles(connection, sourceId)));
    }

    public Task RecordOutcomeAsync(Guid assetId, string outcome, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        if (RecordOutcome(connection, assetId, outcome))
        {
            signal?.Signal();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Records the outcome of the file <paramref name="entityId"/> (or of every file of the work it names) in the checking folder it is in.
    /// Returns true when an outcome was recorded. The same SQL serves the identity job state change, which only has the job's entity.
    /// </summary>
    internal static bool RecordOutcome(SqliteConnection connection, Guid entityId, string outcome)
    {
        // Nearly always nothing is being checked: skip the lookup of the file then.
        if (!connection.ExecuteScalar<bool>("SELECT EXISTS (SELECT 1 FROM folder_first_checks WHERE state = 'checking');"))
        {
            return false;
        }

        return connection.Execute($"""
            INSERT INTO folder_first_check_files (source_id, asset_id, outcome, recorded_at)
            SELECT m.source_id, m.id, @outcome, @now
            FROM (
                SELECT a.id AS id,
                       (SELECT c.source_id FROM folder_first_checks c
                        WHERE c.state = 'checking' AND {FileInFolder("a", "c")}
                        ORDER BY length(c.path_key) DESC LIMIT 1) AS source_id
                FROM (SELECT id, file_path_root FROM media_assets WHERE id = @entityId
                      UNION
                      SELECT ma.id, ma.file_path_root FROM media_assets ma
                      JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @entityId) a) m
            WHERE m.source_id IS NOT NULL
            ON CONFLICT (source_id, asset_id) DO UPDATE SET outcome = excluded.outcome;
            """,
            new { entityId, outcome, now = Format(DateTimeOffset.UtcNow) }) > 0;
    }

    public Task<bool> PassAsync(string sourceId, int tried, int unmatched, DateTimeOffset now, CancellationToken ct = default) =>
        EndAsync(sourceId, FolderFirstCheckStates.Checking, FolderFirstCheckStates.Passed, tried, unmatched, now, ct);

    public Task<bool> PauseAsync(string sourceId, int tried, int unmatched, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = db.CreateConnection();
        // Paused is not the end of the check: its counts are kept and nothing is released, so the file rows stay for a reader.
        return Task.FromResult(connection.Execute("""
            UPDATE folder_first_checks
            SET state = 'paused', tried = @tried, unmatched = @unmatched
            WHERE source_id = @sourceId AND state = 'checking';
            """, new { sourceId, tried, unmatched }) > 0);
    }

    public Task<bool> KeepGoingAsync(string sourceId, DateTimeOffset now, CancellationToken ct = default) =>
        EndAsync(sourceId, FolderFirstCheckStates.Paused, FolderFirstCheckStates.KeptGoing, tried: null, unmatched: null, now, ct);

    public Task<bool> DropAsync(string sourceId, CancellationToken ct = default) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            // The folder's held filing rows go with the check; nothing is moved. A row that waits for the next run is a different thing
            // (the check had already passed) and is left alone.
            connection.Execute($"""
                DELETE FROM media_file_write_intents
                WHERE target_kind = 'media_asset' AND move_step = 'held' AND move_origin = 'filing'
                  AND asset_id IN (SELECT a.id FROM media_assets a JOIN folder_first_checks c ON c.source_id = @sourceId
                                   WHERE {FileInFolder("a", "c")});
                """, new { sourceId }, transaction);
            return connection.Execute("DELETE FROM folder_first_checks WHERE source_id = @sourceId;", new { sourceId }, transaction) > 0;
        }, ct);

    public bool IsHeld(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return false;
        }

        using var connection = db.CreateConnection();
        return connection.ExecuteScalar<bool>(
            $"SELECT EXISTS (SELECT 1 FROM folder_first_checks WHERE source_id = @sourceId AND state IN {HeldStatesSql});",
            new { sourceId });
    }

    // Ends the check in one transaction: the state changes only from the expected one, the folder's held filing rows become waiting (the
    // row of a file that is Unmatched now is dropped instead: it stays where it is) and the per-file rows are no longer needed.
    private Task<bool> EndAsync(
        string sourceId, string from, string to, int? tried, int? unmatched, DateTimeOffset now, CancellationToken ct) =>
        db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var changed = connection.Execute("""
                UPDATE folder_first_checks
                SET state = @to, tried = COALESCE(@tried, tried), unmatched = COALESCE(@unmatched, unmatched), ended_at = @now
                WHERE source_id = @sourceId AND state = @from;
                """, new { sourceId, from, to, tried, unmatched, now = Format(now) }, transaction) > 0;
            if (!changed)
            {
                return false;
            }

            // A file that is Unmatched now has no business being filed: its held row goes, and a later match asks for filing afresh.
            connection.Execute($"""
                DELETE FROM media_file_write_intents
                WHERE target_kind = 'media_asset' AND move_step = 'held' AND move_origin = 'filing'
                  AND asset_id IN (SELECT asset_id FROM unmatched_assets)
                  AND asset_id IN (SELECT a.id FROM media_assets a JOIN folder_first_checks c ON c.source_id = @sourceId
                                   WHERE {FileInFolder("a", "c")});
                """, new { sourceId }, transaction);
            connection.Execute($"""
                UPDATE media_file_write_intents
                SET move_step = 'waiting', updated_at = @now
                WHERE target_kind = 'media_asset' AND move_step = 'held' AND move_origin = 'filing'
                  AND asset_id IN (SELECT a.id FROM media_assets a JOIN folder_first_checks c ON c.source_id = @sourceId
                                   WHERE {FileInFolder("a", "c")});
                """, new { sourceId, now = Format(now) }, transaction);
            connection.Execute("DELETE FROM folder_first_check_files WHERE source_id = @sourceId;", new { sourceId }, transaction);
            return true;
        }, ct);

    private static int CountFiles(SqliteConnection connection, string sourceId) =>
        connection.ExecuteScalar<int>($"""
            SELECT COUNT(*) FROM media_assets a JOIN folder_first_checks c ON c.source_id = @sourceId
            WHERE a.is_orphaned = 0 AND {FileInFolder("a", "c")};
            """, new { sourceId });

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private const string SelectSql = """
        SELECT source_id AS SourceId, library_id AS LibraryId, path AS Path, state AS State, tried AS Tried,
               unmatched AS Unmatched, started_at AS StartedAt, ended_at AS EndedAt
        FROM folder_first_checks
        """;

    private sealed class Row
    {
        public string SourceId { get; set; } = string.Empty;
        public string LibraryId { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public int Tried { get; set; }
        public int Unmatched { get; set; }
        public string StartedAt { get; set; } = string.Empty;
        public string? EndedAt { get; set; }

        public FolderFirstCheck ToModel() => new(
            SourceId, LibraryId, Path, State, Tried, Unmatched,
            DateTimeOffset.Parse(StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            EndedAt is null ? null : DateTimeOffset.Parse(EndedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }
}
