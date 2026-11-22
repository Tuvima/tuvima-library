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

    public Task<MailServerSettingsDto?> GetMailServerAsync(CancellationToken ct = default) =>
        GetAsync<MailServerSettingsDto>("GET /notifications/mail-server", "/notifications/mail-server", ct: ct);

    public Task<MailServerSettingsDto?> SaveMailServerAsync(MailServerRequest request, CancellationToken ct = default) =>
        PutAsync<MailServerRequest, MailServerSettingsDto>("PUT /notifications/mail-server", "/notifications/mail-server", request, ct: ct);

    public Task<NotificationTestResponse?> TestMailServerAsync(MailServerRequest request, CancellationToken ct = default) =>
        PostAsync<MailServerRequest, NotificationTestResponse>("POST /notifications/mail-server/test", "/notifications/mail-server/test", request, ct: ct);

    public Task<NotificationChannelTypesResponse?> GetNotificationChannelTypesAsync(CancellationToken ct = default) =>
        GetAsync<NotificationChannelTypesResponse>("GET /notifications/channel-types", "/notifications/channel-types", ct: ct);

    public Task<NotificationChannelDetailDto?> GetNotificationChannelAsync(string channelId, CancellationToken ct = default) =>
        GetAsync<NotificationChannelDetailDto>("GET /notifications/channels/{id}", $"/notifications/channels/{Uri.EscapeDataString(channelId)}", ct: ct);

    public Task<NotificationChannelSaveResult> AddNotificationChannelAsync(SaveNotificationChannelRequest request, CancellationToken ct = default) =>
        SendChannelAsync("POST /notifications/channels", () => _http.PostAsJsonAsync("/notifications/channels", request, ct), ct);

    public Task<NotificationChannelSaveResult> UpdateNotificationChannelAsync(string channelId, SaveNotificationChannelRequest request, CancellationToken ct = default) =>
        SendChannelAsync("PUT /notifications/channels/{id}", () => _http.PutAsJsonAsync($"/notifications/channels/{Uri.EscapeDataString(channelId)}", request, ct), ct);

    public Task<bool> RemoveNotificationChannelAsync(string channelId, CancellationToken ct = default) =>
        DeleteAsync("DELETE /notifications/channels/{id}", $"/notifications/channels/{Uri.EscapeDataString(channelId)}", ct: ct);

    private async Task<NotificationChannelSaveResult> SendChannelAsync(string endpoint, Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            using var response = await send();
            if (response.IsSuccessStatusCode)
            {
                ClearFailure(endpoint);
                return new NotificationChannelSaveResult(await response.Content.ReadFromJsonAsync<NotificationChannelDetailDto>(cancellationToken: ct), null);
            }

            // A form the Engine refuses comes back as one plain sentence; it is shown as it is.
            var sentence = await ReadProblemDetailAsync(response, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && !string.IsNullOrWhiteSpace(sentence))
            {
                return new NotificationChannelSaveResult(null, sentence);
            }

            await RecordHttpFailureAsync(endpoint, response, ct);
            return new NotificationChannelSaveResult(null, null);
        }
        catch (OperationCanceledException) { return new NotificationChannelSaveResult(null, null); }
        catch (Exception ex)
        {
            RecordExceptionFailure(endpoint, ex);
            return new NotificationChannelSaveResult(null, null);
        }
    }
}
