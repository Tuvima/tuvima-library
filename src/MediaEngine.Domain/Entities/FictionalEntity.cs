namespace MediaEngine.Domain.Entities;

/// <summary>
/// A fictional character, location, or organization belonging to a narrative universe.
/// Unified entity — the <see cref="EntitySubType"/> discriminator distinguishes the kind.
///
/// Identified by <see cref="WikidataQid"/> (UNIQUE) across all media in the library.
/// Multiple works can reference the same entity via the <c>fictional_entity_work_links</c>
/// junction table.
///
/// Maps 1:1 to a row in the <c>fictional_entities</c> table.
/// </summary>
public sealed class FictionalEntity
{
    /// <summary>Stable row identifier (UUID → TEXT in SQLite).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The Wikidata Q-identifier for this entity.
    /// Example: <c>"Q937618"</c> (Paul Atreides).
    /// UNIQUE constraint in the database — QID is the canonical identity.
    /// </summary>
    public string WikidataQid { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable label for display.
    /// Example: <c>"Paul Atreides"</c>, <c>"Arrakis"</c>, <c>"House Atreides"</c>.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Short description of the entity.
    /// Example: <c>"Protagonist of Dune, heir to House Atreides"</c>.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Discriminator: <c>"Character"</c>, <c>"Location"</c>, <c>"Organization"</c>, <c>"Event"</c>, <c>"Object"</c>, <c>"Species"</c> or <c>"Concept"</c>.
    /// Enforced by a CHECK constraint in the database.
    /// </summary>
    public string EntitySubType { get; set; } = string.Empty;

    /// <summary>
    /// The Wikidata QID of the fictional universe this entity belongs to.
    /// Example: <c>"Q3041974"</c> (Dune universe).
    /// Used as the grouping key for the universe graph.
    /// </summary>
    public string? FictionalUniverseQid { get; set; }

    /// <summary>
    /// Human-readable label for the fictional universe.
    /// Example: <c>"Dune universe"</c>.
    /// </summary>
    public string? FictionalUniverseLabel { get; set; }

    /// <summary>
    /// URL to a representative image (e.g. Wikimedia Commons).
    /// Character-specific local portraits are stored separately in character portrait assets.
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// Local filesystem path to a downloaded image, if any.
    /// </summary>
    public string? LocalImagePath { get; set; }

    /// <summary>When this entity record was first created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When this entity was last enriched from Wikidata SPARQL.
    /// <c>null</c> means the entity has been created (from a work's claims)
    /// but not yet deeply enriched with its own properties and relationships.
    /// </summary>
    public DateTimeOffset? EnrichedAt { get; set; }

    /// <summary>Wikidata <c>lastrevid</c> for Lore Delta change detection.</summary>
    public long? WikidataRevisionId { get; set; }

    /// <summary>
    /// True for a thing a person added by hand (Universe contents spec P4). Its <see cref="WikidataQid"/> is a local ID
    /// (<see cref="LocalIdPrefix"/> + a GUID); no provider ever overwrites it and it is never sent anywhere.
    /// </summary>
    public bool AddedByUser { get; set; }

    /// <summary>Prefix of the local ID given to a hand-added thing in place of a Wikidata ID.</summary>
    public const string LocalIdPrefix = "local:";

    /// <summary>Builds a fresh local ID for a hand-added thing.</summary>
    public static string NewLocalId() => LocalIdPrefix + Guid.NewGuid().ToString("N");
}
