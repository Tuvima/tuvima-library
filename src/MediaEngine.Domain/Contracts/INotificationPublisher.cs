namespace MediaEngine.Domain.Contracts;

/// <summary>What happened, for the notification service. The service decides who hears about it and by which channel.</summary>
/// <param name="EventId">An id from the notification catalogue, e.g. <c>backup.failed</c>.</param>
/// <param name="Subject">What it was about (a plugin name, a tool name), used to fold repeats together. May be empty.</param>
/// <param name="Detail">One or two plain sentences for the Dashboard inbox. Never a secret, a token or a file path outside the Library.</param>
public sealed record NotificationRequest(string EventId, string Subject = "", string Detail = "");

/// <summary>
/// Hands something that happened to the notification service. Fire and forget: it returns at once, never throws, and never
/// holds up the work that raised it, so a slow mail server cannot slow a backup.
/// </summary>
public interface INotificationPublisher
{
    void Publish(NotificationRequest request);
}

/// <summary>A publisher that does nothing, for parts of the Engine and tests that run without the notification service.</summary>
public sealed class NullNotificationPublisher : INotificationPublisher
{
    public static readonly NullNotificationPublisher Instance = new();

    public void Publish(NotificationRequest request)
    {
    }
}

/// <summary>The ids of the events the notification service knows. The catalogue in the Engine gives each its wording and default routes.</summary>
public static class NotificationEventIds
{
    public const string BackupFailed = "backup.failed";
    public const string BackupCompleted = "backup.completed";
    public const string ToolMissing = "tool.missing";
    public const string PluginBroken = "plugin.broken";
    public const string PluginUpdateAvailable = "plugin.update_available";
    public const string DiskLow = "disk.low";
    public const string IngestionCompleted = "ingestion.completed";
    public const string ReviewWaiting = "review.waiting";
    public const string AccountInvited = "account.invited";
    public const string RecycleBinKeepRequested = "recycle_bin.keep_requested";
    public const string RecycleBinLeavingSoon = "recycle_bin.leaving_soon";

    /// <summary>The "Send test" message. Never routed; it goes straight to the channel being tested.</summary>
    public const string SystemTest = "system.test";
}
