using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Playback;

/// <summary>One offline variant a paired app reports as stored on its device.</summary>
public sealed class DeviceDownloadDto
{
    [JsonPropertyName("asset_id")] public Guid AssetId { get; init; }
    [JsonPropertyName("variant_id")] public Guid VariantId { get; init; }

    /// <summary>Bytes the device reports as stored (clamped to the variant's size on the server).</summary>
    [JsonPropertyName("stored_bytes")] public long StoredBytes { get; init; }

    /// <summary>When the device finished storing the variant.</summary>
    [JsonPropertyName("stored_at")] public DateTimeOffset StoredAt { get; init; }

    /// <summary>When the Engine will remove the variant (and so this entry); null when it has no expiry.</summary>
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>The downloads the calling paired app has reported for its current profile.</summary>
public sealed class DeviceDownloadListDto
{
    [JsonPropertyName("downloads")] public IReadOnlyList<DeviceDownloadDto> Downloads { get; init; } = [];
}

/// <summary>A paired app reporting (or updating) one stored offline variant. The device comes from the token.</summary>
public sealed class UpsertDeviceDownloadRequest
{
    [JsonPropertyName("asset_id")] public Guid AssetId { get; init; }
    [JsonPropertyName("variant_id")] public Guid VariantId { get; init; }
    [JsonPropertyName("stored_bytes")] public long StoredBytes { get; init; }
    [JsonPropertyName("stored_at")] public DateTimeOffset StoredAt { get; init; }
}
