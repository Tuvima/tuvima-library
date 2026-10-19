using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Endpoints;

/// <summary>
/// Universe graph API endpoints — serves Cytoscape.js-ready JSON for the
/// fictional universe relationship graph.
///
/// <list type="bullet">
/// <item><c>GET /universes</c> — list all known narrative roots</item>
/// <item><c>GET /universe/{qid}</c> — universe detail with entity counts</item>
/// <item><c>GET /universe/{qid}/graph</c> — full graph: nodes + edges</item>
/// </list>
/// </summary>
public static class UniverseGraphEndpoints
{
    public static IEndpointRouteBuilder MapUniverseGraphEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/")
                       .WithTags("Universe Graph");

        // GET /universes — list all known narrative roots.
        group.MapGet("/universes", async (
            INarrativeRootRepository rootRepo,
            IFictionalEntityRepository entityRepo,
            IEntityRelationshipRepository relRepo,
            IDisplayProjectionReadService display,
            HttpContext httpContext,
            CatalogueResourceAuthorizationService resourceAuthorization,
            CancellationToken ct) =>
        {
            var roots = await rootRepo.ListAllAsync(ct);
            var visibleWorkQids = await LoadVisibleWorkQidsAsync(display, ct);
            var results = new List<UniverseListItemDto>();
            foreach (var root in roots)
            {
                if (await resourceAuthorization.EvaluateQidAsync(
                        httpContext,
                        root.Qid,
                        ApplicationPermissionIds.LibraryRead,
                        ct) != CatalogueResourceAccess.Allowed)
                {
                    continue;
                }

                var scope = await LoadVisibleUniverseScopeAsync(
                    root.Qid, entityRepo, visibleWorkQids, ct);
                var entities = scope.Entities;
                var entityQids = entities.Select(e => e.WikidataQid).ToHashSet(StringComparer.OrdinalIgnoreCase);
                IReadOnlyList<MediaEngine.Domain.Entities.EntityRelationship> relationships =
                    entityQids.Count == 0
                        ? []
                        : await relRepo.GetByUniverseAsync(entityQids, ct);

                results.Add(new UniverseListItemDto(
                    qid: root.Qid,
                    label: root.Label,
                    level: root.Level,
                    parent_qid: root.ParentQid,
                    entity_count: entities.Count,
                    character_count: entities.Count(e => e.EntitySubType == "Character"),
                    location_count: entities.Count(e => e.EntitySubType == "Location"),
                    organization_count: entities.Count(e => e.EntitySubType == "Organization"),
                    event_count: entities.Count(e => e.EntitySubType == "Event"),
                    relationship_count: relationships.Count,
                    has_graph: entities.Count > 0 && relationships.Count > 0,
                    enrichment_status: entities.Count == 0
                        ? "Enrichment pending"
                        : relationships.Count == 0
                            ? "Partial"
                            : "Live"));
            }

            return Results.Ok(results);
        })
        .Produces<List<UniverseListItemDto>>(StatusCodes.Status200OK)
        .RequireClientScope(ApplicationPermissionIds.LibraryRead.Value);

        // GET /universe/{qid} — universe detail with entity counts.
        group.MapGet("/universe/{qid}", async (
            string qid,
            INarrativeRootRepository rootRepo,
            IFictionalEntityRepository entityRepo,
            IEntityRelationshipRepository relRepo,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var root = await rootRepo.FindByQidAsync(qid, ct);
            if (root is null)
            {
                return ApiErrors.NotFound($"Narrative root '{qid}' not found.");
            }

            var visibleWorkQids = await LoadVisibleWorkQidsAsync(display, ct);
            var entities = (await LoadVisibleUniverseScopeAsync(
                qid, entityRepo, visibleWorkQids, ct)).Entities;
            var entityQids = entities.Select(e => e.WikidataQid).ToHashSet();
            var relationships = await relRepo.GetByUniverseAsync(entityQids, ct);

            return Results.Ok(new UniverseDetailResponse(
                universe: new UniverseDetailRef(qid: root.Qid, label: root.Label, level: root.Level),
                entity_count: entities.Count,
                character_count: entities.Count(e => e.EntitySubType == "Character"),
                location_count: entities.Count(e => e.EntitySubType == "Location"),
                organization_count: entities.Count(e => e.EntitySubType == "Organization"),
                relationship_count: relationships.Count));
        })
        .Produces<UniverseDetailResponse>(StatusCodes.Status200OK)
        .RequireClientScope(ApplicationPermissionIds.LibraryRead.Value)
        .RequireCatalogueQidAccess(ApplicationPermissionIds.LibraryRead);

        // GET /universe/{qid}/lore-delta — check for Wikidata revision changes.
        group.MapGet("/universe/{qid}/lore-delta", async (
            string qid,
            ILoreDeltaService loreDeltaService,
            CancellationToken ct) =>
        {
            var results = await loreDeltaService.CheckForUpdatesAsync(qid, ct);
            return Results.Ok(results.Select(r => new UniverseLoreDeltaResponse(
                entity_qid: r.EntityQid,
                label: r.Label,
                cached_revision: r.CachedRevision,
                current_revision: r.CurrentRevision,
                has_changed: r.HasChanged)).ToList());
        })
        .Produces<IReadOnlyList<UniverseLoreDeltaResponse>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataEnrichmentRead)
        .RequireCatalogueQidAccess(ApplicationPermissionIds.MetadataEnrichmentRead);

        // GET /universe/{qid}/graph — Cytoscape.js-ready JSON: { universe, nodes[], edges[] }
        group.MapGet("/universe/{qid}/graph", async (
            string qid,
            string? types,
            string? work,
            string? center,
            int? depth,
            int? timeline_year,
            bool? include_supplemental_lore,
            INarrativeRootRepository rootRepo,
            IFictionalEntityRepository entityRepo,
            IEntityRelationshipRepository relRepo,
            IPluginLoreRepository pluginLoreRepo,
            IEraActorResolverService eraActorResolver,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var root = await rootRepo.FindByQidAsync(qid, ct);
            if (root is null)
            {
                return ApiErrors.NotFound($"Narrative root '{qid}' not found.");
            }

            // Load all entities in this universe.
            var visibleWorkQids = await LoadVisibleWorkQidsAsync(display, ct);
            var visibleScope = await LoadVisibleUniverseScopeAsync(
                qid, entityRepo, visibleWorkQids, ct);
            var allEntities = visibleScope.Entities;

            // Apply type filter if specified (e.g. ?types=Character,Location).
            if (!string.IsNullOrWhiteSpace(types))
            {
                var typeSet = new HashSet<string>(
                    types.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    StringComparer.OrdinalIgnoreCase);
                allEntities = allEntities.Where(e => typeSet.Contains(e.EntitySubType)).ToList();
            }

            var workLinksByEntity = visibleScope.WorkLinks;

            // Apply work filter if specified (e.g. ?work=Q190192).
            if (!string.IsNullOrWhiteSpace(work))
            {
                allEntities = allEntities
                    .Where(entity => workLinksByEntity[entity.Id]
                        .Any(link => link.WorkQid.Equals(work, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }

            var entityQids = allEntities.Select(e => e.WikidataQid).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var relationships = (await relRepo.GetByUniverseAsync(entityQids, ct)).ToList();

            // Apply ego-network filter (e.g. ?center=Q937618&depth=1).
            if (!string.IsNullOrWhiteSpace(center) && entityQids.Contains(center))
            {
                var maxDepth = depth ?? 1;
                var egoNetwork = BuildEgoNetwork(center, maxDepth, entityQids, relationships);

                allEntities = allEntities.Where(e => egoNetwork.Contains(e.WikidataQid)).ToList();
                entityQids = allEntities.Select(e => e.WikidataQid).ToHashSet(StringComparer.OrdinalIgnoreCase);
                relationships = relationships
                    .Where(edge => entityQids.Contains(edge.SubjectQid)
                        && entityQids.Contains(edge.ObjectQid))
                    .ToList();
            }

            // Apply timeline year filter — exclude edges that started after the given year.
            if (timeline_year.HasValue)
            {
                relationships = relationships.Where(r =>
                {
                    if (string.IsNullOrWhiteSpace(r.StartTime))
                    {
                        return true;
                    }
                    // Handle Wikidata "+YYYY..." prefix by skipping the leading '+'.
                    var span = r.StartTime.AsSpan();
                    if (span.Length > 0 && span[0] == '+')
                    {
                        span = span[1..];
                    }

                    if (int.TryParse(span[..Math.Min(4, span.Length)], out var startYear))
                    {
                        return startYear <= timeline_year.Value;
                    }

                    return true;
                }).ToList();
            }

            // Build nodes with work links.
            IReadOnlyDictionary<string, ActorResolution> actorResolutions =
                new Dictionary<string, ActorResolution>(StringComparer.OrdinalIgnoreCase);
            if (timeline_year.HasValue)
            {
                actorResolutions = await eraActorResolver.ResolveActorsForEraAsync(
                    allEntities
                        .Where(entity => string.Equals(
                            entity.EntitySubType, "Character", StringComparison.OrdinalIgnoreCase))
                        .Select(entity => entity.WikidataQid),
                    timeline_year.Value,
                    ct);
            }

            var nodes = new List<UniverseGraphNodeDto>(allEntities.Count);
            foreach (var entity in allEntities)
            {
                // Resolve era-specific actor image for character nodes.
                string? image = null;
                if (timeline_year.HasValue
                    && string.Equals(entity.EntitySubType, "Character", StringComparison.OrdinalIgnoreCase)
                    && actorResolutions.TryGetValue(entity.WikidataQid, out var resolution)
                    && resolution.ActorPersonId.HasValue)
                {
                    image = ApiImageUrls.BuildPersonHeadshotUrl(
                        resolution.ActorPersonId.Value,
                        resolution.LocalHeadshotPath,
                        resolution.HeadshotUrl);
                }

                nodes.Add(new UniverseGraphNodeDto(
                    id: entity.WikidataQid,
                    label: entity.Label,
                    type: entity.EntitySubType,
                    description: entity.Description,
                    image: image,
                    works: workLinksByEntity[entity.Id]
                        .Where(link => visibleWorkQids.Contains(link.WorkQid))
                        .Select(link => new UniverseGraphWorkLinkDto(
                            qid: link.WorkQid,
                            label: link.WorkLabel,
                            link_type: link.LinkType,
                            appearance_role: link.AppearanceRole,
                            work_context: link.WorkContext,
                            anchor_kind: link.AnchorKind,
                            anchor_value: link.AnchorValue,
                            narrative_time_index: link.NarrativeTimeIndex,
                            start_time: link.StartTime,
                            end_time: link.EndTime,
                            spoiler_for_work_qid: link.SpoilerForWorkQid,
                            source_provider: link.SourceProvider,
                            provenance: link.Provenance,
                            supplemental: link.IsSupplemental,
                            confidence: link.Confidence,
                            appearance_key: link.AppearanceKey)),
                    supplemental: false,
                    provenance: "wikidata",
                    source_plugin: (string?)null,
                    source_url: (string?)null));
            }

            // Build edges.
            var edges = relationships.Select(r => new UniverseGraphEdgeDto(
                source: r.SubjectQid,
                target: r.ObjectQid,
                type: r.RelationshipTypeValue,
                label: FormatEdgeLabel(r.RelationshipTypeValue),
                confidence: r.Confidence,
                context_work: r.ContextWorkQid,
                start_time: r.StartTime,
                end_time: r.EndTime,
                supplemental: r.IsSupplemental,
                provenance: r.Provenance,
                source_plugin: (string?)null,
                source_url: (string?)null,
                statement_key: r.StatementKey,
                qualifiers: r.Qualifiers.Select(qualifier => new UniverseGraphQualifierDto(
                    type: qualifier.QualifierType,
                    value: qualifier.Value,
                    value_kind: qualifier.ValueKind,
                    provenance: qualifier.Provenance,
                    supplemental: qualifier.IsSupplemental,
                    source_provider: qualifier.SourceProvider,
                    confidence: qualifier.Confidence)).ToList(),
                source_provider: r.SourceProvider)).ToList();

            if (include_supplemental_lore == true
                && string.IsNullOrWhiteSpace(work)
                && string.IsNullOrWhiteSpace(center))
            {
                await AppendSupplementalLoreAsync(
                    qid,
                    types,
                    pluginLoreRepo,
                    entityQids,
                    nodes,
                    edges,
                    ct).ConfigureAwait(false);
            }

            return Results.Ok(new UniverseGraphResponse(
                universe: new UniverseGraphRef(qid: root.Qid, label: root.Label),
                nodes: nodes,
                edges: edges));
        })
        .Produces<UniverseGraphResponse>(StatusCodes.Status200OK)
        .RequireClientScope(ApplicationPermissionIds.LibraryRead.Value)
        .RequireCatalogueQidAccess(ApplicationPermissionIds.LibraryRead);

        // GET /universe/{qid}/paths?from=Q1&to=Q2&maxHops=4 — find paths between entities.
        group.MapGet("/universe/{qid}/paths", async (
            string qid,
            string from,
            string to,
            int? maxHops,
            IUniverseGraphQueryService graphQuery,
            IFictionalEntityRepository entityRepo,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var visibleQids = await LoadVisibleEntityQidsAsync(qid, entityRepo, display, ct);
            var paths = await graphQuery.FindPathsAsync(qid, from, to, maxHops ?? 4, ct);
            paths = paths.Where(path => path.All(visibleQids.Contains)).ToList();
            return Results.Ok(new UniversePathsResponse(universe_qid: qid, from_qid: from, to_qid: to, paths: paths));
        })
        .Produces<UniversePathsResponse>(StatusCodes.Status200OK)
        .RequireClientScope(ApplicationPermissionIds.LibraryRead.Value)
        .RequireCatalogueQidAccess(ApplicationPermissionIds.LibraryRead);

        // GET /universe/{qid}/family-tree?character=Q1&generations=3 — character family tree.
        group.MapGet("/universe/{qid}/family-tree", async (
            string qid,
            string character,
            int? generations,
            IUniverseGraphQueryService graphQuery,
            IFictionalEntityRepository entityRepo,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var visibleQids = await LoadVisibleEntityQidsAsync(qid, entityRepo, display, ct);
            var tree = await graphQuery.GetFamilyTreeAsync(qid, character, generations ?? 3, ct);
            return Results.Ok(new UniverseFamilyTreeResponse(
                universe_qid: qid,
                character_qid: character,
                generations: tree.ToDictionary(
                    kvp => kvp.Key.ToString(),
                    kvp => (IReadOnlyList<string>)kvp.Value.Where(visibleQids.Contains).ToList())));
        })
        .Produces<UniverseFamilyTreeResponse>(StatusCodes.Status200OK)
        .RequireClientScope(ApplicationPermissionIds.LibraryRead.Value)
        .RequireCatalogueQidAccess(ApplicationPermissionIds.LibraryRead);

        // GET /universe/{qid}/cast — characters with their real-world performers.
        group.MapGet("/universe/{qid}/cast", async (
            string qid,
            IFictionalEntityRepository entityRepo,
            IPersonRepository personRepo,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            // Load all Character-type entities in this universe.
            var visibleWorkQids = await LoadVisibleWorkQidsAsync(display, ct);
            var allEntities = (await LoadVisibleUniverseScopeAsync(
                qid, entityRepo, visibleWorkQids, ct)).Entities;
            var characters = allEntities.Where(e =>
                string.Equals(e.EntitySubType, "Character", StringComparison.OrdinalIgnoreCase)).ToList();

            if (characters.Count == 0)
            {
                return Results.Ok(new UniverseCastResponse(universe_qid: qid, characters: Array.Empty<UniverseCastCharacterDto>()));
            }

            var allPerformerRows = await personRepo.GetCharacterPerformersAsync(
                characters.Select(character => character.Id),
                ct);

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

            return Results.Ok(new UniverseCastResponse(universe_qid: qid, characters: castList));
        })
        .Produces<UniverseCastResponse>(StatusCodes.Status200OK)
        .RequireClientScope(ApplicationPermissionIds.LibraryRead.Value)
        .RequireCatalogueQidAccess(ApplicationPermissionIds.LibraryRead);

        // GET /universe/{qid}/adaptations — adaptation chain (based_on/derivative_work).
        group.MapGet("/universe/{qid}/adaptations", async (
            string qid,
            IFictionalEntityRepository entityRepo,
            IEntityRelationshipRepository relRepo,
            INarrativeRootRepository rootRepo,
            IDisplayProjectionReadService display,
            CancellationToken ct) =>
        {
            var root = await rootRepo.FindByQidAsync(qid, ct);
            if (root is null)
            {
                return ApiErrors.NotFound($"Narrative root '{qid}' not found.");
            }

            // Load all entities and adaptation-type relationships.
            var visibleWorkQids = await LoadVisibleWorkQidsAsync(display, ct);
            var allEntities = (await LoadVisibleUniverseScopeAsync(
                qid, entityRepo, visibleWorkQids, ct)).Entities;
            var entityQids = allEntities.Select(e => e.WikidataQid).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var allRels = await relRepo.GetByUniverseAsync(entityQids, ct);
            var adaptationTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "based_on", "derivative_work", "inspired_by" };

            var adaptationRels = allRels
                .Where(r => adaptationTypes.Contains(r.RelationshipTypeValue))
                .ToList();

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
            if (!adaptationRels.Any())
            {
                var flatWorks = allEntities.Select(e => new UniverseAdaptationNodeDto(
                    qid: e.WikidataQid,
                    label: e.Label,
                    year: (int?)null,
                    media_type: e.EntitySubType,
                    cover_image: e.ImageUrl,
                    relationship_to_parent: (string?)null,
                    children: Array.Empty<UniverseAdaptationNodeDto>())).ToList();
                return Results.Ok(new UniverseAdaptationTreeResponse(universe_qid: qid, works: flatWorks));
            }

            // Build tree recursively.
            static IEnumerable<UniverseAdaptationNodeDto> BuildChildren(
                string parentQid,
                IReadOnlyList<MediaEngine.Domain.Entities.EntityRelationship> rels,
                IReadOnlyDictionary<string, MediaEngine.Domain.Entities.FictionalEntity> byQid,
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

            return Results.Ok(new UniverseAdaptationTreeResponse(universe_qid: qid, works: tree));
        })
        .Produces<UniverseAdaptationTreeResponse>(StatusCodes.Status200OK)
        .RequireClientScope(ApplicationPermissionIds.LibraryRead.Value)
        .RequireCatalogueQidAccess(ApplicationPermissionIds.LibraryRead);

        return app;
    }

    private static async Task<HashSet<string>> LoadVisibleWorkQidsAsync(
        IDisplayProjectionReadService display,
        CancellationToken ct) =>
        (await display.LoadWorksAsync(ct))
            .Select(work => work.IdentityQid)
            .Where(qid => !string.IsNullOrWhiteSpace(qid))
            .Select(qid => qid!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task<HashSet<string>> LoadVisibleEntityQidsAsync(
        string universeQid,
        IFictionalEntityRepository entityRepo,
        IDisplayProjectionReadService display,
        CancellationToken ct) =>
        (await LoadVisibleUniverseScopeAsync(
            universeQid,
            entityRepo,
            await LoadVisibleWorkQidsAsync(display, ct),
            ct))
        .Entities
        .Select(entity => entity.WikidataQid)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task<VisibleUniverseScope> LoadVisibleUniverseScopeAsync(
        string universeQid,
        IFictionalEntityRepository entityRepo,
        IReadOnlySet<string> visibleWorkQids,
        CancellationToken ct)
    {
        var allEntities = await entityRepo.GetByUniverseAsync(universeQid, ct);
        var allLinks = await entityRepo.GetWorkLinksAsync(
            allEntities.Select(entity => entity.Id), ct);
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

    private sealed record VisibleUniverseScope(
        IReadOnlyList<FictionalEntity> Entities,
        ILookup<Guid, FictionalEntityWorkLink> WorkLinks);

    private static async Task AppendSupplementalLoreAsync(
        string universeQid,
        string? types,
        IPluginLoreRepository pluginLoreRepo,
        IReadOnlySet<string> existingQids,
        List<UniverseGraphNodeDto> nodes,
        List<UniverseGraphEdgeDto> edges,
        CancellationToken ct)
    {
        var typeSet = string.IsNullOrWhiteSpace(types)
            ? null
            : new HashSet<string>(
                types.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);

        var supplementalEntities = await pluginLoreRepo.GetEntitiesAsync(universeQid, approvedOnly: true, ct).ConfigureAwait(false);
        if (typeSet is not null)
        {
            supplementalEntities = supplementalEntities.Where(entity => typeSet.Contains(entity.EntityType)).ToList();
        }

        var nodeIdsByExternalKey = new Dictionary<(Guid SourceId, string ExternalKey), string>();
        var nodeIds = existingQids.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in supplementalEntities)
        {
            var nodeId = !string.IsNullOrWhiteSpace(entity.WikidataQid)
                ? entity.WikidataQid
                : BuildSupplementalNodeId(entity.SourceId, entity.ExternalKey);

            nodeIdsByExternalKey[(entity.SourceId, entity.ExternalKey)] = nodeId;

            if (!nodeIds.Add(nodeId))
            {
                continue;
            }

            nodes.Add(new UniverseGraphNodeDto(
                id: nodeId,
                label: entity.Label,
                type: entity.EntityType,
                description: entity.Description,
                image: (string?)null,
                works: Array.Empty<UniverseGraphWorkLinkDto>(),
                supplemental: true,
                provenance: "plugin",
                source_plugin: entity.PluginId,
                source_url: entity.SourceUrl));
        }

        var supplementalRelationships = await pluginLoreRepo.GetRelationshipsAsync(universeQid, approvedOnly: true, ct).ConfigureAwait(false);
        foreach (var relationship in supplementalRelationships)
        {
            var sourceId = !string.IsNullOrWhiteSpace(relationship.SubjectQid)
                ? relationship.SubjectQid
                : nodeIdsByExternalKey.GetValueOrDefault((relationship.SourceId, relationship.SubjectExternalKey));
            var targetId = !string.IsNullOrWhiteSpace(relationship.ObjectQid)
                ? relationship.ObjectQid
                : nodeIdsByExternalKey.GetValueOrDefault((relationship.SourceId, relationship.ObjectExternalKey));

            if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(targetId))
            {
                continue;
            }

            if (!nodeIds.Contains(sourceId) || !nodeIds.Contains(targetId))
            {
                continue;
            }

            edges.Add(new UniverseGraphEdgeDto(
                source: sourceId,
                target: targetId,
                type: relationship.RelationshipType,
                label: FormatEdgeLabel(relationship.RelationshipType),
                confidence: relationship.Confidence,
                context_work: (string?)null,
                start_time: (string?)null,
                end_time: (string?)null,
                supplemental: true,
                provenance: "plugin",
                source_plugin: relationship.PluginId,
                source_url: relationship.SourceUrl));
        }
    }

    internal static HashSet<string> BuildEgoNetwork(
        string center,
        int maxDepth,
        IReadOnlySet<string> entityQids,
        IReadOnlyList<MediaEngine.Domain.Entities.EntityRelationship> relationships)
    {
        var egoNetwork = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { center };
        var frontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { center };

        for (var currentDepth = 0; currentDepth < maxDepth; currentDepth++)
        {
            var nextFrontier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var edge in relationships)
            {
                if (!frontier.Contains(edge.SubjectQid) && !frontier.Contains(edge.ObjectQid))
                {
                    continue;
                }

                if (entityQids.Contains(edge.SubjectQid) && egoNetwork.Add(edge.SubjectQid))
                {
                    nextFrontier.Add(edge.SubjectQid);
                }

                if (entityQids.Contains(edge.ObjectQid) && egoNetwork.Add(edge.ObjectQid))
                {
                    nextFrontier.Add(edge.ObjectQid);
                }
            }

            frontier = nextFrontier;
            if (frontier.Count == 0)
            {
                break;
            }
        }

        return egoNetwork;
    }

    private static string BuildSupplementalNodeId(Guid sourceId, string externalKey) =>
        $"plugin:{sourceId:N}:{Uri.EscapeDataString(externalKey)}";

    /// <summary>
    /// Converts a relationship type constant (e.g. "father") to a human-readable
    /// edge label (e.g. "parent of"). Used in the graph JSON for Cytoscape.js display.
    /// </summary>
    private static string FormatEdgeLabel(string relType) => relType switch
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
