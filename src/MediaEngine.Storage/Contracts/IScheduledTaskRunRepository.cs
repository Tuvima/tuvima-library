namespace MediaEngine.Storage.Contracts;

/// <summary>How a scheduled task run ended. Stored as these exact lower-case words.</summary>
public static class ScheduledTaskOutcomes
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Interrupted = "interrupted";
}

/// <summary>One run of a recurring background task, as stored in <c>scheduled_task_runs</c>.</summary>
/// <param name="TaskKey">Snake_case task key, for example <c>vibe_tags</c>.</param>
/// <param name="PluginId">Set when the run belongs to a plugin.</param>
/// <param name="FinishedAt">Null while the run is going.</param>
/// <param name="Outcome">One of <see cref="ScheduledTaskOutcomes"/>.</param>
/// <param name="Message">Plain-English result, failure message or skip reason.</param>
public sealed record ScheduledTaskRun(
    Guid Id,
    string TaskKey,
    string? PluginId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Outcome,
    int? ItemsProcessed,
    string? Message);

/// <summary>
/// Durable history of recurring background work. A run is started, then finished; anything still running when the Engine
/// restarts is marked interrupted. Rows are pruned to 90 days and 500 per task each time a run finishes.
/// </summary>
public interface IScheduledTaskRunRepository
{
    /// <summary>Days of history kept per task.</summary>
    public const int RetentionDays = 90;

    /// <summary>Most rows kept per task.</summary>
    public const int MaxRunsPerTask = 500;

    /// <summary>Records that a run has begun and returns its id.</summary>
    Task<Guid> StartAsync(string taskKey, string? pluginId, DateTimeOffset startedAt, CancellationToken ct = default);

    /// <summary>Records how a run ended, then prunes that task's old rows.</summary>
    Task FinishAsync(
        Guid runId,
        string outcome,
        DateTimeOffset finishedAt,
        int? itemsProcessed,
        string? message,
        CancellationToken ct = default);

    /// <summary>A single run by id, or null when it has been pruned or never existed.</summary>
    Task<ScheduledTaskRun?> GetAsync(Guid runId, CancellationToken ct = default);

    /// <summary>The most recent run of each task, keyed by task key.</summary>
    Task<IReadOnlyDictionary<string, ScheduledTaskRun>> GetLatestPerTaskAsync(CancellationToken ct = default);

    /// <summary>The newest runs of a task first, optionally limited to one plugin, <paramref name="skip"/> rows in.</summary>
    Task<IReadOnlyList<ScheduledTaskRun>> ListRecentAsync(
        string taskKey,
        int limit,
        int skip = 0,
        string? pluginId = null,
        CancellationToken ct = default);

    /// <summary>Marks every run still marked running as interrupted. Called once at Engine startup; returns how many.</summary>
    Task<int> MarkRunningAsInterruptedAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Deletes a task's runs older than <see cref="RetentionDays"/> before <paramref name="now"/>, then everything past
    /// the newest <see cref="MaxRunsPerTask"/>. Runs that are still going are never deleted.
    /// </summary>
    Task PruneAsync(string taskKey, DateTimeOffset now, CancellationToken ct = default);
}
