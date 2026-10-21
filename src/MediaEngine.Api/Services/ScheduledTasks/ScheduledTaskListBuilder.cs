using MediaEngine.Contracts.System;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ScheduledTasks;

/// <summary>Builds the <c>GET /system/scheduled-tasks</c> answer from the registry and the run records.</summary>
public static class ScheduledTaskListBuilder
{
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
                LastRun = last is null
                    ? null
                    : new ScheduledTaskLastRunDto
                    {
                        StartedAt = last.StartedAt,
                        FinishedAt = last.FinishedAt,
                        Outcome = last.Outcome,
                        ItemsProcessed = last.ItemsProcessed,
                        Message = last.Message,
                    },
            });
        }

        return new ScheduledTaskListResponse { Tasks = tasks };
    }
}
