using MediaEngine.Contracts.Notifications;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Notifications;

/// <summary>One kind of thing the Library can tell an administrator about.</summary>
/// <param name="Id">One of <see cref="NotificationEventIds"/>.</param>
/// <param name="Title">The short generic line. This is all that leaves the computer unless the administrator chooses to include details.</param>
/// <param name="Description">What the setting is for, in the routing table.</param>
/// <param name="AdminOnly">Only administrators hear about it. Every current event is; the flag keeps that a decision, not an accident.</param>
/// <param name="DefaultChannels">Where it goes until the administrator changes it.</param>
internal sealed record NotificationEventDefinition(
    string Id,
    string Title,
    string Description,
    bool AdminOnly,
    IReadOnlyList<string> DefaultChannels);

/// <summary>
/// Every event the notification service knows (Developer tools spec WP23). Password reset is deliberately not here: it is
/// a one-off message to the person who asked, not a notification, and it never goes through the routing table.
/// </summary>
internal static class NotificationEventCatalogue
{
    private static readonly string[] InboxOnly = [NotificationChannelIds.Dashboard];
    private static readonly string[] InboxAndEmail = [NotificationChannelIds.Dashboard, NotificationChannelIds.Email];

    public static readonly IReadOnlyList<NotificationEventDefinition> All =
    [
        new(NotificationEventIds.BackupFailed, "A backup did not finish", "A backup could not be written.", true, InboxAndEmail),
        new(NotificationEventIds.BackupCompleted, "A backup finished", "A backup was written successfully.", true, []),
        new(NotificationEventIds.ToolMissing, "A tool the Library needs is missing", "FFmpeg or another required tool is not ready, so some files are waiting.", true, InboxAndEmail),
        new(NotificationEventIds.PluginBroken, "A plugin stopped working", "A plugin has a problem that stops it doing its job.", true, InboxAndEmail),
        new(NotificationEventIds.PluginUpdateAvailable, "A plugin update is available", "A newer version of an installed plugin is in the plugin directory.", true, InboxOnly),
        new(NotificationEventIds.DiskLow, "Storage is running low", "A drive the Library uses is nearly full.", true, InboxAndEmail),
        new(NotificationEventIds.IngestionCompleted, "New media finished being added", "A batch of new files has been identified and added.", true, []),
        new(NotificationEventIds.ReviewWaiting, "Items are waiting in Needs Review", "The Library was not sure about some items and wants a person to look.", true, InboxOnly),
        new(NotificationEventIds.AccountInvited, "Someone was invited", "An invitation to join this Library was created.", true, []),
    ];

    public static NotificationEventDefinition? Find(string id) =>
        All.FirstOrDefault(definition => string.Equals(definition.Id, id, StringComparison.Ordinal));

    /// <summary>The wording of the "Send test" message.</summary>
    public const string TestTitle = "Library test message";
}
