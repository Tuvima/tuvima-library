namespace MediaEngine.Domain.Constants;

/// <summary>
/// String constants for relationship types stored in the <c>entity_relationships</c> table.
/// Each constant maps to a Wikidata property that defines a graph edge between
/// two entities in the universe graph.
/// </summary>
public static class RelationshipType
{
    // ── Character → Character ────────────────────────────────────────────

    /// <summary>P22 — Father relationship (parent → child).</summary>
    public const string Father = "father";

    /// <summary>P25 — Mother relationship (parent → child).</summary>
    public const string Mother = "mother";

    /// <summary>P26 — Spouse/partner relationship.</summary>
    public const string Spouse = "spouse";

    /// <summary>P3373 — Sibling relationship.</summary>
    public const string Sibling = "sibling";

    /// <summary>P40 — Child relationship (parent → child).</summary>
    public const string Child = "child";

    /// <summary>P1344 — Participant in: a character took part in an event (the reverse of <see cref="Participant"/>).</summary>
    public const string ParticipantIn = "participant_in";

    /// <summary>P7047 — Enemy (a character's declared enemy).</summary>
    public const string Enemy = "enemy";

    /// <summary>P1066 — Student-of / mentor relationship.</summary>
    public const string StudentOf = "student_of";

    /// <summary>P451 — Romantic partner (non-marriage).</summary>
    public const string Partner = "partner";

    // ── Character/Location → Organization ────────────────────────────────

    /// <summary>P463 — Organization membership.</summary>
    public const string MemberOf = "member_of";

    /// <summary>P945 — Military or political allegiance.</summary>
    public const string Allegiance = "allegiance";

    // ── Character/Entity → Location ──────────────────────────────────────

    /// <summary>P551 — Where a character resides.</summary>
    public const string Residence = "residence";

    /// <summary>P69 — Educational institution attended.</summary>
    public const string EducatedAt = "educated_at";

    /// <summary>P131 — Located in the administrative territorial entity.</summary>
    public const string LocatedIn = "located_in";

    /// <summary>P361 — Part-of relationship (sub-location or sub-organization).</summary>
    public const string PartOf = "part_of";

    // ── Organization ─────────────────────────────────────────────────────

    /// <summary>P169 — Chief executive officer. This must not be generalized into fictional leadership.</summary>
    public const string ChiefExecutiveOfficer = "chief_executive_officer";

    /// <summary>P749 — Parent organization.</summary>
    public const string ParentOrganization = "parent_organization";

    /// <summary>P527 — Organization has parts/members.</summary>
    public const string HasParts = "has_parts";

    // ── Cross-type ───────────────────────────────────────────────────────

    /// <summary>P170 — Creator of a fictional character.</summary>
    public const string Creator = "creator";

    /// <summary>P39 — Position held (e.g. King of Gondor).</summary>
    public const string PositionHeld = "position_held";

    /// <summary>P607 — Involved in a military or narrative conflict.</summary>
    public const string Conflict = "conflict";

    /// <summary>P175 — Performer (actor who portrays a character).</summary>
    public const string Performer = "performer";

    /// <summary>Two entities with different QIDs that represent the same concept.</summary>
    public const string SameAs = "same_as";

    // ── Social Web ─────────────────────────────────────────────────────

    /// <summary>P3342 — Significant person (ally, rival, mentor, etc.).</summary>
    public const string SignificantPerson = "significant_person";

    /// <summary>P1416 — Affiliation with a group or organization.</summary>
    public const string Affiliation = "affiliation";

    // ── Event ───────────────────────────────────────────────────────────

    /// <summary>P276 — Location where an event occurs.</summary>
    public const string OccursAt = "occurs_at";

    /// <summary>P710 — Participant in an event.</summary>
    public const string Participant = "participant";

    /// <summary>P828 — Event or entity that caused this event.</summary>
    public const string CausedBy = "caused_by";

    /// <summary>P1542 — Event or entity caused by this event.</summary>
    public const string Causes = "causes";

    // ── Adaptation ─────────────────────────────────────────────────────

    /// <summary>P144 — Work is based on another work (adaptation source).</summary>
    public const string BasedOn = "based_on";

    /// <summary>P4969 — Work is a derivative of another work.</summary>
    public const string DerivativeWork = "derivative_work";

    /// <summary>P941 — Work is inspired by another work or entity.</summary>
    public const string InspiredBy = "inspired_by";
}
