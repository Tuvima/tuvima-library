using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ScheduledTasks;

/// <summary>
/// Describes every recurring task the Engine runs, in one place, for the Scheduled tasks list. Each description reads its
/// schedule from the same setting its service reads, at the moment it is asked, so an edit shows without a restart.
/// </summary>
public static class ScheduledTaskCatalog
{
    /// <summary>Plain text shown as the schedule of continuous webhook delivery.</summary>
    public const string WebhookDeliverySchedule = "every 5 seconds";

    private const string MaintenancePlace = "Library maintenance schedule";

    private static readonly IReadOnlyDictionary<string, string> DefaultSchedules = new MaintenanceSettings().Schedules;

    public static void RegisterAll(
        IScheduledTaskRegistry registry,
        IConfigurationLoader configLoader,
        ApplicationWebhookRepository webhooks)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(configLoader);
        ArgumentNullException.ThrowIfNull(webhooks);

        Maintenance(ScheduledTaskKeys.ActivityPruning, "Activity log cleanup");
        Maintenance(ScheduledTaskKeys.LibraryReconciliation, "Library file check");
        Maintenance(ScheduledTaskKeys.MissingUniverseSweep, "Missing Universe retry");
        Maintenance(ScheduledTaskKeys.RejectedFileCleanup, "Rejected file cleanup");
        Maintenance(ScheduledTaskKeys.UniverseEnrichment, "Universe enrichment");
        Maintenance(ScheduledTaskKeys.RetagSweep, "File tag refresh");
        Maintenance(ScheduledTaskKeys.StorageMaintenance, "Storage maintenance");
        Maintenance(ScheduledTaskKeys.LogFileRetention, "Log file cleanup");
        Maintenance(ScheduledTaskKeys.NightlyEnrichmentSweep, "Nightly enrichment catch-up");

        registry.Register(new ScheduledTaskDescriptor(
            ScheduledTaskKeys.PluginSegmentDetection,
            "Playback segment detection",
            ScheduledTaskOwners.Plugin,
            PluginId: null,
            () => MaintenanceSchedule(ScheduledTaskKeys.PluginSegmentDetection),
            MaintenancePlace));

        registry.Register(new ScheduledTaskDescriptor(
            ScheduledTaskKeys.WebhookDelivery,
            "Webhook delivery",
            ScheduledTaskOwners.Library,
            PluginId: null,
            () => WebhookDeliverySchedule,
            "Not adjustable; deliveries retry on their own",
            IsContinuous: true,
            LastRunProvider: async ct => ToRun(await webhooks.LastAttemptAsync(ct).ConfigureAwait(false))));

        Continuous(ScheduledTaskKeys.ProviderHealthChecks, "Provider health checks", "every minute");
        Continuous(ScheduledTaskKeys.PlaybackSessionCleanup, "Playback session cleanup", "every minute");
        Continuous(ScheduledTaskKeys.FolderHealthCheck, "Library folder health check", "every 30 seconds");
        ContinuousAt(
            ScheduledTaskKeys.AdaptiveStreamingCleanup,
            "Streaming file cleanup",
            () => $"every {Math.Clamp(configLoader.LoadTranscoding().AdaptiveHls.CleanupIntervalMinutes, 1, 1440)} minutes",
            "Transcoding settings");
        Continuous(ScheduledTaskKeys.EnrichmentRefreshSchedule, "Scheduled metadata refresh", "every hour");
        Continuous(ScheduledTaskKeys.CollectionListRefresh, "Imported list refresh", "every 15 minutes");
        ContinuousAt(
            ScheduledTaskKeys.UnmatchedRetry,
            "Unmatched file retry",
            () =>
            {
                var days = Math.Clamp(configLoader.LoadMaintenance().UnmatchedRetryDays, UnmatchedRetryLimits.MinDays, UnmatchedRetryLimits.MaxDays);
                return days == 1 ? "every day" : $"every {days} days";
            },
            "Library maintenance settings");

        void Continuous(string key, string name, string schedule) =>
            ContinuousAt(key, name, () => schedule, "Not adjustable");

        void ContinuousAt(string key, string name, Func<string> schedule, string place) =>
            registry.Register(new ScheduledTaskDescriptor(
                key, name, ScheduledTaskOwners.Library, PluginId: null, schedule, place, IsContinuous: true));

        void Maintenance(string key, string name) =>
            registry.Register(new ScheduledTaskDescriptor(
                key, name, ScheduledTaskOwners.Library, PluginId: null, () => MaintenanceSchedule(key), MaintenancePlace));

        string MaintenanceSchedule(string key)
        {
            var configured = configLoader.LoadMaintenance().Schedules.GetValueOrDefault(key);
            return string.IsNullOrWhiteSpace(configured) ? DefaultSchedules.GetValueOrDefault(key, string.Empty) : configured;
        }
    }

    /// <summary>
    /// Webhook delivery keeps no run records (it ticks every few seconds); its last run is the newest delivery attempt.
    /// A delivery still pending after an attempt is waiting for a retry, which means that attempt did not get through.
    /// </summary>
    private static ScheduledTaskRun? ToRun(ApplicationWebhookLastAttempt? attempt)
    {
        if (attempt is null)
        {
            return null;
        }

        var (outcome, message) = attempt.Status switch
        {
            "delivered" => (ScheduledTaskOutcomes.Succeeded, "Delivered."),
            "skipped" => (ScheduledTaskOutcomes.Skipped, "Skipped."),
            "pending" => (ScheduledTaskOutcomes.Failed, "Not delivered; a retry is scheduled."),
            _ => (ScheduledTaskOutcomes.Failed, "Not delivered; retries ran out."),
        };
        return new ScheduledTaskRun(
            Guid.Empty, ScheduledTaskKeys.WebhookDelivery, null, attempt.At, attempt.At, outcome, null, message);
    }
}
