using MediaEngine.Api.Services.Events;
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
    /// <summary>The events that have a notification setting, taken from the one event list webhooks and plugin subscriptions share.</summary>
    public static readonly IReadOnlyList<NotificationEventDefinition> All =
    [
        .. LibraryEvents.All
            .Where(entry => entry.Notification is not null)
            .Select(entry => new NotificationEventDefinition(
                entry.EventType, entry.Notification!.Title, entry.Notification.Description, entry.Notification.AdminOnly, entry.Notification.DefaultChannels)),
    ];

    public static NotificationEventDefinition? Find(string id) =>
        All.FirstOrDefault(definition => string.Equals(definition.Id, id, StringComparison.Ordinal));

    /// <summary>The wording of the "Send test" message.</summary>
    public const string TestTitle = "Library test message";
}
