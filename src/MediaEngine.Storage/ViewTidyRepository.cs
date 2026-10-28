using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>Stores Tidy this folder runs and their rename ledger (Photo and video filing spec D17).</summary>
public sealed class ViewTidyRepository(IDatabaseConnection database) : IViewTidyRepository
{
    private const string JobColumns = """
        id AS Id, source_id AS SourceId, trigger_kind AS TriggerKind, state AS State, total_files AS TotalFiles,
        moved_files AS MovedFiles, failed_files AS FailedFiles, message AS Message, created_at AS CreatedAt, finished_at AS FinishedAt
        """;

    public Task CreateJobAsync(ViewTidyJob job, IReadOnlyList<ViewTidyMove> moves, Guid? startedBy, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(new CommandDefinition("""
                INSERT INTO view_tidy_jobs (id, source_id, trigger_kind, state, total_files, moved_files, failed_files, message, started_by, created_at)
                VALUES (@Id, @SourceId, @TriggerKind, @State, @TotalFiles, 0, 0, NULL, @startedBy, @createdAt);
                """, new { job.Id, job.SourceId, job.TriggerKind, job.State, job.TotalFiles, startedBy, createdAt = job.CreatedAt.ToUniversalTime().ToString("o") },
                transaction, cancellationToken: token));
            foreach (var move in moves)
            {
                connection.Execute(new CommandDefinition("""
                    INSERT INTO view_tidy_moves (job_id, source_file_id, from_path, to_path, state) VALUES (@JobId, @SourceFileId, @FromPath, @ToPath, 'planned');
                    """, move, transaction, cancellationToken: token));
            }

            return 0;
        }, ct);

    public Task<IReadOnlyList<ViewTidyJob>> GetRunningJobsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<JobRow>(new CommandDefinition(
            $"SELECT {JobColumns} FROM view_tidy_jobs WHERE state = 'running' ORDER BY created_at, id;", cancellationToken: ct)).Select(ToJob).ToList();
        return Task.FromResult<IReadOnlyList<ViewTidyJob>>(rows);
    }

    public Task<ViewTidyJob?> GetLatestJobAsync(Guid sourceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<JobRow>(new CommandDefinition(
            $"SELECT {JobColumns} FROM view_tidy_jobs WHERE source_id = @sourceId ORDER BY created_at DESC, id LIMIT 1;", new { sourceId }, cancellationToken: ct));
        return Task.FromResult(row is null ? null : ToJob(row));
    }

    public Task<IReadOnlyList<ViewTidyMove>> GetPlannedMovesAsync(Guid jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<MoveRow>(new CommandDefinition("""
            SELECT job_id AS JobId, source_file_id AS SourceFileId, from_path AS FromPath, to_path AS ToPath, state AS State
              FROM view_tidy_moves WHERE job_id = @jobId AND state = 'planned' ORDER BY from_path;
            """, new { jobId }, cancellationToken: ct)).Select(row => new ViewTidyMove(row.JobId, row.SourceFileId, row.FromPath, row.ToPath, row.State)).ToList();
        return Task.FromResult<IReadOnlyList<ViewTidyMove>>(rows);
    }

    public Task CompleteMoveAsync(Guid jobId, Guid sourceFileId, string toPath, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            // If the folder watcher already noticed the file at its new place, that row stands and the old one goes.
            connection.Execute(new CommandDefinition("""
                DELETE FROM local_file_sources
                 WHERE id = @sourceFileId
                   AND EXISTS (SELECT 1 FROM local_file_sources other
                                WHERE other.library_id = local_file_sources.library_id AND other.id <> local_file_sources.id
                                  AND other.file_path = @toPath COLLATE NOCASE);
                UPDATE local_file_sources SET file_path = @toPath WHERE id = @sourceFileId;
                UPDATE view_tidy_moves SET state = 'moved', reason = NULL WHERE job_id = @jobId AND source_file_id = @sourceFileId;
                UPDATE view_tidy_jobs SET moved_files = moved_files + 1 WHERE id = @jobId;
                """, new { jobId, sourceFileId, toPath }, transaction, cancellationToken: token));
            return 0;
        }, ct);

    public Task FailMoveAsync(Guid jobId, Guid sourceFileId, string reason, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(new CommandDefinition("""
                UPDATE view_tidy_moves SET state = 'failed', reason = @reason WHERE job_id = @jobId AND source_file_id = @sourceFileId;
                UPDATE view_tidy_jobs SET failed_files = failed_files + 1 WHERE id = @jobId;
                """, new { jobId, sourceFileId, reason }, transaction, cancellationToken: token));
            return 0;
        }, ct);

    public Task FinishJobAsync(Guid jobId, string state, string message, DateTimeOffset now, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(new CommandDefinition("""
                UPDATE view_tidy_jobs SET state = @state, message = @message, finished_at = @now WHERE id = @jobId;
                """, new { jobId, state, message, now = now.ToUniversalTime().ToString("o") }, transaction, cancellationToken: token));
            return 0;
        }, ct);

    public Task<bool> IsLibraryBeingTidiedAsync(Guid libraryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var running = connection.ExecuteScalar<long>(new CommandDefinition("""
            SELECT COUNT(1) FROM view_tidy_jobs j JOIN view_sources s ON s.id = j.source_id
             WHERE j.state = 'running' AND s.library_id = @libraryId;
            """, new { libraryId }, cancellationToken: ct));
        return Task.FromResult(running > 0);
    }

    public Task<IReadOnlyList<ViewTidySourceFile>> GetSourceFilesAsync(Guid sourceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<SourceFileRow>(new CommandDefinition("""
            SELECT lfs.id AS SourceFileId, lfs.file_path AS FilePath,
                   MIN(CASE WHEN li.captured_at_user_override = 1 THEN li.captured_at ELSE li.embedded_captured_at END) AS TakenAt
              FROM local_file_sources lfs
              LEFT JOIN local_item_files lif ON lif.file_id = lfs.file_id
              LEFT JOIN local_items li ON li.id = lif.item_id
             WHERE lfs.source_id = @sourceId
             GROUP BY lfs.id, lfs.file_path
             ORDER BY lfs.file_path;
            """, new { sourceId }, cancellationToken: ct)).Select(row => new ViewTidySourceFile(row.SourceFileId, row.FilePath, row.TakenAt)).ToList();
        return Task.FromResult<IReadOnlyList<ViewTidySourceFile>>(rows);
    }

    public Task SetScheduledAsync(Guid sourceId, bool scheduled, DateTimeOffset now, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(new CommandDefinition(
                scheduled
                    ? "INSERT OR IGNORE INTO view_tidy_schedules (source_id, created_at) VALUES (@sourceId, @now);"
                    : "DELETE FROM view_tidy_schedules WHERE source_id = @sourceId;",
                new { sourceId, now = now.ToUniversalTime().ToString("o") }, transaction, cancellationToken: token));
            return 0;
        }, ct);

    public Task<IReadOnlyList<Guid>> GetScheduledSourceIdsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<Guid>(new CommandDefinition("SELECT source_id FROM view_tidy_schedules ORDER BY created_at;", cancellationToken: ct)).ToList();
        return Task.FromResult<IReadOnlyList<Guid>>(rows);
    }

    public Task<bool> IsScheduledAsync(Guid sourceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return Task.FromResult(connection.ExecuteScalar<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM view_tidy_schedules WHERE source_id = @sourceId;", new { sourceId }, cancellationToken: ct)) > 0);
    }

    private static ViewTidyJob ToJob(JobRow row) => new(row.Id, row.SourceId, row.TriggerKind, row.State, row.TotalFiles, row.MovedFiles,
        row.FailedFiles, row.Message, row.CreatedAt, row.FinishedAt);

    private sealed class JobRow
    {
        public Guid Id { get; init; }
        public Guid SourceId { get; init; }
        public string TriggerKind { get; init; } = "";
        public string State { get; init; } = "";
        public long TotalFiles { get; init; }
        public long MovedFiles { get; init; }
        public long FailedFiles { get; init; }
        public string? Message { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? FinishedAt { get; init; }
    }

    private sealed class MoveRow
    {
        public Guid JobId { get; init; }
        public Guid SourceFileId { get; init; }
        public string FromPath { get; init; } = "";
        public string ToPath { get; init; } = "";
        public string State { get; init; } = "";
    }

    private sealed class SourceFileRow
    {
        public Guid SourceFileId { get; init; }
        public string FilePath { get; init; } = "";
        public string? TakenAt { get; init; }
    }
}
