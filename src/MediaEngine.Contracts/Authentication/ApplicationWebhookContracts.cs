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
    [property: JsonPropertyName("last_success_at")] DateTimeOffset? LastSuccessAt);

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
