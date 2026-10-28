using System.Collections.Concurrent;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Configuration;
using MediaEngine.Plugins;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Runs the recurring jobs plugins declare (<see cref="IPluginScheduledJob"/>) for plugins that are switched on, approved,
/// loaded and allowed <c>scheduled-work</c>. Every run is a durable run record through <see cref="ScheduledTaskRunner"/>
/// (task key <c>plugin:{pluginId}:{jobKey}</c>), so it is listed in Scheduled tasks and survives a restart. Limits, from
/// <see cref="PluginHostLimits"/>: a job never overlaps itself, at most two plugin jobs run at once across the Engine, a
/// run longer than ten minutes is cancelled and recorded as failed, and a job stopped that way three times in a row
/// pauses until its plugin is switched off and on. A plugin that declares itself heavy (Plugin architecture spec D11) waits while
/// ingestion is working and runs one at a time, so it never competes with the Library finishing its own work.
/// </summary>
internal sealed class PluginJobScheduler : BackgroundService, IScheduledTaskDescriptorSource
{
    private sealed class JobTiming
    {
        public int Generation { get; init; }
        public DateTimeOffset? NextDue { get; set; }
    }

    private readonly PluginCatalog _catalog;
    private readonly PluginHostStatusRegistry _statuses;
    private readonly IPluginExecutionContextFactory _contexts;
    private readonly ScheduledTaskRunner _runner;
    private readonly IScheduledTaskRunRepository _runs;
    private readonly TimeProvider _clock;
    private readonly PluginHostLimits _limits;
    private readonly ILogger<PluginJobScheduler> _logger;
    private readonly PluginActivityService? _activity;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _heavySlot = new(1, 1);
    private readonly IIngestionQueueProgressService? _ingestion;
    private readonly object _gate = new();
    private readonly Dictionary<string, JobTiming> _timings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly HashSet<string> _heavyRunning = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ignoredOverrides = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);

    public PluginJobScheduler(
        PluginCatalog catalog,
        PluginHostStatusRegistry statuses,
        IPluginExecutionContextFactory contexts,
        ScheduledTaskRunner runner,
        IScheduledTaskRunRepository runs,
        PluginHostLimits limits,
        TimeProvider clock,
        ILogger<PluginJobScheduler> logger,
        PluginActivityService? activity = null,
        IIngestionQueueProgressService? ingestion = null)
    {
        _activity = activity;
        _ingestion = ingestion;
        _catalog = catalog;
        _statuses = statuses;
        _contexts = contexts;
        _runner = runner;
        _runs = runs;
        _limits = limits;
        _clock = clock;
        _logger = logger;
        _slots = new SemaphoreSlim(limits.MaxConcurrentJobs, limits.MaxConcurrentJobs);
    }

    /// <summary>The run-record key of one plugin job.</summary>
    public static string TaskKey(string pluginId, string jobKey) => $"plugin:{pluginId}:{jobKey}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunDueJobsAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(_limits.JobPollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin job scheduling pass failed; trying again shortly");
                try
                {
                    await Task.Delay(_limits.JobPollInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        await WhenIdleAsync().ConfigureAwait(false);
    }

    /// <summary>Starts every job that is due now and has room to run. Returns once they are started, not when they finish.</summary>
    public async Task RunDueJobsAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        foreach (var registration in _catalog.List())
        {
            if (!IsEligible(registration))
            {
                continue;
            }

            var status = _statuses.Get(registration.Manifest.Id);
            foreach (var job in registration.Capabilities.OfType<IPluginScheduledJob>())
            {
                ct.ThrowIfCancellationRequested();
                await ConsiderAsync(registration, job, status, now, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Completes when every job run started so far has finished.</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_inFlight.Values);

    /// <inheritdoc />
    public IReadOnlyList<ScheduledTaskDescriptor> ListScheduledTasks()
    {
        var descriptors = new List<ScheduledTaskDescriptor>();
        foreach (var registration in _catalog.List().Where(IsEligible))
        {
            var pluginId = registration.Manifest.Id;
            var status = _statuses.Get(pluginId);
            foreach (var job in registration.Capabilities.OfType<IPluginScheduledJob>())
            {
                var key = TaskKey(pluginId, job.Key);
                var schedule = PluginJobSchedules.Effective(job, registration.Schedules, out _);
                descriptors.Add(new ScheduledTaskDescriptor(
                    key,
                    $"{registration.Manifest.Name}: {job.DisplayName}",
                    ScheduledTaskOwners.Plugin,
                    pluginId,
                    () => PluginJobSchedules.Describe(schedule, out _) ?? string.Empty,
                    "Set by the plugin",
                    NextRunProvider: now => status.IsPaused(PluginHostMessages.JobProblemKey(job.Key))
                        ? null
                        : NextDueFor(key, schedule, now)));
            }
        }

        return descriptors;
    }

    private static bool IsEligible(PluginRegistration registration) =>
        registration.Enabled
        && registration.LoadError is null
        && registration.Manifest.Permissions.Contains(PluginPermissionIds.ScheduledWork, StringComparer.Ordinal);

    private DateTimeOffset? NextDueFor(string taskKey, PluginSchedule schedule, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_timings.TryGetValue(taskKey, out var timing) && timing.NextDue is { } due)
            {
                return due;
            }
        }

        // Not looked at yet (the Engine has only just started): the next time its schedule gives from now.
        return PluginJobSchedules.NextDue(schedule, now);
    }

    private async Task ConsiderAsync(
        PluginRegistration registration,
        IPluginScheduledJob job,
        PluginHostStatus status,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var pluginId = registration.Manifest.Id;
        var taskKey = TaskKey(pluginId, job.Key);
        var problemKey = PluginHostMessages.JobProblemKey(job.Key);
        if (status.IsPaused(problemKey))
        {
            status.SetJobState(job.Key, job.DisplayName, "paused");
            return;
        }

        var schedule = PluginJobSchedules.Effective(job, registration.Schedules, out var overrideProblem);
        if (overrideProblem is not null && _ignoredOverrides.Add($"{taskKey}|{overrideProblem}"))
        {
            _logger.LogWarning("Plugin {PluginId}: {Problem}", pluginId, overrideProblem);
        }

        JobTiming timing;
        lock (_gate)
        {
            // A new status generation means the plugin was switched off and on since this job was last looked at.
            if (!_timings.TryGetValue(taskKey, out timing!) || timing.Generation != status.Generation)
            {
                timing = new JobTiming { Generation = status.Generation };
                _timings[taskKey] = timing;
            }
        }

        if (timing.NextDue is null)
        {
            timing.NextDue = await InitialNextDueAsync(taskKey, schedule, now, ct).ConfigureAwait(false);
        }

        lock (_gate)
        {
            status.SetJobState(job.Key, job.DisplayName, _running.Contains(taskKey) ? "running" : "scheduled");
        }

        if (timing.NextDue is not { } due || due > now)
        {
            return;
        }

        lock (_gate)
        {
            // No overlapping runs of one job.
            if (_running.Contains(taskKey))
            {
                return;
            }

            // A heavy job waits for ingestion to finish and for any other heavy job (D11); it stays due and is tried again next pass.
            var heavy = registration.Manifest.ResolvedDeclaration.WeightClass == PluginWeightClass.Heavy;
            if (heavy && (_ingestion?.Current.State == "working" || !_heavySlot.Wait(0)))
            {
                return;
            }

            // At most MaxConcurrentJobs at once; a job that finds no room stays due and is tried again next pass.
            if (!_slots.Wait(0))
            {
                if (heavy)
                {
                    _heavySlot.Release();
                }

                return;
            }

            _running.Add(taskKey);
            if (heavy)
            {
                _heavyRunning.Add(taskKey);
            }

            timing.NextDue = PluginJobSchedules.NextDue(schedule, now);
        }

        status.SetJobState(job.Key, job.DisplayName, "running");
        _inFlight[taskKey] = Task.Run(() => RunJobAsync(registration, job, status, taskKey, ct), CancellationToken.None);
    }

    private async Task<DateTimeOffset?> InitialNextDueAsync(string taskKey, PluginSchedule schedule, DateTimeOffset now, CancellationToken ct)
    {
        if (schedule.Interval is { } interval)
        {
            // An interval counts from the last run, so a restart does not push the next run further away.
            var last = (await _runs.ListRecentAsync(taskKey, 1, ct: ct).ConfigureAwait(false)).FirstOrDefault();
            return (last?.StartedAt ?? now) + interval;
        }

        return PluginJobSchedules.NextDue(schedule, now);
    }

    private async Task RunJobAsync(
        PluginRegistration registration,
        IPluginScheduledJob job,
        PluginHostStatus status,
        string taskKey,
        CancellationToken stoppingToken)
    {
        var pluginId = registration.Manifest.Id;
        var problemKey = PluginHostMessages.JobProblemKey(job.Key);
        var limitStopCounter = $"job-limit-stops:{job.Key}";
        IPluginExecutionContext? context = null;
        Task? jobTask = null;
        var stoppedForTime = false;
        try
        {
            var created = context = _contexts.Create(pluginId, "job");
            await _runner.RunAsync(
                taskKey,
                async runToken =>
                {
                    using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                    var started = jobTask = Task.Run(() => job.RunAsync(created, jobCancellation.Token), CancellationToken.None);
                    var limit = Task.Delay(_limits.JobMaxRunTime, runToken);
                    if (await Task.WhenAny(started, limit).ConfigureAwait(false) == started)
                    {
                        await started.ConfigureAwait(false);
                        return TaskRunResult.Succeeded();
                    }

                    runToken.ThrowIfCancellationRequested();
                    stoppedForTime = true;
                    jobCancellation.Cancel();
                    await Task.WhenAny(started, Task.Delay(_limits.JobCancelGrace, CancellationToken.None)).ConfigureAwait(false);
                    return TaskRunResult.Failed(PluginHostMessages.JobTooLong(_limits));
                },
                stoppingToken,
                pluginId).ConfigureAwait(false);

            if (stoppedForTime)
            {
                var stops = status.Increment(limitStopCounter);
                status.SetLastProblem($"{job.DisplayName}: {PluginHostMessages.JobTooLong(_limits)}");
                if (stops >= _limits.JobLimitStopsBeforePause)
                {
                    status.Pause(problemKey);
                    status.SetProblem(problemKey, PluginHostSeverity.NeedsAttention, PluginHostMessages.JobPaused(job.DisplayName, _limits));
                    status.SetJobState(job.Key, job.DisplayName, "paused");
                    _logger.LogWarning("Plugin {PluginId} job {JobKey} paused after running too long {Stops} times", pluginId, job.Key, stops);
                }
            }
            else
            {
                status.ClearCounter(limitStopCounter);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Engine is shutting down; the run record already says the run was interrupted.
        }
        catch (Exception ex)
        {
            // The run record already holds the failure; keep the plugin's card informed and the log useful.
            _logger.LogWarning(ex, "Plugin {PluginId} job {JobKey} failed", pluginId, job.Key);
            status.ClearCounter(limitStopCounter);
            status.SetLastProblem($"{job.DisplayName} failed: {ex.Message}");
            if (_activity is not null && _catalog.Get(pluginId) is { } failedPlugin)
            {
                await _activity.RecordFailureAsync(
                    new PluginActivityContext(failedPlugin.Manifest.Id, failedPlugin.Manifest.Version),
                    PluginFailureClassifier.Classify(ex),
                    summary: $"{job.DisplayName} failed.",
                    exception: ex).ConfigureAwait(false);
            }
        }
        finally
        {
            ObserveAndRelease(taskKey, job, status, context, jobTask);
        }
    }

    private void ObserveAndRelease(string taskKey, IPluginScheduledJob job, PluginHostStatus status, IPluginExecutionContext? context, Task? jobTask)
    {
        void Release()
        {
            lock (_gate)
            {
                _running.Remove(taskKey);
                if (_heavyRunning.Remove(taskKey))
                {
                    _heavySlot.Release();
                }
            }

            if (!status.IsPaused(PluginHostMessages.JobProblemKey(job.Key)))
            {
                status.SetJobState(job.Key, job.DisplayName, "scheduled");
            }

            _slots.Release();
            if (context is not null)
            {
                _ = DisposeQuietlyAsync(context);
            }
        }

        if (jobTask is { IsCompleted: false })
        {
            // A job that ignores its cancellation keeps its place (no overlap, no extra slot) until it really ends.
            _ = jobTask.ContinueWith(
                task =>
                {
                    _ = task.Exception; // observed here so a late failure is not an unhandled task error
                    Release();
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
        else
        {
            Release();
        }
    }

    private async Task DisposeQuietlyAsync(IPluginExecutionContext context)
    {
        try
        {
            await context.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "A plugin job's context could not be cleaned up");
        }
    }
}
