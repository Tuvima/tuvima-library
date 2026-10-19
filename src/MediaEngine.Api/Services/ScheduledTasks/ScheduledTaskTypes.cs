namespace MediaEngine.Api.Services.ScheduledTasks;

/// <summary>How a scheduled task's last run ended.</summary>
public enum ScheduledTaskOutcome
{
    Done,
    Failed,
}

/// <summary>
/// What a recurring task is, in the words the Tools and requirements page shows.
/// <see cref="Schedule"/> is plain words ("Every day at 3:00"), not a cron expression.
/// </summary>
public sealed record ScheduledTaskDefinition(string Id, string Name, string Description, string Schedule);

/// <summary>One finished run of a task. <see cref="Reason"/> is a short sentence, set only when the run failed.</summary>
public sealed record ScheduledTaskRun(DateTimeOffset At, ScheduledTaskOutcome Outcome, string? Reason);

/// <summary>A task, its last run (if one is known) and when it runs next (if the task knows).</summary>
public sealed record ScheduledTaskSnapshot(
    ScheduledTaskDefinition Definition,
    ScheduledTaskRun? LastRun,
    DateTimeOffset? NextRun);

/// <summary>
/// A source of past runs that outlives the Engine process. Nothing implements it yet: the registry only knows what
/// happened since the Engine started. When durable run records exist (Developer tools, work packet 3) they register
/// here and the registry shows the newest of the two, so the Scheduled tasks list needs no change.
/// </summary>
public interface IScheduledTaskRunHistory
{
    /// <summary>The most recent recorded run of the task, or <see langword="null"/> when none is recorded.</summary>
    ScheduledTaskRun? GetLastRun(string taskId);
}

/// <summary>
/// Every recurring Engine task reports here so one list can show them all. Reports are in memory and cheap; a task
/// that has not run since the Engine started simply has no last run.
/// </summary>
public interface IScheduledTaskRegistry
{
    /// <summary>When the Engine started: the moment "since the Engine started" is measured from.</summary>
    DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Adds the task, or refreshes its words if it is already known (the schedule can change when settings change).
    /// Returns the handle the task uses to report each run.
    /// </summary>
    ScheduledTaskHandle Register(ScheduledTaskDefinition definition);

    /// <summary>Records how a run ended.</summary>
    void RecordRun(string taskId, ScheduledTaskOutcome outcome, string? reason = null);

    /// <summary>Records when the task runs next, or <see langword="null"/> when it does not know.</summary>
    void SetNextRun(string taskId, DateTimeOffset? nextRun);

    /// <summary>Every known task, ordered by name.</summary>
    IReadOnlyList<ScheduledTaskSnapshot> GetSnapshot();
}

/// <summary>The small object a task keeps to report its runs without repeating its own id.</summary>
public sealed class ScheduledTaskHandle
{
    private readonly IScheduledTaskRegistry _registry;
    private readonly string _taskId;

    public ScheduledTaskHandle(IScheduledTaskRegistry registry, string taskId)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        _registry = registry;
        _taskId = taskId;
    }

    /// <summary>The run finished.</summary>
    public void Done() => _registry.RecordRun(_taskId, ScheduledTaskOutcome.Done);

    /// <summary>The run failed; <paramref name="exception"/> is turned into one short sentence.</summary>
    public void Failed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _registry.RecordRun(_taskId, ScheduledTaskOutcome.Failed, ScheduledTaskReason.From(exception));
    }

    /// <summary>The task runs next at <paramref name="nextRun"/>.</summary>
    public void NextRunAt(DateTimeOffset? nextRun) => _registry.SetNextRun(_taskId, nextRun);

    /// <summary>The task runs next after <paramref name="delay"/> from now.</summary>
    public void NextRunIn(TimeSpan delay) => _registry.SetNextRun(_taskId, DateTimeOffset.Now.Add(delay));
}

/// <summary>Turns a failure into the short reason shown beside "Failed".</summary>
internal static class ScheduledTaskReason
{
    private const int MaxLength = 140;

    public static string From(Exception exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message.Trim();
        var firstLine = message.Split('\n', 2)[0].Trim();
        return firstLine.Length <= MaxLength ? firstLine : firstLine[..(MaxLength - 1)] + "…";
    }
}
