using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Backups;

/// <summary>
/// Runs the scheduled backup and the monthly restore check (Developer tools spec WP25). It looks once a minute whether the
/// schedule says a backup is due, so a changed schedule applies at once and a machine that was off simply runs late. Each run
/// is recorded under Scheduled tasks. A failed backup shows there as failed and tells the administrators.
/// </summary>
internal sealed class BackupScheduleWorker(
    BackupSettingsAccessor settingsAccessor,
    BackupOrchestrator orchestrator,
    IRecurringTaskRecorder recorder,
    TimeProvider clock,
    ILogger<BackupScheduleWorker> logger) : BackgroundService, IScheduledTaskDescriptorSource
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan RestoreCheckInterval = TimeSpan.FromDays(30);

    public IReadOnlyList<ScheduledTaskDescriptor> ListScheduledTasks() =>
    [
        new ScheduledTaskDescriptor(
            ScheduledTaskKeys.ScheduledBackup,
            "Library backup",
            ScheduledTaskOwners.Library,
            null,
            () => settingsAccessor.Load().Cron(),
            "System > Backup and recovery",
            NextRunProvider: now =>
            {
                var settings = settingsAccessor.Load();
                var cron = settings.Cron();
                return string.IsNullOrEmpty(cron) ? null : CronScheduler.NextOccurrence(cron, settings.LastScheduledRunAt ?? now);
            }),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(PollInterval, clock);
        try
        {
            do
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Engine is stopping.
        }
    }

    /// <summary>One look at the schedule. Public to the assembly so tests can drive it with a fake clock.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        try
        {
            var settings = await settingsAccessor.LoadAsync(ct).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            var cron = settings.Cron();
            if (!string.IsNullOrEmpty(cron)
                && CronScheduler.NextOccurrence(cron, settings.LastScheduledRunAt ?? now, clock.LocalTimeZone) is { } due
                && due <= now)
            {
                await settingsAccessor.UpdateAsync(document => document.LastScheduledRunAt = now, ct).ConfigureAwait(false);
                await recorder.RecordAsync(ScheduledTaskKeys.ScheduledBackup, async token =>
                {
                    var result = await orchestrator.RunAsync(token).ConfigureAwait(false);
                    if (!result.AllOk)
                    {
                        throw new BackupDestinationException("The backup was made but a copy to a destination failed.");
                    }

                    return result.Destinations.Count;
                }, ct).ConfigureAwait(false);
            }

            settings = await settingsAccessor.LoadAsync(ct).ConfigureAwait(false);
            if (settings.ScheduleEnabled && (settings.LastRestoreCheckAt is null || now - settings.LastRestoreCheckAt >= RestoreCheckInterval))
            {
                await orchestrator.RunRestoreCheckAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The run record and the notification already say what failed; the loop carries on to the next look.
            logger.LogWarning(ex, "The scheduled backup check did not finish.");
        }
    }
}
