using MediaEngine.Contracts.Notifications;

namespace MediaEngine.Web.Services.Integration;

public sealed partial class EngineApiClient
{
    public Task<NotificationListResponse?> GetNotificationsAsync(CancellationToken ct = default) =>
        GetAsync<NotificationListResponse>("GET /notifications", "/notifications", ct: ct);

    public Task<bool> MarkNotificationsReadAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        PostAsync("POST /notifications/read", "/notifications/read", new MarkNotificationsReadRequest { Ids = [.. ids] }, ct: ct);

    public Task<NotificationSettingsResponse?> GetNotificationSettingsAsync(CancellationToken ct = default) =>
        GetAsync<NotificationSettingsResponse>("GET /notifications/settings", "/notifications/settings", ct: ct);

    public Task<NotificationSettingsResponse?> UpdateNotificationSettingsAsync(UpdateNotificationSettingsRequest request, CancellationToken ct = default) =>
        PutAsync<UpdateNotificationSettingsRequest, NotificationSettingsResponse>("PUT /notifications/settings", "/notifications/settings", request, ct: ct);

    public Task<NotificationTestResponse?> SendNotificationTestAsync(string channelId, CancellationToken ct = default) =>
        PostAsync<object, NotificationTestResponse>("POST /notifications/channels/{id}/test", $"/notifications/channels/{Uri.EscapeDataString(channelId)}/test", new { }, ct: ct);

    public Task<bool> EnableNotificationChannelAsync(string channelId, CancellationToken ct = default) =>
        PostAsync("POST /notifications/channels/{id}/enable", $"/notifications/channels/{Uri.EscapeDataString(channelId)}/enable", new { }, ct: ct);
}
