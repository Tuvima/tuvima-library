namespace MediaEngine.Domain.Enums;

/// <summary>
/// Identifies the type of domain entity that a <see cref="Entities.MetadataClaim"/>,
/// <see cref="Entities.CanonicalValue"/>, or harvest request targets.
///
/// Used by the external provider pipeline (Phase 9) to route requests to the
/// correct adapter and to interpret returned claims.
/// </summary>
public enum EntityType
{
    /// <summary>A <c>Work</c> aggregate (e.g. a book title or film franchise entry).</summary>
    Work,

    /// <summary>An <c>Edition</c> aggregate (e.g. a specific print or release).</summary>
    Edition,

    /// <summary>A <see cref="Entities.Person"/> entity (author, narrator, director).</summary>
    Person,

    /// <summary>A <c>MediaAsset</c> aggregate (a single file on disk).</summary>
    MediaAsset,

    /// <summary>A fictional character in a narrative universe.</summary>
    Character,

    /// <summary>A fictional location or world in a narrative universe.</summary>
    Location,

    /// <summary>A fictional organization or faction in a narrative universe.</summary>
    Organization,

    /// <summary>A narrative event in a fictional universe.</summary>
    Event,

    /// <summary>An important fictional object or artifact in a narrative universe.</summary>
    Object,

    /// <summary>A fictional species or race in a narrative universe.</summary>
    Species,

    /// <summary>A lore concept or glossary term in a narrative universe.</summary>
    Concept,
}
