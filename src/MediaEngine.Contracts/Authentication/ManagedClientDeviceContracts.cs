using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Authentication;

/// <summary>
/// A paired phone, tablet or TV as people see it on the Apps &amp; devices panel. The device-facing
/// <see cref="ClientDeviceDto"/> is for the app itself; this one adds who it belongs to.
/// </summary>
public sealed class ManagedClientDeviceDto
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    [JsonPropertyName("device_name")] public string DeviceName { get; init; } = string.Empty;

    /// <summary>The platform: <c>mobile</c>, <c>television</c>, <c>automotive</c> or <c>web</c>.</summary>
    [JsonPropertyName("platform")] public string Platform { get; init; } = string.Empty;

    [JsonPropertyName("client_name")] public string ClientName { get; init; } = string.Empty;
    [JsonPropertyName("account_id")] public Guid AccountId { get; init; }
    [JsonPropertyName("account_display_name")] public string AccountDisplayName { get; init; } = string.Empty;
    [JsonPropertyName("profile_id")] public Guid ProfileId { get; init; }

    /// <summary>The profile the device was paired with.</summary>
    [JsonPropertyName("profile_name")] public string ProfileName { get; init; } = string.Empty;

    [JsonPropertyName("paired_at")] public DateTimeOffset PairedAt { get; init; }
    [JsonPropertyName("last_seen_at")] public DateTimeOffset LastSeenAt { get; init; }

    /// <summary>The profile a phone backs its photos up to. Empty until phone backup exists.</summary>
    [JsonPropertyName("backs_up_to")] public string? BacksUpTo { get; init; }
}
