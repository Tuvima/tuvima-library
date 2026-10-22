namespace MediaEngine.Domain.Entities;

/// <summary>
/// A typed image asset belonging to any entity in the library
/// (Work, Person, Universe, or FictionalEntity).
///
/// The <see cref="AssetTypeValue"/> classifies the image (CoverArt, Headshot,
/// Logo, Background). Multiple assets of the same type may exist per entity
/// (e.g. three different movie posters); one is marked as preferred.
///
/// Maps 1:1 to a row in the <c>entity_assets</c> table.
/// </summary>
public sealed class EntityAsset
{
    /// <summary>Stable row identifier (UUID → TEXT in SQLite).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The entity this asset belongs to (work ID, person ID, universe QID, or entity ID).
    /// Internal entity IDs are persisted as GUID/BLOB values; external identifiers stay as text.
    /// </summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>
    /// The type of entity: <c>"Work"</c>, <c>"Person"</c>, <c>"Universe"</c>,
    /// or <c>"FictionalEntity"</c>.
    /// </summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>
    /// The asset classification. One of: <c>"CoverArt"</c>, <c>"Headshot"</c>,
    /// <c>"Logo"</c>, <c>"Background"</c>.
    /// </summary>
    public string AssetTypeValue { get; set; } = string.Empty;

    /// <summary>Remote URL of the image (e.g. TMDB image CDN, Wikimedia Commons).</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Local filesystem path to the downloaded image.</summary>
    public string? LocalImagePath { get; set; }

    /// <summary>Tiny face/thumbnail rendition path (96px long edge).</summary>
    public string? LocalImagePathTiny { get; set; }

    /// <summary>Small shelf/thumbnail rendition path (typically 320px long edge).</summary>
    public string? LocalImagePathSmall { get; set; }

    /// <summary>Medium library/hover rendition path (typically 960px long edge).</summary>
    public string? LocalImagePathMedium { get; set; }

    /// <summary>Large zoom/detail rendition path (original or capped to 2160px long edge).</summary>
    public string? LocalImagePathLarge { get; set; }

    /// <summary>
    /// Which provider supplied this asset.
    /// Example: <c>"tmdb"</c>, <c>"wikidata"</c>, <c>"user_upload"</c>.
    /// </summary>
    public string? SourceProvider { get; set; }

    /// <summary>Measured pixel width of the source artwork.</summary>
    public int? WidthPx { get; set; }

    /// <summary>Measured pixel height of the source artwork.</summary>
    public int? HeightPx { get; set; }

    /// <summary>
    /// Normalized aspect classification for UI surface selection.
    /// One of Portrait, Square, LandscapeWide, BannerStrip, or UnsupportedRect.
    /// </summary>
    public string AspectClass { get; set; } = ArtworkAspectClasses.UnsupportedRect;

    /// <summary>Primary extracted palette hex used for generated gradients.</summary>
    public string? PrimaryHex { get; set; }

    /// <summary>Secondary extracted palette hex used for generated gradients.</summary>
    public string? SecondaryHex { get; set; }

    /// <summary>Accent extracted palette hex used for generated gradients.</summary>
    public string? AccentHex { get; set; }

    /// <summary>Broad classification for the asset record.</summary>
    public string AssetClassValue { get; set; } = "Artwork";

    /// <summary>Current storage location for the managed file.</summary>
    public string StorageLocationValue { get; set; } = "Central";

    /// <summary>Human-readable ownership scope such as Series, Season, Album, or Work.</summary>
    public string OwnerScope { get; set; } = "Unknown";

    /// <summary>Whether this is the preferred asset for its entity + type combination.</summary>
    public bool IsPreferred { get; set; }

    /// <summary>Whether the user explicitly chose this asset (overrides auto-selection).</summary>
    public bool IsUserOverride { get; set; }

    /// <summary>Whether the preferred asset has been mirrored to a local export path.</summary>
    public bool IsLocallyExported { get; set; }

    /// <summary>Whether this variant is the currently exported preferred mirror.</summary>
    public bool IsPreferredExported { get; set; }

    /// <summary>When this asset record was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this asset was last updated.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}
