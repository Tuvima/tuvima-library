using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Playback;

/// <summary>What kind of thing a paired app reports as stored on its device.</summary>
public static class DeviceDownloadKinds
{
    /// <summary>A prepared offline variant of a catalogue asset (the default when a request names no kind).</summary>
    public const string Variant = "variant";

    /// <summary>A photo or video from the person's own View, kept as a 2048 preview image.</summary>
    public const string Photo = "photo";

    /// <summary>Reserved for books kept on the device. The Engine refuses it until books are offered in the native apps.</summary>
    public const string Book = "book";
}

/// <summary>One item (an offline variant or a photo) a paired app reports as stored on its device.</summary>
public sealed class DeviceDownloadDto
{
    /// <summary>The catalogue asset of a <c>variant</c> entry; the empty id for any other kind.</summary>
    [JsonPropertyName("asset_id")] public Guid AssetId { get; init; }

    /// <summary>The offline variant of a <c>variant</c> entry; the empty id for any other kind.</summary>
    [JsonPropertyName("variant_id")] public Guid VariantId { get; init; }

    /// <summary><c>variant</c> or <c>photo</c> (see <see cref="DeviceDownloadKinds"/>).</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = DeviceDownloadKinds.Variant;

    /// <summary>The View item id of a <c>photo</c> entry; null for a variant.</summary>
    [JsonPropertyName("photo_item_id")] public Guid? PhotoItemId { get; init; }

    /// <summary>Bytes the device reports as stored (clamped to the variant's size on the server).</summary>
    [JsonPropertyName("stored_bytes")] public long StoredBytes { get; init; }

    /// <summary>When the device finished storing the variant.</summary>
    [JsonPropertyName("stored_at")] public DateTimeOffset StoredAt { get; init; }

    /// <summary>When the Engine will remove the variant (and so this entry); null when it has no expiry (always null for a photo).</summary>
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>The downloads the calling paired app has reported for its current profile.</summary>
public sealed class DeviceDownloadListDto
{
    [JsonPropertyName("downloads")] public IReadOnlyList<DeviceDownloadDto> Downloads { get; init; } = [];
}

/// <summary>A paired app reporting (or updating) one stored offline variant or photo. The device comes from the token.</summary>
public sealed class UpsertDeviceDownloadRequest
{
    /// <summary>Required for a <c>variant</c>; ignored for a photo.</summary>
    [JsonPropertyName("asset_id")] public Guid AssetId { get; init; }

    /// <summary>Required for a <c>variant</c>; ignored for a photo.</summary>
    [JsonPropertyName("variant_id")] public Guid VariantId { get; init; }

    /// <summary><c>variant</c> (the default when omitted) or <c>photo</c>. <c>book</c> is reserved and, like any other value, is refused with 400.</summary>
    [JsonPropertyName("kind")] public string? Kind { get; init; }

    /// <summary>The View item id; required for a <c>photo</c>, ignored for a variant.</summary>
    [JsonPropertyName("photo_item_id")] public Guid? PhotoItemId { get; init; }

    [JsonPropertyName("stored_bytes")] public long StoredBytes { get; init; }
    [JsonPropertyName("stored_at")] public DateTimeOffset StoredAt { get; init; }
}
