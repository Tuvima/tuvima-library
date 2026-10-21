using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaEngine.Api.Services.Events;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Events;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.Webhooks;

/// <summary>
/// Builds the history rows for one delivery attempt. A result is only ever the transport's short status text, so no
/// response body, event body or secret can reach the history.
/// </summary>
internal static class WebhookAttempts
{
    public static ApplicationWebhookAttempt Describe(ApplicationWebhookDelivery delivery, string eventType, WebhookSendResult result,
        DateTimeOffset at, TimeSpan elapsed, bool isTest) => new()
        {
            Id = Guid.NewGuid(),
            WebhookId = delivery.WebhookId,
            DeliveryId = delivery.Id,
            EventId = delivery.EventId,
            EventType = eventType,
            AttemptNumber = delivery.AttemptCount,
            AttemptedAt = at,
            Delivered = result.Delivered,
            Result = result.Status,
            HttpStatus = ParseHttpStatus(result.Status),
            DurationMs = (long)elapsed.TotalMilliseconds,
            IsTest = isTest,
        };

    /// <summary>The status code in a transport result such as "HTTP 503", or null when the receiver never answered.</summary>
    internal static int? ParseHttpStatus(string result) =>
        result.StartsWith("HTTP ", StringComparison.Ordinal)
        && int.TryParse(result.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : null;

    public static ApplicationWebhookAttemptResponse Map(ApplicationWebhookAttempt attempt) => new(
        attempt.Id, attempt.EventType, attempt.AttemptNumber, attempt.AttemptedAt, attempt.Delivered,
        attempt.Result, attempt.DurationMs, attempt.IsTest);
}

/// <summary>
/// The "Send test" button: sends one <c>webhook.test</c> event to a switched-on webhook straight away, through the same
/// signed transport and address checks as a real delivery, and records it as a finished delivery plus an attempt marked as
/// a test. A test is never retried and carries no library data.
/// </summary>
internal sealed class ApplicationWebhookTestSender(
    ApplicationWebhookRepository repository,
    ApplicationWebhookService management,
    IApplicationEventDeliveryAuthorizer authorization,
    IWebhookTransport transport,
    TimeProvider clock)
{
    /// <summary>Not a subscribable event type: it exists only for this button.</summary>
    public const string TestEventType = "webhook.test";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ApplicationWebhookAttemptResponse> SendAsync(Guid applicationId, Guid webhookId, CancellationToken ct)
    {
        var webhook = await management.GetAsync(applicationId, webhookId, ct);
        if (!webhook.IsEnabled)
        {
            throw new InvalidOperationException("Turn this webhook on to send a test.");
        }

        var subscribed = JsonSerializer.Deserialize<string[]>(webhook.EventTypesJson) ?? [];
        if (!await authorization.CanSubscribeAsync(applicationId, subscribed, ct))
        {
            throw new InvalidOperationException("This integration no longer has access to send events, so a test cannot be sent.");
        }

        var now = clock.GetUtcNow();
        var delivery = new ApplicationWebhookDelivery
        {
            Id = Guid.NewGuid(),
            WebhookId = webhook.Id,
            WebhookVersion = webhook.Version,
            EventId = Guid.NewGuid(),
            EventSequence = 0,
            AttemptCount = 1,
            CreatedAt = now,
            NextAttemptAt = now,
        };
        var envelope = ApplicationEventEnvelopeMapper.ToEnvelope(new StoredApplicationEvent(
            0, delivery.EventId, TestEventType, 1, now, await repository.EventServerIdAsync(ct),
            new("webhook", webhook.Id.ToString("D")), "{\"message\":\"Test delivery from Library\"}"));
        delivery.EventJson = JsonSerializer.Serialize(envelope, Json);

        var started = Stopwatch.GetTimestamp();
        WebhookSendResult result;
        try
        {
            result = await transport.SendAsync(webhook.Url, webhook.AllowLocalNetwork, management.RevealSecret(webhook),
                delivery.Id, delivery.EventId, Encoding.UTF8.GetBytes(delivery.EventJson), ct);
        }
        catch (CryptographicException)
        {
            result = new(false, "Signing secret unavailable; rotate it to resume delivery", false);
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        delivery.UpdatedAt = clock.GetUtcNow();
        delivery.Status = result.Delivered ? "delivered" : "failed";
        var attempt = WebhookAttempts.Describe(delivery, TestEventType, result, delivery.UpdatedAt, elapsed, isTest: true);
        await repository.RecordTestAsync(delivery, attempt, result.Delivered ? result.Status : $"Failed: {result.Status}", ct);
        return WebhookAttempts.Map(attempt);
    }
}
