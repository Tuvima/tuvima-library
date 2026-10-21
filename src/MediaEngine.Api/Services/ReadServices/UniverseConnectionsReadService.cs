using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Reads how one Universe's characters and works connect: the Characters themselves, the Cast (characters and the people
/// who play them) and the adaptation relationships between works. Every row carries the source it was learned from
/// ("Wikidata", an approved lore source, or whatever else was recorded). The visibility rule is the one every Universe
/// read uses: an entity is listed only when it is linked to a work the requesting profile may see, judged through the
/// authorised display projection (read through <see cref="CharacterSpoilerService"/>). Backs the Universe graph actions
/// (<c>/universe/{qid}/cast</c>, <c>/universe/{qid}/adaptations</c>), the compact read-only lists on
/// <c>/api/v1/universe/{qid}</c> and the Connections of the Universe page, so the queries exist once. Characters follow the
/// one spoiler rule (<see cref="CharacterSpoilerService"/>): a character the profile has not reached is left out (and
/// counted), one it cannot place is named with "no plot details".
/// </summary>
public sealed class UniverseConnectionsReadService(
    IFictionalEntityRepository entities,
    IEntityRelationshipRepository relationships,
    INarrativeRootRepository roots,
    IPersonRepository people,
    IPluginLoreRepository pluginLore,
    CharacterSpoilerService spoilers)
{
    /// <summary>Most people listed in the compact Cast.</summary>
    public const int MaxCastMembers = 50;

    /// <summary>Most relationships listed in the compact Adaptations.</summary>
    public const int MaxAdaptations = 50;

    /// <summary>Most characters listed in the Connections; the full number is still reported.</summary>
    public const int MaxCharacters = 60;

    private static readonly string[] AdaptationTypes = ["based_on", "derivative_work", "inspired_by"];

    /// <summary>Characters in the Universe with their real-world performers (the Universe graph's Cast action).</summary>
    public async Task<UniverseCastResponse> GetCastAsync(string qid, Profile? profile, CancellationToken ct = default)
    {
        // Load all Character-type entities in this universe.
        var visibleWorkQids = await LoadVisibleWorkQidsAsync(ct).ConfigureAwait(false);
        var scope = await LoadVisibleUniverseScopeAsync(qid, entities, visibleWorkQids, ct).ConfigureAwait(false);
        var visibility = await EvaluateCharactersAsync(scope, profile, ct).ConfigureAwait(false);
        var characters = scope.Entities
            .Where(entity => visibility.ContainsKey(entity.Id) && visibility[entity.Id] != CharacterVisibility.Hidden)
            .ToList();

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
                // "No plot details" names the character and nothing else.
                description: visibility[character.Id] == CharacterVisibility.NoPlotDetails ? null : character.Description,
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
    /// The adaptation chain as a tree, or null when the narrative root is unknown. With no adaptation relationships the
    /// tree is empty: evidence only, the same as the compact list (entities are never listed as if they were adaptations).
    /// Each child names the source of its relationship to its parent; roots have none.
    /// </summary>
    public async Task<UniverseAdaptationTreeResponse?> GetAdaptationTreeAsync(string qid, CancellationToken ct = default)
    {
        var root = await roots.FindByQidAsync(qid, ct).ConfigureAwait(false);
        if (root is null)
        {
            return null;
        }

        // Load all entities and adaptation-type relationships.
        var visibleWorkQids = await LoadVisibleWorkQidsAsync(ct).ConfigureAwait(false);
        var treeScope = await LoadVisibleUniverseScopeAsync(qid, entities, visibleWorkQids, ct).ConfigureAwait(false);
        var allEntities = treeScope.Entities;
        var story = await spoilers.LoadStoryAsync(ct).ConfigureAwait(false);
        // The Universe's own Works, as far as this action can tell, are the ones its characters and places appear in.
        var universeWorkQids = treeScope.WorkLinks.SelectMany(group => group).Select(link => link.WorkQid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var adaptationRels = await LoadAdaptationRelationshipsAsync(allEntities, story, universeWorkQids, ct).ConfigureAwait(false);

        // Entities are named from the entities table, Works (which are not fictional entities) from what the profile owns.
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

        if (adaptationRels.Count == 0)
        {
            return new UniverseAdaptationTreeResponse(qid, []);
        }

        var tree = rootWorkQids.Select(rootQid =>
        {
            var end = Describe(rootQid, entityByQid, story);
            return new UniverseAdaptationNodeDto(
                qid: rootQid,
                label: end.Label,
                year: (int?)null,
                media_type: end.MediaType ?? "Unknown",
                cover_image: end.CoverUrl,
                relationship_to_parent: (string?)null,
                children: BuildChildren(rootQid, adaptationRels, entityByQid, story, 1).ToList());
        }).ToList();

        return new UniverseAdaptationTreeResponse(universe_qid: qid, works: tree);
    }

    /// <summary>
    /// The Universe's Characters, Cast and Adaptations with the source of every row, from one read of the visible scope.
    /// Cast and Adaptations are exactly the helper-app rows (same filter, order and cap), only wrapped with their sources.
    /// </summary>
    /// <param name="visibleWorkQids">
    /// Identity QIDs of every work the profile may see, when the caller already loaded the authorised projection;
    /// null to read it here.
    /// </param>
    /// <param name="includeCharacters">
    /// Whether to build the Characters list (and read the approved lore sources for it); false leaves it empty with a
    /// character count of 0.
    /// </param>
    /// <param name="universeWorkQids">
    /// The identities of the Universe's own Works (and of their shows); an adaptation row needs one end in the Universe and
    /// both ends visible to the profile. Null means only the Universe's entities anchor an adaptation.
    /// </param>
    internal async Task<UniverseConnectionsDto> GetConnectionsAsync(
        string qid,
        IReadOnlySet<string>? visibleWorkQids,
        bool includeCharacters,
        Profile? profile,
        IReadOnlyCollection<string>? universeWorkQids = null,
        CancellationToken ct = default)
    {
        visibleWorkQids ??= await LoadVisibleWorkQidsAsync(ct).ConfigureAwait(false);
        var scope = await LoadVisibleUniverseScopeAsync(qid, entities, visibleWorkQids, ct).ConfigureAwait(false);
        var view = await spoilers.LoadAsync(profile, ct).ConfigureAwait(false);
        var visibility = EvaluateCharacters(view, scope);
        var cast = await BuildCastMembersAsync(scope, visibleWorkQids, visibility, ct).ConfigureAwait(false);
        var adaptations = await BuildAdaptationsAsync(scope.Entities, view.Story, universeWorkQids ?? [], ct).ConfigureAwait(false);
        if (!includeCharacters)
        {
            return new UniverseConnectionsDto { Cast = cast, Adaptations = adaptations };
        }

        var allCharacters = await BuildCharactersAsync(qid, scope, view, visibility, ct).ConfigureAwait(false);
        return new UniverseConnectionsDto
        {
            Characters = allCharacters.Take(MaxCharacters).ToList(),
            CharacterCount = allCharacters.Count,
            HiddenCharacterCount = visibility.Values.Count(value => value == CharacterVisibility.Hidden),
            Cast = cast,
            Adaptations = adaptations,
        };
    }

    /// <summary>What the profile may be shown of each Character-type entity in the scope, by entity id.</summary>
    internal async Task<IReadOnlyDictionary<Guid, CharacterVisibility>> EvaluateCharactersAsync(
        VisibleUniverseScope scope,
        Profile? profile,
        CancellationToken ct) =>
        EvaluateCharacters(await spoilers.LoadAsync(profile, ct).ConfigureAwait(false), scope);

    /// <summary>The requesting profile's spoiler judgement, for surfaces that need more than a character's visibility.</summary>
    internal Task<CharacterSpoilerView> LoadSpoilerViewAsync(Profile? profile, CancellationToken ct) => spoilers.LoadAsync(profile, ct);

    internal static IReadOnlyDictionary<Guid, CharacterVisibility> EvaluateCharacters(
        CharacterSpoilerView view,
        VisibleUniverseScope scope) =>
        scope.Entities
            .Where(entity => string.Equals(entity.EntitySubType, "Character", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(entity => entity.Id, entity => view.Evaluate(scope.WorkLinks[entity.Id]));

    private async Task<IReadOnlyList<UniverseCharacterDto>> BuildCharactersAsync(
        string universeQid,
        VisibleUniverseScope scope,
        CharacterSpoilerView view,
        IReadOnlyDictionary<Guid, CharacterVisibility> visibility,
        CancellationToken ct)
    {
        var characters = scope.Entities
            .Where(entity => string.Equals(entity.EntitySubType, "Character", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entity.WikidataQid)
                && visibility[entity.Id] != CharacterVisibility.Hidden)
            .ToList();
        if (characters.Count == 0)
        {
            return [];
        }

        // Approved lore sources are named only when they hold an entity for the character's QID.
        var loreSourceNamesByQid = await LoadLoreSourceNamesByCharacterQidAsync(
            universeQid,
            characters.Select(character => character.WikidataQid).ToList(),
            ct).ConfigureAwait(false);

        return characters
            .Select(character =>
            {
                var links = scope.WorkLinks[character.Id].ToList();
                // A link marked as a spoiler for a work is not counted unless every appearance is marked (and so cleared).
                var countedLinks = view.Counted(links);
                var noPlotDetails = visibility[character.Id] == CharacterVisibility.NoPlotDetails;
                var sources = links
                    .Select(link => UniverseSourceLabels.FromProvenance(link.Provenance, link.SourceProvider))
                    .Concat(loreSourceNamesByQid.TryGetValue(character.WikidataQid, out var loreNames)
                        ? loreNames.Select(name => new UniverseSourceDto { Kind = UniverseSourceDto.LorePlugin, Label = name })
                        : []);
                return new UniverseCharacterDto
                {
                    Qid = character.WikidataQid,
                    Label = character.Label,
                    NoPlotDetails = noPlotDetails,
                    WorkCount = noPlotDetails ? 0 : view.CountWorks(countedLinks),
                    Sources = UniverseSourceLabels.Combine(sources),
                };
            })
            .OrderBy(character => character.NoPlotDetails)
            .ThenByDescending(character => character.WorkCount)
            .ThenBy(character => character.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(character => character.Qid, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyDictionary<string, List<string>>> LoadLoreSourceNamesByCharacterQidAsync(
        string universeQid,
        IReadOnlyCollection<string> characterQids,
        CancellationToken ct)
    {
        // Only the (item, source name) pairs for the listed characters are read, never the lore entities themselves.
        var itemSources = await pluginLore.GetApprovedSourceNamesForItemsAsync(universeQid, characterQids, ct).ConfigureAwait(false);
        return itemSources
            .GroupBy(row => row.WikidataQid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(row => string.IsNullOrWhiteSpace(row.SourceName) ? "Lore plugin" : row.SourceName.Trim())
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<UniverseSourcedCastMemberDto>> BuildCastMembersAsync(
        VisibleUniverseScope scope,
        IReadOnlySet<string> visibleWorkQids,
        IReadOnlyDictionary<Guid, CharacterVisibility> visibility,
        CancellationToken ct)
    {
        var allCharacters = scope.Entities
            .Where(entity => string.Equals(entity.EntitySubType, "Character", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(entity => entity.Id);
        if (allCharacters.Count == 0)
        {
            return [];
        }

        // A character the profile has not reached is not named, even beside the actor who plays them; the person stays.
        var characters = allCharacters
            .Where(pair => visibility[pair.Key] != CharacterVisibility.Hidden)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var credits = await people.GetCharacterPerformersAsync(allCharacters.Keys, ct).ConfigureAwait(false);
        return credits
            // A credit that cannot be tied to a work the profile may see is left out.
            .Where(credit => credit.WorkQid is { Length: > 0 } workQid && visibleWorkQids.Contains(workQid))
            .GroupBy(credit => credit.PersonId)
            .Select(person =>
            {
                var first = person.First();
                return (
                    Member: new UniverseClientCastMemberDto
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
                    },
                    Sources: UniverseSourceLabels.Combine(person.Select(credit => SourceOfCredit(scope, credit))));
            })
            .OrderByDescending(item => item.Member.WorkCount)
            .ThenBy(item => item.Member.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Member.PersonId)
            .Take(MaxCastMembers)
            .Select(item => new UniverseSourcedCastMemberDto { Member = item.Member, Sources = item.Sources })
            .ToList();
    }

    private static UniverseSourceDto SourceOfCredit(VisibleUniverseScope scope, CharacterPerformerCredit credit)
    {
        // The credit's visible appearance link for the same work; "portrayed_in" is the one written for a credit.
        var link = scope.WorkLinks[credit.FictionalEntityId]
            .Where(candidate => string.Equals(candidate.WorkQid, credit.WorkQid, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => string.Equals(candidate.LinkType, "portrayed_in", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
        if (link is not null)
        {
            return UniverseSourceLabels.FromProvenance(link.Provenance, link.SourceProvider);
        }

        // A performer credit is written only by PersonEnrichmentWorker from Wikidata cast statements (P161 with a P453
        // qualifier), and the credit stores no provenance of its own, so a credit with no matching link is Wikidata.
        return UniverseSourceLabels.FromProvenance(null, null);
    }

    private async Task<IReadOnlyList<UniverseSourcedAdaptationDto>> BuildAdaptationsAsync(
        IReadOnlyList<FictionalEntity> visibleEntities,
        OwnedStory story,
        IReadOnlyCollection<string> universeWorkQids,
        CancellationToken ct)
    {
        var adaptationRels = await LoadAdaptationRelationshipsAsync(visibleEntities, story, universeWorkQids, ct).ConfigureAwait(false);
        if (adaptationRels.Count == 0)
        {
            return [];
        }

        var entityByQid = visibleEntities.ToDictionary(
            entity => entity.WikidataQid, entity => entity, StringComparer.OrdinalIgnoreCase);
        return adaptationRels
            .Select(rel =>
            {
                var subject = Describe(rel.SubjectQid, entityByQid, story);
                var source = Describe(rel.ObjectQid, entityByQid, story);
                return (
                    Row: new UniverseClientAdaptationDto
                    {
                        Qid = rel.SubjectQid,
                        Label = subject.Label,
                        MediaType = subject.MediaType,
                        Relationship = FormatEdgeLabel(rel.RelationshipTypeValue),
                        SourceQid = rel.ObjectQid,
                        SourceLabel = source.Label,
                    },
                    Source: UniverseSourceLabels.FromProvenance(rel.Provenance, rel.SourceProvider),
                    Rank: AdaptationRank(rel.RelationshipTypeValue));
            })
            // One row per pair of Works, whichever way it was recorded ("based on" from the adaptation's side, "derivative work"
            // from the source's side say the same thing): the row keeps the preferred type's label and every source contributes.
            .OrderBy(item => item.Rank)
            .GroupBy(item => (item.Row.Qid.ToUpperInvariant(), item.Row.SourceQid.ToUpperInvariant()))
            .Select(group => new UniverseSourcedAdaptationDto
            {
                Adaptation = group.First().Row,
                Sources = UniverseSourceLabels.Combine(group.Select(item => item.Source)),
            })
            .OrderBy(row => row.Adaptation.SourceLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Adaptation.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Adaptation.Qid, StringComparer.OrdinalIgnoreCase)
            .Take(MaxAdaptations)
            .ToList();
    }

    /// <summary>Which relationship type names a pair of Works when several do: "based on" first, then the order they are read in.</summary>
    private static int AdaptationRank(string relationshipType) =>
        Math.Max(0, Array.FindIndex(AdaptationTypes, type => string.Equals(type, relationshipType, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The adaptation edges that belong on this Universe. Works are not fictional entities, so an edge between two Works is
    /// found by offering the profile's visible Work identities alongside the entities': both ends must be visible, and one
    /// end must be the Universe's own (an entity, or one of <paramref name="universeWorkQids"/>).
    /// </summary>
    private async Task<IReadOnlyList<EntityRelationship>> LoadAdaptationRelationshipsAsync(
        IReadOnlyList<FictionalEntity> visibleEntities,
        OwnedStory story,
        IReadOnlyCollection<string> universeWorkQids,
        CancellationToken ct)
    {
        var anchors = visibleEntities.Select(e => e.WikidataQid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new HashSet<string>(anchors, StringComparer.OrdinalIgnoreCase);
        candidates.UnionWith(story.VisibleQids);
        anchors.UnionWith(universeWorkQids);
        // Only the adaptation relationship types are read; the data store filters them.
        var edges = await relationships.GetByUniverseAsync(candidates, AdaptationTypes, ct).ConfigureAwait(false);
        return edges
            .Where(edge => anchors.Contains(edge.SubjectQid) || anchors.Contains(edge.ObjectQid))
            .ToList();
    }

    private sealed record AdaptationEnd(string Label, string? MediaType, string? CoverUrl);

    private static AdaptationEnd Describe(string qid, IReadOnlyDictionary<string, FictionalEntity> entityByQid, OwnedStory story)
    {
        if (entityByQid.TryGetValue(qid, out var entity))
        {
            return new AdaptationEnd(entity.Label, entity.EntitySubType, entity.ImageUrl);
        }

        return story.Describe(qid) is { } work
            ? new AdaptationEnd(work.Label, work.MediaType, work.CoverUrl)
            : new AdaptationEnd(qid, null, null);
    }

    private static IEnumerable<UniverseAdaptationNodeDto> BuildChildren(
        string parentQid,
        IReadOnlyList<EntityRelationship> rels,
        IReadOnlyDictionary<string, FictionalEntity> byQid,
        OwnedStory story,
        int depth)
    {
        if (depth > 6)
        {
            yield break; // Guard against cycles.
        }

        foreach (var rel in rels
            .Where(r => string.Equals(r.ObjectQid, parentQid, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => AdaptationRank(r.RelationshipTypeValue))
            .DistinctBy(r => r.SubjectQid, StringComparer.OrdinalIgnoreCase))
        {
            var child = Describe(rel.SubjectQid, byQid, story);
            yield return new UniverseAdaptationNodeDto(
                qid: rel.SubjectQid,
                label: child.Label,
                year: (int?)null,
                media_type: child.MediaType ?? "Unknown",
                cover_image: child.CoverUrl,
                relationship_to_parent: FormatEdgeLabel(rel.RelationshipTypeValue),
                children: BuildChildren(rel.SubjectQid, rels, byQid, story, depth + 1).ToList(),
                source: UniverseSourceLabels.FromProvenance(rel.Provenance, rel.SourceProvider).Label);
        }
    }

    /// <summary>
    /// The one visibility set every Universe read judges a link's work against: the identity of each work the requesting
    /// profile may see, plus the identity of the show each belongs to (so a link to the show counts as visible whenever any of
    /// its episodes is, even when every episode carries an identity of its own).
    /// </summary>
    internal Task<OwnedStory> LoadStoryAsync(CancellationToken ct) => spoilers.LoadStoryAsync(ct);

    internal async Task<IReadOnlySet<string>> LoadVisibleWorkQidsAsync(CancellationToken ct) =>
        (await spoilers.LoadStoryAsync(ct).ConfigureAwait(false)).VisibleQids;

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
