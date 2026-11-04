namespace MediaEngine.Domain.Constants;

/// <summary>
/// Discriminator for the unified <c>fictional_entities</c> table.
/// Each fictional entity belongs to exactly one sub-type.
/// </summary>
public static class FictionalEntityType
{
    /// <summary>A fictional character (e.g. Paul Atreides, Sherlock Holmes).</summary>
    public const string Character = "Character";

    /// <summary>A fictional location (e.g. Arrakis, Middle-earth, Gotham City).</summary>
    public const string Location = "Location";

    /// <summary>A fictional organization or faction (e.g. House Atreides, S.H.I.E.L.D.).</summary>
    public const string Organization = "Organization";

    /// <summary>A narrative event (e.g. Battle of Helm's Deep, Clone Wars, Infinity War).</summary>
    public const string Event = "Event";

    /// <summary>An important fictional object, artifact, vehicle, device, or document.</summary>
    public const string Object = "Object";

    /// <summary>A fictional species or race (e.g. Fremen, Vulcans, Hobbits).</summary>
    public const string Species = "Species";

    /// <summary>A lore concept or glossary term (e.g. the Force, the Spice Must Flow). Shown only when Fandom Lore is on, or when added by hand.</summary>
    public const string Concept = "Concept";

    /// <summary>All valid sub-type values for CHECK constraint generation.</summary>
    public static readonly IReadOnlyList<string> All = [Character, Location, Organization, Event, Object, Species, Concept];
}
