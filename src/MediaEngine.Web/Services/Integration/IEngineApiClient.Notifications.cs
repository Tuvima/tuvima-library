using MediaEngine.Contracts.Notifications;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /notifications: the Dashboard inbox, newest first. Null when the Engine could not be asked.</summary>
    Task<NotificationListResponse?> GetNotificationsAsync(CancellationToken ct = default);

    /// <summary>POST /notifications/read: marks the given messages read, or every unread message when the list is empty.</summary>
    Task<bool> MarkNotificationsReadAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>GET /notifications/settings: where events go, channel health, quiet hours and the include-details choice.</summary>
    Task<NotificationSettingsResponse?> GetNotificationSettingsAsync(CancellationToken ct = default);

    /// <summary>PUT /notifications/settings: saves the changes and returns the settings as they now stand. Null when it could not be saved.</summary>
    Task<NotificationSettingsResponse?> UpdateNotificationSettingsAsync(UpdateNotificationSettingsRequest request, CancellationToken ct = default);

    /// <summary>POST /notifications/channels/{id}/test: sends a test message to the signed-in administrator.</summary>
    Task<NotificationTestResponse?> SendNotificationTestAsync(string channelId, CancellationToken ct = default);

    /// <summary>POST /notifications/channels/{id}/enable: turns a channel that switched itself off back on.</summary>
    Task<bool> EnableNotificationChannelAsync(string channelId, CancellationToken ct = default);

    /// <summary>The mail server email is sent through (the password is never returned).</summary>
    Task<MailServerSettingsDto?> GetMailServerAsync(CancellationToken ct = default);

    /// <summary>Saves the mail server; null when the Engine refused it.</summary>
    Task<MailServerSettingsDto?> SaveMailServerAsync(MailServerRequest request, CancellationToken ct = default);

    /// <summary>Sends a test email to the signed-in administrator with the form's values, without saving them.</summary>
    Task<NotificationTestResponse?> TestMailServerAsync(MailServerRequest request, CancellationToken ct = default);
}
