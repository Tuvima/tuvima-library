using MediaEngine.Contracts.Notifications;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Events;

/// <summary>How an event is offered as a notification: the generic line, what the routing setting is for, and where it goes until changed.</summary>
/// <param name="Quiet">A calm, nothing-is-wrong kind of event that the optional daily summary may batch into one message.</param>
internal sealed record LibraryEventNotification(string Title, string Description, bool AdminOnly, IReadOnlyList<string> DefaultChannels, bool Quiet = false);

/// <summary>
/// One event the Library can tell the outside about. <see cref="Notification"/> is set when administrators can also route it as
/// a notification; <see cref="PublishedByNotifications"/> is true when the notification service is what raises it (the others
/// are raised where the thing happens).
/// </summary>
internal sealed record LibraryEventEntry(
    string EventType,
    ApplicationPermissionId ReadPermission,
    bool IsLibraryScoped,
    bool IsProfileScoped = false,
    LibraryEventNotification? Notification = null,
    bool PublishedByNotifications = false);

/// <summary>
/// The one list of events (Developer tools spec D-W, WP31). Webhooks, plugin event subscriptions and notification routes all
/// read it, so an event added here shows up in all three: its name is what a webhook selects, what a plugin subscribes to, and
/// what the routing table lists (when it has a <see cref="LibraryEventEntry.Notification"/>).
/// </summary>
internal static class LibraryEvents
{
    private static readonly string[] InboxOnly = [NotificationChannelIds.Dashboard];
    private static readonly string[] InboxAndEmail = [NotificationChannelIds.Dashboard, NotificationChannelIds.Email];
    private static readonly string[] Nowhere = [];

    public static readonly IReadOnlyList<LibraryEventEntry> All =
    [
        new("playback.started", ApplicationPermissionIds.PlaybackSessionsRead, true, true),
        new("playback.paused", ApplicationPermissionIds.PlaybackSessionsRead, true, true),
        new("playback.stopped", ApplicationPermissionIds.PlaybackSessionsRead, true, true),
        new("playback.completed", ApplicationPermissionIds.PlaybackSessionsRead, true, true),
        new("library.item_added", ApplicationPermissionIds.LibraryChangesRead, true),
        new("library.item_removed", ApplicationPermissionIds.LibraryChangesRead, true),
        new("library.item_updated", ApplicationPermissionIds.LibraryChangesRead, true),
        new("ingestion.started", ApplicationPermissionIds.IngestionStatusRead, false),
        // ingestion.progress is sent while the live queue is working, ingestion.completed once when it returns to idle;
        // both are subject "ingestion/current" with payload { files_found, files_done, files_waiting, progress, state }.
        new("ingestion.progress", ApplicationPermissionIds.IngestionStatusRead, false),
        new("ingestion.completed", ApplicationPermissionIds.IngestionStatusRead, false,
            Notification: new("New media finished being added", "A batch of new files has been identified and added.", true, Nowhere)),
        new("ingestion.failed", ApplicationPermissionIds.IngestionStatusRead, false),
        new("metadata.updated", ApplicationPermissionIds.MetadataRead, true),
        new("metadata.review_required", ApplicationPermissionIds.ReviewRead, true),
        new("provider.status_changed", ApplicationPermissionIds.ProvidersStatusRead, false),
        new("plugin.job_started", ApplicationPermissionIds.PluginsJobsRead, false),
        new("plugin.job_completed", ApplicationPermissionIds.PluginsJobsRead, false),
        new("system.health_changed", ApplicationPermissionIds.SystemStatusRead, false),
        new(NotificationEventIds.BackupFailed, ApplicationPermissionIds.SystemStatusRead, false,
            Notification: new("A backup did not finish", "A backup could not be written.", true, InboxAndEmail), PublishedByNotifications: true),
        new(NotificationEventIds.BackupCompleted, ApplicationPermissionIds.SystemStatusRead, false,
            Notification: new("A backup finished", "A backup was written successfully.", true, Nowhere, Quiet: true), PublishedByNotifications: true),
        new(NotificationEventIds.ToolMissing, ApplicationPermissionIds.SystemStatusRead, false,
            Notification: new("A tool the Library needs is missing", "FFmpeg or another required tool is not ready, so some files are waiting.", true, InboxAndEmail), PublishedByNotifications: true),
        new(NotificationEventIds.PluginBroken, ApplicationPermissionIds.SystemStatusRead, false,
            Notification: new("A plugin stopped working", "A plugin has a problem that stops it doing its job.", true, InboxAndEmail), PublishedByNotifications: true),
        new(NotificationEventIds.PluginUpdateAvailable, ApplicationPermissionIds.SystemStatusRead, false,
            Notification: new("A plugin update is available", "A newer version of an installed plugin is in the plugin directory.", true, InboxOnly, Quiet: true), PublishedByNotifications: true),
        new(NotificationEventIds.DiskLow, ApplicationPermissionIds.SystemStatusRead, false,
            Notification: new("Storage is running low", "A drive the Library uses is nearly full.", true, InboxAndEmail), PublishedByNotifications: true),
        new(NotificationEventIds.ReviewWaiting, ApplicationPermissionIds.ReviewRead, false,
            Notification: new("Items are waiting in Needs Review", "The Library was not sure about some items and wants a person to look.", true, InboxOnly, Quiet: true), PublishedByNotifications: true),
        new(NotificationEventIds.RecycleBinKeepRequested, ApplicationPermissionIds.SystemStatusRead, false,
            Notification: new("Someone asked to keep a deleted item", "A person asked for an item in the Recycle bin to be put back.", true, InboxOnly), PublishedByNotifications: true),
        new(NotificationEventIds.RecycleBinLeavingSoon, ApplicationPermissionIds.SystemStatusRead, false,
            Notification: new("Items are about to leave the Recycle bin", "Some deleted items will be removed for good within days unless they are put back.", true, InboxOnly, Quiet: true), PublishedByNotifications: true),
        new(NotificationEventIds.AccountNewDevice, ApplicationPermissionIds.SystemStatusRead, false,
            Notification: new("A new device signed in", "Someone signed in to an account from a device that account had not used before.", true, InboxOnly), PublishedByNotifications: true),
        new(NotificationEventIds.SecurityFailedSignIns, ApplicationPermissionIds.SystemStatusRead, false,
            Notification: new("Several sign-ins failed in a row", "A run of failed sign-in attempts, which may be a mistyped password or someone guessing.", true, InboxOnly), PublishedByNotifications: true),
        new(NotificationEventIds.LibraryDriveOffline, ApplicationPermissionIds.SystemStatusRead, false,
            Notification: new("A library drive went offline", "A folder the Library reads from stopped answering, such as an unplugged or sleeping drive.", true, InboxOnly), PublishedByNotifications: true),
        new(NotificationEventIds.LibraryUpdateAvailable, ApplicationPermissionIds.SystemStatusRead, false,
            Notification: new("A Library update is available", "A newer version of Library is ready to install.", true, InboxOnly, Quiet: true), PublishedByNotifications: true),
        new(NotificationEventIds.EncodeFailedRepeatedly, ApplicationPermissionIds.SystemStatusRead, false,
            Notification: new("Video preparing keeps failing", "Several videos in a row could not be prepared for playback.", true, InboxOnly), PublishedByNotifications: true),
        new(NotificationEventIds.AccountInvited, ApplicationPermissionIds.SystemStatusRead, false,
            Notification: new("Someone was invited", "An invitation to join this Library was created.", true, Nowhere), PublishedByNotifications: true),
    ];

    public static LibraryEventEntry? Find(string eventType) =>
        All.FirstOrDefault(entry => string.Equals(entry.EventType, eventType, StringComparison.Ordinal));
}
