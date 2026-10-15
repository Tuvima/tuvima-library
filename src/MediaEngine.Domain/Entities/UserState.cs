namespace MediaEngine.Domain.Entities;

/// <summary>
/// Records a user's interaction progress with a specific <see cref="Aggregates.MediaAsset"/>.
///
/// Spec invariants:
/// • "UserState MUST NOT be lost if a MediaAsset is moved to a different file path,
///   provided the file hash remains identical." — reconciliation is performed via
///   <see cref="ContentHash"/>, not the file path.
/// • "UserState MUST allow for extended properties to support unique tracking
///   requirements for different media types." — see <see cref="ExtendedProperties"/>.
///
/// Maps to the composite-primary-key row (<c>user_id</c>, <c>asset_id</c>) in
/// the <c>user_states</c> table.
/// </summary>
public sealed class UserState
{
    public long Revision { get; set; }
    /// <summary>Identity of the user this state belongs to.</summary>
    public Guid UserId { get; set; }

    /// <summary>FK → <c>media_assets.id</c>.</summary>
    public Guid AssetId { get; set; }

    /// <summary>
    /// Content hash of the asset at the time this state was created.
    /// Used by the storage layer to re-link state after a file move
    /// (spec: Hash Dominance / Progress Persistence invariant).
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Completion percentage in the range 0.0 – 100.0.
    /// Interpretation is media-type-specific (e.g. % of runtime vs. % of pages).
    /// </summary>
    public double ProgressPct { get; set; }

    /// <summary>When the user last accessed this asset.</summary>
    public DateTimeOffset LastAccessed { get; set; }

    /// <summary>
    /// Open-ended key-value bag for media-type-specific tracking data.
    /// Examples: <c>"last_page_read"</c>, <c>"last_chapter"</c>, <c>"playback_timestamp_ms"</c>.
    /// Spec: "MUST allow for extended properties."
    /// </summary>
    public Dictionary<string, string> ExtendedProperties { get; set; } = [];

    /// <summary>
    /// Not stored on <c>user_states</c>. A deterministic key for the consumption-history entry an offline replay writes
    /// when the state carries no player or reader session of its own: replaying the same play again finds the same
    /// key, so the history keeps one entry.
    /// </summary>
    public string? HistorySessionKey { get; set; }
}
