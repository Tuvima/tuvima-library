using MediaEngine.Contracts.System;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ScheduledTasks;

/// <summary>Builds the <c>GET /system/scheduled-tasks</c> answer from the registry and the run records.</summary>
public static class ScheduledTaskListBuilder
{
    /// <summary>How many recent runs each task carries for the Scheduled Tasks history.</summary>
    public const int RecentRunCount = 5;

    public static async Task<ScheduledTaskListResponse> BuildAsync(
        IScheduledTaskRegistry registry,
        IScheduledTaskRunRepository runs,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runs);

        var latest = await runs.GetLatestPerTaskAsync(ct).ConfigureAwait(false);
        var tasks = new List<ScheduledTaskDto>();
        foreach (var descriptor in registry.List())
        {
            ScheduledTaskRun? last = descriptor.LastRunProvider is not null
                ? await descriptor.LastRunProvider(ct).ConfigureAwait(false)
                : latest.GetValueOrDefault(descriptor.Key);
            var recent = await runs.ListRecentAsync(descriptor.Key, RecentRunCount, 0, descriptor.PluginId, ct).ConfigureAwait(false);
            var schedule = descriptor.ScheduleProvider();
            tasks.Add(new ScheduledTaskDto
            {
                Key = descriptor.Key,
                Name = descriptor.Name,
                Owner = descriptor.Owner,
                PluginId = descriptor.PluginId,
                Schedule = schedule,
                IsContinuous = descriptor.IsContinuous,
                SettingsPlace = descriptor.SettingsPlace,
                NextRunAt = descriptor.NextRunProvider is { } nextRun
                    ? nextRun(now)?.ToUniversalTime()
                    : descriptor.IsContinuous ? null : CronScheduler.NextOccurrence(schedule, now)?.ToUniversalTime(),
                LastRun = last is null ? null : ToDto(last),
                RecentRuns = recent.Select(ToDto).ToList(),
            });
        }

        return new ScheduledTaskListResponse { Tasks = tasks };
    }

    private static ScheduledTaskLastRunDto ToDto(ScheduledTaskRun run) => new()
    {
        StartedAt = run.StartedAt,
        FinishedAt = run.FinishedAt,
        Outcome = run.Outcome,
        ItemsProcessed = run.ItemsProcessed,
        Message = run.Message,
    };
}
