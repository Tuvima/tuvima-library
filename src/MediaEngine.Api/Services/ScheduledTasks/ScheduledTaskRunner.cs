using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ScheduledTasks;

/// <summary>
/// Wraps one run of a recurring task with a durable run record: started, then finished with an outcome. An exception is
/// recorded as a failure with its message and then rethrown, so the calling loop logs and retries exactly as before.
/// A problem writing the record is logged and never stops the task itself.
/// </summary>
public sealed class ScheduledTaskRunner
{
    private const int MaxMessageLength = 1000;

    private readonly IScheduledTaskRunRepository _runs;
    private readonly ILogger<ScheduledTaskRunner> _logger;

    public ScheduledTaskRunner(IScheduledTaskRunRepository runs, ILogger<ScheduledTaskRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(logger);

        _runs = runs;
        _logger = logger;
    }

    /// <summary>
    /// Records and runs <paramref name="body"/>. Returns the finished run (as stored, or as it would have been stored if the
    /// record could not be written). Shutdown during the run is recorded as interrupted and rethrown.
    /// </summary>
    public async Task<ScheduledTaskRun> RunAsync(
        string taskKey,
        Func<CancellationToken, Task<TaskRunResult>> body,
        CancellationToken ct,
        string? pluginId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskKey);
        ArgumentNullException.ThrowIfNull(body);

        var startedAt = DateTimeOffset.UtcNow;
        var runId = await TryStartAsync(taskKey, pluginId, startedAt).ConfigureAwait(false);

        TaskRunResult result;
        try
        {
            result = await body(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryFinishAsync(runId, taskKey, ScheduledTaskOutcomes.Interrupted, null, "Stopped because the Engine was shutting down.")
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await TryFinishAsync(runId, taskKey, ScheduledTaskOutcomes.Failed, null, ex.Message).ConfigureAwait(false);
            throw;
        }

        var finishedAt = await TryFinishAsync(runId, taskKey, result.Outcome, result.ItemsProcessed, result.Message)
            .ConfigureAwait(false);
        return new ScheduledTaskRun(
            runId ?? Guid.NewGuid(),
            taskKey,
            pluginId,
            startedAt,
            finishedAt,
            result.Outcome,
            result.ItemsProcessed,
            Trim(result.Message));
    }

    private async Task<Guid?> TryStartAsync(string taskKey, string? pluginId, DateTimeOffset startedAt)
    {
        try
        {
            return await _runs.StartAsync(taskKey, pluginId, startedAt, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record the start of scheduled task {TaskKey}", taskKey);
            return null;
        }
    }

    private async Task<DateTimeOffset> TryFinishAsync(
        Guid? runId, string taskKey, string outcome, int? itemsProcessed, string? message)
    {
        var finishedAt = DateTimeOffset.UtcNow;
        if (runId is null)
        {
            return finishedAt;
        }

        try
        {
            await _runs.FinishAsync(runId.Value, outcome, finishedAt, itemsProcessed, Trim(message), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record the end of scheduled task {TaskKey}", taskKey);
        }

        return finishedAt;
    }

    private static string? Trim(string? message) =>
        message is { Length: > MaxMessageLength } ? message[..MaxMessageLength] : message;
}
