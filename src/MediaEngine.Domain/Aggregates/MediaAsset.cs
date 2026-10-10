using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Aggregates;

/// <summary>
/// A single physical media file (or a group of files unified by a
/// <see cref="MediaManifest"/>) as it exists on the local file system.
///
/// <see cref="ContentHash"/> is the primary identity key across moves:
/// user progress MUST be preserved when a file is relocated,
/// provided the hash is unchanged.
/// Spec: Phase 2 – Invariants § Asset Integrity and Progress Persistence.
///
/// Maps to <c>media_assets</c> in the Phase 4 schema.
/// </summary>
public sealed class MediaAsset
{
    /// <summary>Stable identifier. PK in <c>media_assets</c>.</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// FK → <c>editions.id</c>.
    /// Spec invariant: "A MediaAsset MUST have a verified hash before it is
    /// linked to an Edition."  Verification happens at the application layer.
    /// </summary>
    public Guid EditionId { get; set; }

    /// <summary>
    /// Content-addressable hash (e.g. SHA-256) of the media file.
    /// UNIQUE in the database.  The reconciliation anchor for <see cref="UserState"/>.
    /// Spec: Phase 4 – Hash Dominance invariant.
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Root path on the local file system where this asset resides.
    /// Full binary data is NEVER stored in the database.
    /// Spec: Phase 4 – Binary Storage constraint.
    /// </summary>
    public string FilePathRoot { get; set; } = string.Empty;

    /// <summary>
    /// Current lifecycle status of this asset.
    /// Assets in <see cref="AssetStatus.Conflicted"/> state MUST NOT be
    /// automatically assigned to a Collection.
    /// </summary>
    public AssetStatus Status { get; set; } = AssetStatus.Normal;

    /// <summary>
    /// Present when this asset consists of more than one physical file
    /// (e.g. multi-disc movie, split audiobook).
    /// Null for single-file assets.
    /// Spec: "MUST be treated as a single MediaAsset via a MediaManifest."
    /// </summary>
    public MediaManifest? Manifest { get; set; }

    /// <summary>
    /// Stable identifier of the logical library that owns this asset's source
    /// path. <see langword="null"/> for rows written before Migration M-085, and
    /// for assets ingested before the <c>ILibraryFolderResolver</c> is wired in.
    /// Side-by-side-with-Plex plan §F: one library can span multiple source
    /// paths, and every asset records which library attributed it.
    /// </summary>
    public string? LibraryId { get; set; }

    /// <summary>
    /// Attributes this asset to another logical library. Library membership is data only:
    /// <see cref="FilePathRoot"/> and the file itself are never touched.
    /// </summary>
    public void AssignToLibrary(string libraryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryId);
        LibraryId = libraryId;
    }

    /// <summary>
    /// Soft-delete flag. Set to <see langword="true"/> by the watcher when a
    /// file disappears from disk (NAS unmount, user reorganised in Plex).
    /// The asset row is retained so user progress and metadata survive; a
    /// background reconciler clears the flag if the same hash reappears
    /// within the grace window.
    /// Side-by-side-with-Plex plan §L.
    /// </summary>
    public bool IsOrphaned { get; set; }

    /// <summary>
    /// UTC timestamp at which <see cref="IsOrphaned"/> was most recently set
    /// to <see langword="true"/>. <see langword="null"/> when the asset has
    /// never been orphaned (or has been reconciled back to normal).
    /// </summary>
    public DateTimeOffset? OrphanedAt { get; set; }

    /// <summary>Delivery intent for this file. It does not define an Edition.</summary>
    public RenditionPurpose RenditionPurpose { get; set; } = RenditionPurpose.Original;

    /// <summary>
    /// Optional source asset for a generated rendition. Source and derived
    /// assets must belong to the same Edition.
    /// </summary>
    public Guid? DerivedFromAssetId { get; set; }

    public string? EncoderProfileVersion { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? BitrateBitsPerSecond { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public string? DynamicRange { get; set; }
    public string? AudioLayout { get; set; }
    public DateTimeOffset? RenditionGeneratedAt { get; set; }
    public string? SourceFingerprint { get; set; }
}
