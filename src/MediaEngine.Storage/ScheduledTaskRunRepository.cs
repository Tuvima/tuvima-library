using System.Globalization;
using Dapper;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IScheduledTaskRunRepository"/>, backed by <c>scheduled_task_runs</c>.</summary>
public sealed class ScheduledTaskRunRepository : IScheduledTaskRunRepository
{
    private const string Columns =
        "id AS Id, task_key AS TaskKey, plugin_id AS PluginId, started_at AS StartedAt, finished_at AS FinishedAt, "
        + "outcome AS Outcome, items_processed AS ItemsProcessed, message AS Message";

    private readonly IDatabaseConnection _db;

    public ScheduledTaskRunRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<Guid> StartAsync(string taskKey, string? pluginId, DateTimeOffset startedAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);

        var id = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO scheduled_task_runs (id, task_key, plugin_id, started_at, outcome)
            VALUES (@id, @taskKey, @pluginId, @startedAt, @outcome);
            """,
            new { id, taskKey, pluginId, startedAt = Format(startedAt), outcome = ScheduledTaskOutcomes.Running });
        return Task.FromResult(id);
    }

    public Task FinishAsync(
        Guid runId,
        string outcome,
        DateTimeOffset finishedAt,
        int? itemsProcessed,
        string? message,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        using var conn = _db.CreateConnection();
        var taskKey = conn.ExecuteScalar<string?>("""
            UPDATE scheduled_task_runs
            SET outcome = @outcome, finished_at = @finishedAt, items_processed = @itemsProcessed, message = @message
            WHERE id = @runId
            RETURNING task_key;
            """,
            new { runId, outcome, finishedAt = Format(finishedAt), itemsProcessed, message });
        if (taskKey is not null)
        {
            Prune(conn, taskKey, finishedAt);
        }

        return Task.CompletedTask;
    }

    public Task<ScheduledTaskRun?> GetAsync(Guid runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var row = conn.QuerySingleOrDefault<Row>(
            $"SELECT {Columns} FROM scheduled_task_runs WHERE id = @runId;", new { runId });
        return Task.FromResult(row?.ToRun());
    }

    public Task<IReadOnlyDictionary<string, ScheduledTaskRun>> GetLatestPerTaskAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<Row>($"""
            SELECT {Columns}
            FROM scheduled_task_runs r
            WHERE r.id = (
                SELECT l.id FROM scheduled_task_runs l
                WHERE l.task_key = r.task_key
                ORDER BY l.started_at DESC, l.rowid DESC
                LIMIT 1);
            """);
        IReadOnlyDictionary<string, ScheduledTaskRun> latest = rows
            .Select(row => row.ToRun())
            .ToDictionary(run => run.TaskKey, StringComparer.Ordinal);
        return Task.FromResult(latest);
    }

    public Task<IReadOnlyList<ScheduledTaskRun>> ListRecentAsync(
        string taskKey,
        int limit,
        int skip = 0,
        string? pluginId = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var rows = conn.Query<Row>($"""
            SELECT {Columns}
            FROM scheduled_task_runs
            WHERE task_key = @taskKey AND (@pluginId IS NULL OR plugin_id = @pluginId)
            ORDER BY started_at DESC, rowid DESC
            LIMIT @limit OFFSET @skip;
            """,
            new { taskKey, pluginId, limit = Math.Max(limit, 0), skip = Math.Max(skip, 0) });
        return Task.FromResult<IReadOnlyList<ScheduledTaskRun>>(rows.Select(row => row.ToRun()).ToList());
    }

    public Task<int> MarkRunningAsInterruptedAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var changed = conn.Execute("""
            UPDATE scheduled_task_runs
            SET outcome = @interrupted, finished_at = @now, message = 'The Engine stopped before this run finished.'
            WHERE outcome = @running;
            """,
            new { interrupted = ScheduledTaskOutcomes.Interrupted, running = ScheduledTaskOutcomes.Running, now = Format(now) });
        return Task.FromResult(changed);
    }

    public Task PruneAsync(string taskKey, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        Prune(conn, taskKey, now);
        return Task.CompletedTask;
    }

    private static void Prune(SqliteConnection conn, string taskKey, DateTimeOffset now)
    {
        conn.Execute("""
            DELETE FROM scheduled_task_runs
            WHERE task_key = @taskKey AND outcome <> @running AND started_at < @cutoff;
            """,
            new
            {
                taskKey,
                running = ScheduledTaskOutcomes.Running,
                cutoff = Format(now.AddDays(-IScheduledTaskRunRepository.RetentionDays)),
            });
        conn.Execute("""
            DELETE FROM scheduled_task_runs
            WHERE task_key = @taskKey AND outcome <> @running AND id NOT IN (
                SELECT id FROM scheduled_task_runs
                WHERE task_key = @taskKey
                ORDER BY started_at DESC, rowid DESC
                LIMIT @keep);
            """,
            new { taskKey, running = ScheduledTaskOutcomes.Running, keep = IScheduledTaskRunRepository.MaxRunsPerTask });
    }

    private static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);

    private sealed class Row
    {
        public Guid Id { get; set; }
        public string TaskKey { get; set; } = "";
        public string? PluginId { get; set; }
        public string StartedAt { get; set; } = "";
        public string? FinishedAt { get; set; }
        public string Outcome { get; set; } = "";
        public int? ItemsProcessed { get; set; }
        public string? Message { get; set; }

        public ScheduledTaskRun ToRun() => new(
            Id,
            TaskKey,
            PluginId,
            Parse(StartedAt),
            FinishedAt is null ? null : Parse(FinishedAt),
            Outcome,
            ItemsProcessed,
            Message);
    }
}
