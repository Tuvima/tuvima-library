using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// A performer credit attached to a fictional character.
/// </summary>
public sealed record CharacterPerformerCredit(
    Guid PersonId,
    Guid FictionalEntityId,
    string? WorkQid,
    string PerformerName,
    string? HeadshotUrl,
    string? LocalHeadshotPath);

/// <summary>
/// Persistence contract for <see cref="Person"/> records and their links
/// to media assets.
///
/// Implementations live in <c>MediaEngine.Storage</c>.
/// </summary>
public interface IPersonRepository
{
    /// <summary>
    /// Finds a person by name.
    /// Returns <c>null</c> if no matching person exists.
    /// Comparison is case-insensitive.
    /// </summary>
    /// <param name="name">The person's display name.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Person?> FindByNameAsync(
        string name,
        CancellationToken ct = default);

    /// <summary>Finds all people matching the supplied names in bounded batches.</summary>
    Task<IReadOnlyList<Person>> FindByNamesAsync(
        IEnumerable<string> names,
        CancellationToken ct = default);

    /// <summary>
    /// Adds a role to the given person in the <c>person_roles</c> junction table.
    /// Idempotent — duplicate (person_id, role) pairs are silently ignored.
    /// </summary>
    Task AddRoleAsync(Guid personId, string role, CancellationToken ct = default);

    /// <summary>
    /// Returns all roles for the given person from the <c>person_roles</c> junction table.
    /// </summary>
    Task<IReadOnlyList<string>> GetRolesAsync(Guid personId, CancellationToken ct = default);

    /// <summary>
    /// Returns the count of distinct persons per role across all person_roles entries.
    /// </summary>
    Task<Dictionary<string, int>> GetRoleCountsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the count of distinct people per canonical primary-credit role
    /// across owned works.
    /// </summary>
    Task<Dictionary<string, int>> GetCatalogRoleCountsAsync(CancellationToken ct = default) =>
        GetRoleCountsAsync(ct);

    /// <summary>
    /// Returns per-person media type counts (e.g. how many Books, Movies, etc. each person
    /// is linked to). Used by people surfaces for library presence display.
    /// </summary>
    Task<Dictionary<Guid, Dictionary<string, int>>> GetPresenceBatchAsync(
        IEnumerable<Guid> personIds,
        CancellationToken ct = default);

    /// <summary>
    /// Inserts a new person record and returns it with any DB-generated fields populated.
    /// </summary>
    /// <param name="person">The person to create.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Person> CreateAsync(
        Person person,
        CancellationToken ct = default);

    /// <summary>
    /// Updates the Wikidata enrichment fields for an existing person and sets
    /// <see cref="Person.EnrichedAt"/> to <see cref="DateTimeOffset.UtcNow"/>.
    /// </summary>
    /// <param name="personId">The person to update.</param>
    /// <param name="wikidataQid">The Wikidata Q-identifier (e.g. <c>"Q42"</c>), or <c>null</c> to clear.</param>
    /// <param name="headshotUrl">The Wikimedia Commons image URL, or <c>null</c> to clear.</param>
    /// <param name="biography">The Wikidata entity description, or <c>null</c> to clear.</param>
    /// <param name="name">The resolved display name from Wikidata, or <c>null</c> to leave unchanged.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateEnrichmentAsync(
        Guid personId,
        string? wikidataQid,
        string? headshotUrl,
        string? biography,
        string? name,
        CancellationToken ct = default);

    /// <summary>
    /// Replaces only the person's display name. Used when a structured
    /// relationship already supplies a trustworthy label before the person's
    /// full profile hydration completes.
    /// </summary>
    Task UpdateNameAsync(
        Guid personId,
        string name,
        CancellationToken ct = default);

    /// <summary>
    /// Updates the social media and contact fields for an existing person.
    /// Called after Wikidata enrichment returns P2003/P2002/P7085/P4033/P856/P106 claims.
    /// Only non-null values are written; null parameters leave existing values unchanged.
    /// </summary>
    Task UpdateSocialFieldsAsync(
        Guid personId,
        string? occupation,
        string? instagram,
        string? twitter,
        string? tiktok,
        string? mastodon,
        string? website,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a link between a media asset and a person in the
    /// <c>person_media_links</c> table.
    /// If the link already exists the call is a no-op (INSERT OR IGNORE).
    /// </summary>
    /// <param name="mediaAssetId">The media asset.</param>
    /// <param name="personId">The person.</param>
    /// <param name="role">The role the person plays in this asset.</param>
    /// <param name="ct">Cancellation token.</param>
    Task LinkToMediaAssetAsync(
        Guid mediaAssetId,
        Guid personId,
        string role,
        CancellationToken ct = default);

    /// <summary>
    /// Updates the <c>local_headshot_path</c> column for the given person.
    /// Called after a headshot is downloaded to disk.
    /// </summary>
    Task UpdateLocalHeadshotPathAsync(
        Guid id,
        string path,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the people who have a saved face file (<c>local_headshot_path</c>) but no managed Headshot picture,
    /// so the file can be registered with its sized copies. Roles are not loaded.
    /// </summary>
    Task<IReadOnlyList<Person>> GetWithLocalHeadshotWithoutManagedAssetAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a single person by ID, or <c>null</c> if not found.
    /// </summary>
    Task<Person?> FindByIdAsync(
        Guid id,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all persons linked to a given media asset.
    /// </summary>
    /// <param name="mediaAssetId">The media asset whose persons to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<Person>> GetByMediaAssetAsync(
        Guid mediaAssetId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all persons linked to any of the given media assets in one query.
    /// </summary>
    Task<IReadOnlyList<Person>> GetByMediaAssetsAsync(
        IEnumerable<Guid> mediaAssetIds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all person records in the database.
    /// Used by the reconciliation service for orphan detection.
    /// </summary>
    Task<IReadOnlyList<Person>> ListAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a bounded person page, optionally filtered by role.
    /// </summary>
    Task<IReadOnlyList<Person>> ListPagedAsync(
        string? role,
        int offset,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a bounded presentation catalog page containing only people with
    /// canonical primary credits on owned works.
    /// </summary>
    Task<IReadOnlyList<Person>> ListCatalogPagedAsync(
        string? search,
        string? role,
        int offset,
        int limit,
        string? lane = null,
        string? sort = null,
        CancellationToken ct = default) =>
        ListPagedAsync(role, offset, limit, ct);

    /// <summary>
    /// Returns the number of media asset links for a given person.
    /// Used by the reconciliation service to detect orphaned persons
    /// (persons with zero linked media assets).
    /// </summary>
    Task<int> CountMediaLinksAsync(Guid personId, CancellationToken ct = default);

    /// <summary>
    /// Returns the number of durable graph references that keep a person in the
    /// library. Unlike media-link counting, this includes group membership,
    /// aliases, and character relationships created during enrichment.
    /// </summary>
    Task<int> CountGraphReferencesAsync(Guid personId, CancellationToken ct = default);

    /// <summary>
    /// Finds a person by Wikidata QID.
    /// Returns <c>null</c> if no matching person exists.
    /// Used by QID-first person enrichment and reconciliation.
    /// </summary>
    Task<Person?> FindByQidAsync(string qid, CancellationToken ct = default);

    /// <summary>
    /// Finds all people matching the supplied Wikidata QIDs in bounded batches.
    /// Returned people include their stored roles.
    /// </summary>
    Task<IReadOnlyList<Person>> FindByQidsAsync(
        IEnumerable<string> qids,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a person record and all associated media links.
    /// Used by the reconciliation service when cleaning orphaned persons.
    /// </summary>
    Task DeleteAsync(Guid personId, CancellationToken ct = default);

    /// <summary>
    /// Updates biographical fields for a person record.
    /// Called after Wikidata enrichment returns birth/death/nationality data.
    /// </summary>
    Task UpdateBiographicalFieldsAsync(
        Guid personId,
        string? dateOfBirth,
        string? dateOfDeath,
        string? placeOfBirth,
        string? placeOfDeath,
        string? nationality,
        bool isPseudonym,
        bool isGroup = false,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a pseudonym alias link between a pen name person and a real person.
    /// Idempotent — duplicate links are ignored.
    /// </summary>
    Task LinkAliasAsync(
        Guid pseudonymPersonId,
        Guid realPersonId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all alias-linked persons for the given person ID.
    /// Works bidirectionally: returns real people behind a pseudonym
    /// AND pseudonyms used by a real person.
    /// </summary>
    Task<IReadOnlyList<Person>> FindAliasesAsync(
        Guid personId,
        CancellationToken ct = default);

    /// <summary>
    /// Creates a character-performer link between a person (actor) and a
    /// fictional entity (character) for a specific work.
    /// Idempotent — duplicate links are ignored.
    /// </summary>
    Task LinkToCharacterAsync(
        Guid personId,
        Guid fictionalEntityId,
        string? workQid,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all character links for a given performer person.
    /// Each entry contains the fictional entity ID and optional work QID.
    /// </summary>
    Task<IReadOnlyList<(Guid FictionalEntityId, string? WorkQid)>> GetCharacterLinksAsync(
        Guid personId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all character-performer links for a given work QID.
    /// Each entry contains the person ID and fictional entity ID.
    /// </summary>
    Task<IReadOnlyList<(Guid PersonId, Guid FictionalEntityId)>> GetCharacterLinksByWorkAsync(
        string workQid,
        CancellationToken ct = default);

    /// <summary>
    /// Returns performer credits for all supplied fictional entities in one database read.
    /// Internal identifiers remain typed GUIDs throughout the query and result.
    /// </summary>
    Task<IReadOnlyList<CharacterPerformerCredit>> GetCharacterPerformersAsync(
        IEnumerable<Guid> fictionalEntityIds,
        CancellationToken ct = default);

    /// <summary>
    /// Reassigns all media links, character links, and alias links from one person
    /// to another. Used during QID-based deduplication. Idempotent.
    /// </summary>
    Task ReassignAllLinksAsync(
        Guid fromPersonId,
        Guid toPersonId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns <c>true</c> if the given person is a pseudonym (pen name) or is
    /// already linked via the <c>person_aliases</c> table (either as the pseudonym
    /// side or the real-person side).
    ///
    /// Used during QID-based deduplication to prevent erroneously merging a pen
    /// name record (e.g. "James S.A. Corey") into one of its real authors.
    /// </summary>
    Task<bool> IsPseudonymOrAliasAsync(Guid personId, CancellationToken ct = default);

    /// <summary>
    /// Links a member person to a group person in the <c>person_group_members</c> table.
    /// Idempotent — duplicate (group_id, member_id) pairs are silently ignored.
    /// </summary>
    /// <param name="groupId">The group person (e.g. Metallica).</param>
    /// <param name="memberId">The member person (e.g. James Hetfield).</param>
    /// <param name="ct">Cancellation token.</param>
    Task LinkGroupMemberAsync(Guid groupId, Guid memberId, CancellationToken ct = default);
}
