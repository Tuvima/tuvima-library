using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.Libraries;

/// <summary>
/// Empties the Recycle bin of what has been there longer than the retention setting (spec Delete a show, season, album or Series, D18)
/// and, once a day, tells administrators what is about to leave (D23). The purge is the same permanent step as Empty, so each run that
/// removes anything writes one line to the activity history.
/// </summary>
public sealed class RecycleBinRetentionHostedService(
    RecycleBinService bin,
    IConfigurationLoader configuration,
    ISystemActivityRepository activity,
    INotificationPublisher notifications,
    ILogger<RecycleBinRetentionHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>One pass: purge what is expired, then warn about what is close. Public so a test can run it at a chosen time.</summary>
    public async Task RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var settings = configuration.LoadMaintenance();
            var vault = VaultLibraryIds();
            var purged = bin.PurgeExpired(now, settings.RecycleBinRetentionDays, vault);
            if (purged.Items > 0)
            {
                await activity.LogAsync(new SystemActivityEntry
                {
                    ActionType = SystemActionType.AutoPurge,
                    EntityType = "RecycleBin",
                    Detail = $"The Recycle bin removed {purged.Items} item{(purged.Items == 1 ? string.Empty : "s")} for good after {settings.RecycleBinRetentionDays} days, freeing {FormatBytes(purged.Bytes)}.",
                }, ct).ConfigureAwait(false);
            }

            var summary = RecycleBinSummaryBuilder.Build(
                bin.List(vault), now, settings.RecycleBinRetentionDays, settings.RecycleBinWarningDays, null,
                new RecycleBinViewer(CanManage: true, CanListItems: false, Requester: string.Empty));
            if (summary.LeavingSoonItems > 0)
            {
                notifications.Publish(new NotificationRequest(
                    NotificationEventIds.RecycleBinLeavingSoon,
                    Subject: "recycle-bin",
                    Detail: $"{summary.LeavingSoonItems} item{(summary.LeavingSoonItems == 1 ? string.Empty : "s")} ({FormatBytes(summary.LeavingSoonBytes)}) will be deleted from the Recycle bin within {settings.RecycleBinWarningDays} days."));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The Recycle bin clean-up could not finish; it tries again tomorrow");
        }
    }

    private HashSet<string> VaultLibraryIds() =>
        [.. configuration.LoadLibraries().Libraries.Where(library => library.IsVault).Select(library => library.Id)];

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824d:0.#} GB",
        >= 1_048_576 => $"{bytes / 1_048_576d:0.#} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };
}
