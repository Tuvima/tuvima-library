namespace MediaEngine.Contracts.Progress;

/// <summary>
/// Response body for the <c>/progress</c> endpoints. Property names are
/// byte-identical to the anonymous type this record replaces — no
/// <c>[JsonPropertyName]</c> needed.
/// </summary>
public sealed record UserStateResponse(
    [property: global::System.Text.Json.Serialization.JsonPropertyName("user_id")] Guid UserId,
    [property: global::System.Text.Json.Serialization.JsonPropertyName("asset_id")] Guid AssetId,
    [property: global::System.Text.Json.Serialization.JsonPropertyName("content_hash")] string ContentHash,
    [property: global::System.Text.Json.Serialization.JsonPropertyName("progress_pct")] double ProgressPct,
    [property: global::System.Text.Json.Serialization.JsonPropertyName("last_accessed")] DateTime? LastAccessed,
    [property: global::System.Text.Json.Serialization.JsonPropertyName("extended_properties")] Dictionary<string, string> ExtendedProperties)
{
    public long Revision { get; init; }
}

/// <summary>Request body for creating or replacing an asset progress state.</summary>
public sealed record ProgressUpdateRequest(
    [property: global::System.Text.Json.Serialization.JsonPropertyName("user_id")] string? UserId,
    [property: global::System.Text.Json.Serialization.JsonPropertyName("progress_pct")] double ProgressPct,
    [property: global::System.Text.Json.Serialization.JsonPropertyName("extended_properties")] Dictionary<string, string>? ExtendedProperties)
{
    [global::System.Text.Json.Serialization.JsonPropertyName("expected_revision")]
    public long ExpectedRevision { get; init; }

    /// <summary>
    /// True when a paired app is syncing progress it recorded while offline. With a <see cref="ClientTimestamp"/>,
    /// a stale <see cref="ExpectedRevision"/> no longer rejects the write: the later of the server's last update and
    /// the client's timestamp wins for that asset. Without a timestamp the flag changes nothing.
    /// </summary>
    [global::System.Text.Json.Serialization.JsonPropertyName("offline")]
    public bool Offline { get; init; }

    /// <summary>
    /// When the progress was recorded on the device (ISO 8601, UTC). Only read when <see cref="Offline"/> is true.
    /// The Engine treats a time later than its own clock as its own current time.
    /// </summary>
    [global::System.Text.Json.Serialization.JsonPropertyName("client_timestamp")]
    public DateTimeOffset? ClientTimestamp { get; init; }
}
