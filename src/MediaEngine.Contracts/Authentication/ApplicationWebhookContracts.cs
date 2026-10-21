using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Authentication;

public sealed record ApplicationWebhookResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("application_id")] Guid ApplicationId,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("event_types")] IReadOnlyList<string> EventTypes,
    [property: JsonPropertyName("allow_local_network")] bool AllowLocalNetwork,
    [property: JsonPropertyName("is_enabled")] bool IsEnabled,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("last_status")] string LastStatus,
    [property: JsonPropertyName("last_attempt_at")] DateTimeOffset? LastAttemptAt,
    [property: JsonPropertyName("last_success_at")] DateTimeOffset? LastSuccessAt,
    [property: JsonPropertyName("consecutive_failures")] int ConsecutiveFailures = 0);

public sealed record SaveApplicationWebhookRequest(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("event_types")] IReadOnlyList<string> EventTypes,
    [property: JsonPropertyName("allow_local_network")] bool AllowLocalNetwork,
    [property: JsonPropertyName("is_enabled")] bool IsEnabled,
    [property: JsonPropertyName("expected_version")] long? ExpectedVersion,
    [property: JsonPropertyName("approval_hash")] string? ApprovalHash = null);

public sealed record ApplicationWebhookSecretResponse(
    [property: JsonPropertyName("webhook")] ApplicationWebhookResponse Webhook,
    [property: JsonPropertyName("signing_secret")] string? SigningSecret);

/// <summary>
/// One attempt to send an event to a webhook receiver. <c>result</c> is short plain text ("HTTP 503", "Receiver unavailable");
/// it never carries the event body, the signing secret or anything the receiver answered with.
/// </summary>
public sealed record ApplicationWebhookAttemptResponse(
    [property: JsonPropertyName("attempt_id")] Guid AttemptId,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("attempt_number")] int AttemptNumber,
    [property: JsonPropertyName("attempted_at")] DateTimeOffset AttemptedAt,
    [property: JsonPropertyName("delivered")] bool Delivered,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("is_test")] bool IsTest);
