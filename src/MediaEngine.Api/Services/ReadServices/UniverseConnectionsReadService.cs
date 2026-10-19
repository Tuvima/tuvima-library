using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Reads how one Universe's characters and works connect: the Cast (characters and the people who play them) and the
/// adaptation relationships between works. The visibility rule is the one every Universe read uses: an entity is
/// listed only when it is linked to a work the requesting profile may see, judged through the authorised display
/// projection (<see cref="IDisplayProjectionReadService"/>). Backs the Universe graph actions
/// (<c>/universe/{qid}/cast</c>, <c>/universe/{qid}/adaptations</c>) and the compact read-only lists on
/// <c>/api/v1/universe/{qid}</c>, so the queries exist once.
/// </summary>
public sealed class UniverseConnectionsReadService(
    IFictionalEntityRepository entities,
    IEntityRelationshipRepository relationships,
    INarrativeRootRepository roots,
    IPersonRepository people,
    IDisplayProjectionReadService display)
{
    /// <summary>Most people listed in the compact Cast.</summary>
    public const int MaxCastMembers = 50;

    /// <summary>Most relationships listed in the compact Adaptations.</summary>
    public const int MaxAdaptations = 50;

    private static readonly HashSet<string> AdaptationTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "based_on",
        "derivative_work",
        "inspired_by",
    };

    /// <summary>Characters in the Universe with their real-world performers (the Universe graph's Cast action).</summary>
    public async Task<UniverseCastResponse> GetCastAsync(string qid, CancellationToken ct = default)
    {
        // Load all Character-type entities in this universe.
        var visibleWorkQids = await LoadVisibleWorkQidsAsync(display, ct).ConfigureAwait(false);
        var allEntities = (await LoadVisibleUniverseScopeAsync(
            qid, entities, visibleWorkQids, ct).ConfigureAwait(false)).Entities;
        var characters = allEntities.Where(e =>
            string.Equals(e.EntitySubType, "Character", StringComparison.OrdinalIgnoreCase)).ToList();

        if (characters.Count == 0)
        {
            return new UniverseCastResponse(universe_qid: qid, characters: Array.Empty<UniverseCastCharacterDto>());
        }

        var allPerformerRows = await people.GetCharacterPerformersAsync(
            characters.Select(character => character.Id),
            ct).ConfigureAwait(false);

        // Group performers by entity UUID for O(1) lookup.
        var performersByEntity = allPerformerRows
            .GroupBy(performer => performer.FictionalEntityId)
            .ToDictionary(grouping => grouping.Key, grouping => grouping.ToList());

        var castList = characters.Select(character =>
        {
            performersByEntity.TryGetValue(character.Id, out var performers);
            return new UniverseCastCharacterDto(
                qid: character.WikidataQid,
                label: character.Label,
                image: character.ImageUrl,
                description: character.Description,
                performers: (performers ?? []).Select(p => new UniverseCastPerformerDto(
                    person_id: p.PersonId,
                    name: p.PerformerName,
                    headshot_url: ApiImageUrls.BuildPersonHeadshotUrl(
                        p.PersonId,
                        p.LocalHeadshotPath,
                        p.HeadshotUrl),
                    work_qid: p.WorkQid,
                    year: (int?)null)));
        }).ToList();

        return new UniverseCastResponse(universe_qid: qid, characters: castList);
    }

    /// <summary>
    /// The adaptation chain as a tree, or null when the narrative root is unknown. With no adaptation relationships
    /// every entity comes back as a flat root (the Universe graph's Adaptations action; the compact list never does that).
    /// </summary>
    public async Task<UniverseAdaptationTreeResponse?> GetAdaptationTreeAsync(string qid, CancellationToken ct = default)
    {
        var root = await roots.FindByQidAsync(qid, ct).ConfigureAwait(false);
        if (root is null)
        {
            return null;
        }

        // Load all entities and adaptation-type relationships.
        var visibleWorkQids = await LoadVisibleWorkQidsAsync(display, ct).ConfigureAwait(false);
        var allEntities = (await LoadVisibleUniverseScopeAsync(
            qid, entities, visibleWorkQids, ct).ConfigureAwait(false)).Entities;
        var adaptationRels = await LoadAdaptationRelationshipsAsync(allEntities, ct).ConfigureAwait(false);

        // Build a lookup by entity QID → work metadata from entities table.
        // Works (not fictional entities) are tracked separately; fall back to entity data.
        var entityByQid = allEntities.ToDictionary(
            e => e.WikidataQid, e => e, StringComparer.OrdinalIgnoreCase);

        // Identify root works (sources that appear only as subjects, never as objects of based_on).
        var childQids = adaptationRels
            .Select(r => r.SubjectQid)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rootWorkQids = adaptationRels
            .Select(r => r.ObjectQid)
            .Where(q => !childQids.Contains(q))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // If no adaptation relationships exist, return all works as flat roots.
        if (adaptationRels.Count == 0)
        {
            var flatWorks = allEntities.Select(e => new UniverseAdaptationNodeDto(
                qid: e.WikidataQid,
                label: e.Label,
                year: (int?)null,
                media_type: e.EntitySubType,
                cover_image: e.ImageUrl,
                relationship_to_parent: (string?)null,
                children: Array.Empty<UniverseAdaptationNodeDto>())).ToList();
            return new UniverseAdaptationTreeResponse(universe_qid: qid, works: flatWorks);
        }

        var tree = rootWorkQids.Select(rootQid =>
        {
            entityByQid.TryGetValue(rootQid, out var entity);
            return new UniverseAdaptationNodeDto(
                qid: rootQid,
                label: entity?.Label ?? rootQid,
                year: (int?)null,
                media_type: entity?.EntitySubType ?? "Unknown",
                cover_image: entity?.ImageUrl,
                relationship_to_parent: (string?)null,
                children: BuildChildren(rootQid, adaptationRels, entityByQid, 1).ToList());
        }).ToList();

        return new UniverseAdaptationTreeResponse(universe_qid: qid, works: tree);
    }

    /// <summary>
    /// The compact read-only Cast and Adaptations lists the helper apps and the Universe page show. Both are limited to
    /// what the profile may see; Adaptations lists only real adaptation relationships and is empty without them.
    /// </summary>
    /// <param name="visibleWorkQids">
    /// Identity QIDs of every work the profile may see, when the caller already loaded the authorised projection;
    /// null to read it here.
    /// </param>
    public async Task<UniverseHelperLists> GetHelperListsAsync(
        string qid,
        IReadOnlySet<string>? visibleWorkQids,
        CancellationToken ct = default)
    {
        visibleWorkQids ??= await LoadVisibleWorkQidsAsync(display, ct).ConfigureAwait(false);
        var scope = await LoadVisibleUniverseScopeAsync(qid, entities, visibleWorkQids, ct).ConfigureAwait(false);
        var cast = await BuildCastMembersAsync(scope.Entities, visibleWorkQids, ct).ConfigureAwait(false);
        var adaptations = await BuildAdaptationsAsync(scope.Entities, ct).ConfigureAwait(false);
        return new UniverseHelperLists(cast, adaptations);
    }

    private async Task<IReadOnlyList<UniverseClientCastMemberDto>> BuildCastMembersAsync(
        IReadOnlyList<FictionalEntity> visibleEntities,
        IReadOnlySet<string> visibleWorkQids,
        CancellationToken ct)
    {
        var characters = visibleEntities
            .Where(entity => string.Equals(entity.EntitySubType, "Character", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(entity => entity.Id);
        if (characters.Count == 0)
        {
            return [];
        }

        var credits = await people.GetCharacterPerformersAsync(characters.Keys, ct).ConfigureAwait(false);
        return credits
            // A credit that cannot be tied to a work the profile may see is left out.
            .Where(credit => credit.WorkQid is { Length: > 0 } workQid && visibleWorkQids.Contains(workQid))
            .GroupBy(credit => credit.PersonId)
            .Select(person =>
            {
                var first = person.First();
                return new UniverseClientCastMemberDto
                {
                    PersonId = person.Key,
                    Name = first.PerformerName,
                    HeadshotUrl = ApiImageUrls.BuildPersonHeadshotUrl(
                        person.Key,
                        first.LocalHeadshotPath,
                        first.HeadshotUrl),
                    Characters = person
                        .Where(credit => characters.ContainsKey(credit.FictionalEntityId))
                        .Select(credit => characters[credit.FictionalEntityId])
                        .Where(character => !string.IsNullOrWhiteSpace(character.WikidataQid))
                        .DistinctBy(character => character.WikidataQid, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(character => character.Label, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(character => character.WikidataQid, StringComparer.OrdinalIgnoreCase)
                        .Select(character => new UniverseClientCastCharacterDto
                        {
                            Qid = character.WikidataQid,
                            Label = character.Label,
                        })
                        .ToList(),
                    WorkCount = person
                        .Select(credit => credit.WorkQid!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                };
            })
            .OrderByDescending(member => member.WorkCount)
            .ThenBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(member => member.PersonId)
            .Take(MaxCastMembers)
            .ToList();
    }

    private async Task<IReadOnlyList<UniverseClientAdaptationDto>> BuildAdaptationsAsync(
        IReadOnlyList<FictionalEntity> visibleEntities,
        CancellationToken ct)
    {
        var adaptationRels = await LoadAdaptationRelationshipsAsync(visibleEntities, ct).ConfigureAwait(false);
        if (adaptationRels.Count == 0)
        {
            return [];
        }

        var entityByQid = visibleEntities.ToDictionary(
            entity => entity.WikidataQid, entity => entity, StringComparer.OrdinalIgnoreCase);
        return adaptationRels
            .Select(rel =>
            {
                entityByQid.TryGetValue(rel.SubjectQid, out var subject);
                entityByQid.TryGetValue(rel.ObjectQid, out var source);
                return new UniverseClientAdaptationDto
                {
                    Qid = rel.SubjectQid,
                    Label = subject?.Label ?? rel.SubjectQid,
                    MediaType = subject?.EntitySubType,
                    Relationship = FormatEdgeLabel(rel.RelationshipTypeValue),
                    SourceQid = rel.ObjectQid,
                    SourceLabel = source?.Label ?? rel.ObjectQid,
                };
            })
            .DistinctBy(row => (row.Qid.ToUpperInvariant(), row.Relationship, row.SourceQid.ToUpperInvariant()))
            .OrderBy(row => row.SourceLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Qid, StringComparer.OrdinalIgnoreCase)
            .Take(MaxAdaptations)
            .ToList();
    }

    private async Task<IReadOnlyList<EntityRelationship>> LoadAdaptationRelationshipsAsync(
        IReadOnlyList<FictionalEntity> visibleEntities,
        CancellationToken ct)
    {
        var entityQids = visibleEntities.Select(e => e.WikidataQid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allRels = await relationships.GetByUniverseAsync(entityQids, ct).ConfigureAwait(false);
        return allRels
            .Where(r => AdaptationTypes.Contains(r.RelationshipTypeValue))
            .ToList();
    }

    private static IEnumerable<UniverseAdaptationNodeDto> BuildChildren(
        string parentQid,
        IReadOnlyList<EntityRelationship> rels,
        IReadOnlyDictionary<string, FictionalEntity> byQid,
        int depth)
    {
        if (depth > 6)
        {
            yield break; // Guard against cycles.
        }

        foreach (var rel in rels.Where(r =>
            string.Equals(r.ObjectQid, parentQid, StringComparison.OrdinalIgnoreCase)))
        {
            byQid.TryGetValue(rel.SubjectQid, out var child);
            yield return new UniverseAdaptationNodeDto(
                qid: rel.SubjectQid,
                label: child?.Label ?? rel.SubjectQid,
                year: (int?)null,
                media_type: child?.EntitySubType ?? "Unknown",
                cover_image: child?.ImageUrl,
                relationship_to_parent: FormatEdgeLabel(rel.RelationshipTypeValue),
                children: BuildChildren(rel.SubjectQid, rels, byQid, depth + 1).ToList());
        }
    }

    internal static async Task<HashSet<string>> LoadVisibleWorkQidsAsync(
        IDisplayProjectionReadService display,
        CancellationToken ct) =>
        (await display.LoadWorksAsync(ct).ConfigureAwait(false))
            .Select(work => work.IdentityQid)
            .Where(qid => !string.IsNullOrWhiteSpace(qid))
            .Select(qid => qid!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static async Task<VisibleUniverseScope> LoadVisibleUniverseScopeAsync(
        string universeQid,
        IFictionalEntityRepository entityRepo,
        IReadOnlySet<string> visibleWorkQids,
        CancellationToken ct)
    {
        var allEntities = await entityRepo.GetByUniverseAsync(universeQid, ct).ConfigureAwait(false);
        var allLinks = await entityRepo.GetWorkLinksAsync(
            allEntities.Select(entity => entity.Id), ct).ConfigureAwait(false);
        var visibleLinks = allLinks
            .Where(link => visibleWorkQids.Contains(link.WorkQid))
            .ToList();
        return new VisibleUniverseScope(
            FilterVisibleEntities(allEntities, visibleLinks),
            visibleLinks.ToLookup(link => link.FictionalEntityId));
    }

    internal static IReadOnlyList<FictionalEntity> FilterVisibleEntities(
        IReadOnlyList<FictionalEntity> entities,
        IReadOnlyList<FictionalEntityWorkLink> visibleLinks)
    {
        var visibleEntityIds = visibleLinks
            .Select(link => link.FictionalEntityId)
            .ToHashSet();
        return entities.Where(entity => visibleEntityIds.Contains(entity.Id)).ToList();
    }

    internal sealed record VisibleUniverseScope(
        IReadOnlyList<FictionalEntity> Entities,
        ILookup<Guid, FictionalEntityWorkLink> WorkLinks);

    /// <summary>
    /// Converts a relationship type constant (e.g. "father") to a human-readable
    /// edge label (e.g. "parent of"). Used in the graph JSON for Cytoscape.js display.
    /// </summary>
    internal static string FormatEdgeLabel(string relType) => relType switch
    {
        "father" => "father of",
        "mother" => "mother of",
        "spouse" => "spouse of",
        "sibling" => "sibling of",
        "child" => "child of",
        "opponent" => "opponent of",
        "student_of" => "student of",
        "member_of" => "member of",
        "residence" => "resides in",
        "located_in" => "located in",
        "part_of" => "part of",
        "head_of" => "head of",
        "parent_organization" => "parent org of",
        "has_parts" => "has parts",
        "creator" => "created by",
        "performer" => "performed by",
        "same_as" => "same as",
        "significant_person" => "significant to",
        "affiliation" => "affiliated with",
        "based_on" => "based on",
        "derivative_work" => "derivative of",
        "inspired_by" => "inspired by",
        _ => relType.Replace('_', ' '),
    };
}

/// <summary>The compact Cast and Adaptations lists of one Universe.</summary>
public sealed record UniverseHelperLists(
    IReadOnlyList<UniverseClientCastMemberDto> Cast,
    IReadOnlyList<UniverseClientAdaptationDto> Adaptations);
