namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Lets recurring work that lives outside the Engine project write a durable run record (started, finished, outcome)
/// for the Scheduled tasks list.
/// </summary>
public interface IRecurringTaskRecorder
{
    /// <summary>
    /// Runs <paramref name="run"/> under a run record for <paramref name="taskKey"/>. The function returns how many items
    /// it handled, or null. An exception is recorded as a failure and rethrown.
    /// </summary>
    Task RecordAsync(string taskKey, Func<CancellationToken, Task<int?>> run, CancellationToken ct);
}
