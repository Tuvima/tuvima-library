using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Authentication;

/// <summary>
/// The rules behind an integration's health word (Developer tools spec D-D, D-W). The Engine and the Dashboard use the same
/// numbers, so the page and the menu always agree. Only repeated failure shades the menu red: one failed delivery is a bad moment,
/// not a broken integration.
/// </summary>
public static class IntegrationHealthRules
{
    /// <summary>A key nobody has used for this long needs a look: it may belong to something that is gone.</summary>
    public const int UnusedKeyDays = 90;

    /// <summary>A key that expires within this many days shows Needs attention.</summary>
    public const int KeyExpiryWarningDays = 14;

    /// <summary>The expiry suggested when a key is issued.</summary>
    public const int SuggestedKeyLifetimeDays = 365;

    /// <summary>A webhook that failed this many deliveries in a row shows Needs attention.</summary>
    public const int WebhookAttentionFailures = 3;

    /// <summary>A webhook that failed this many deliveries in a row is Broken: it is switched on and cannot do its job.</summary>
    public const int WebhookBrokenFailures = 5;

    public static bool KeyUnused(DateTimeOffset createdAt, DateTimeOffset? lastUsedAt, DateTimeOffset now) =>
        (lastUsedAt ?? createdAt) <= now.AddDays(-UnusedKeyDays);

    public static bool KeyNearExpiry(DateTimeOffset? expiresAt, DateTimeOffset now) =>
        expiresAt is { } expires && expires <= now.AddDays(KeyExpiryWarningDays);
}

/// <summary>What the Settings menu needs to colour the Applications row, without loading every application and webhook.</summary>
public sealed record IntegrationHealthResponse(
    [property: JsonPropertyName("broken_webhooks")] int BrokenWebhooks,
    [property: JsonPropertyName("webhooks_needing_attention")] int WebhooksNeedingAttention,
    [property: JsonPropertyName("keys_needing_attention")] int KeysNeedingAttention);
