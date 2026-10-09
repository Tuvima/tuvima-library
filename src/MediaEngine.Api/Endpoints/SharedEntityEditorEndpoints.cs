using System.Security.Cryptography;
using System.Text;
using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Metadata;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Endpoints;

/// <summary>Authorized shared-editor projections for roots and fictional entities. Graph facts are read-only.</summary>
public static class SharedEntityEditorEndpoints
{
    private sealed record Category(string Id, string Label);
    // Ordered client contract; unlike a set this cannot drift between runs.
    private static readonly IReadOnlyList<Category> Categories =
    [new("Character", "Character"), new("Location", "Places"), new("Organization", "Groups"), new("Event", "Event"), new("Object", "Object")];

    public static IEndpointRouteBuilder MapSharedEntityEditorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/entity-editor")
            .WithTags("Shared Entity Editor")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
        group.MapGet("/universes/{qid}/context", GetUniverseContextAsync).Produces<SharedEntityEditorContextDto>().RequireClientScope(ApplicationPermissionIds.MetadataRead.Value);
        group.MapGet("/universes/{qid}/categories", GetCategorySummariesAsync).Produces<IReadOnlyList<SharedEntityCategorySummaryDto>>().RequireClientScope(ApplicationPermissionIds.MetadataRead.Value);
        group.MapGet("/universes/{qid}/entities", GetSelectorAsync).Produces<SharedEntitySelectorPageDto>().RequireClientScope(ApplicationPermissionIds.MetadataRead.Value);
        group.MapGet("/universes/{qid}/details", GetUniverseDetailsAsync).Produces<SharedEntityDetailsDto>().RequireClientScope(ApplicationPermissionIds.MetadataRead.Value);
        group.MapPut("/universes/{qid}/details", UpdateUniverseDetailsAsync).Produces<SharedEntityDetailsDto>().RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite).RequireClientScope(ApplicationPermissionIds.MetadataWrite.Value);
        group.MapGet("/universes/{qid}/artwork", GetUniverseArtworkAsync).Produces<IReadOnlyList<SharedEntityArtworkDto>>().RequireClientScope(ApplicationPermissionIds.ArtworkRead.Value);
        group.MapPut("/universes/{qid}/artwork", UpdateUniverseArtworkAsync).Produces<IReadOnlyList<SharedEntityArtworkDto>>().RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite).RequireClientScope(ApplicationPermissionIds.MetadataWrite.Value);
        group.MapPost("/universes/{qid}/artwork/{assetType}/upload", UploadUniverseArtworkAsync).Accepts<IFormFile>("multipart/form-data").Produces<IReadOnlyList<SharedEntityArtworkDto>>().RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite).RequireClientScope(ApplicationPermissionIds.MetadataWrite.Value).DisableAntiforgery();
        group.MapGet("/universes/{qid}/history", GetUniverseHistoryAsync).Produces<IEnumerable<SharedEntityHistoryEntryDto>>().RequireClientScope(ApplicationPermissionIds.MetadataRead.Value);
        group.MapGet("/universes/{qid}/relationships", GetUniverseRelationshipsAsync).Produces<IEnumerable<SharedEntityRelationshipDto>>().RequireClientScope(ApplicationPermissionIds.MetadataRead.Value);
        group.MapGet("/universes/{qid}/timeline", GetUniverseTimelineAsync).Produces<IEnumerable<SharedEntityTimelineEntryDto>>().RequireClientScope(ApplicationPermissionIds.MetadataRead.Value);
        group.MapGet("/universes/{qid}/sources", GetUniverseSourcesAsync).Produces<IEnumerable<SharedEntitySourceDto>>().RequireClientScope(ApplicationPermissionIds.MetadataRead.Value);
        group.MapGet("/universes/{qid}/enrichment", GetUniverseEnrichmentAsync).Produces<SharedEntityEnrichmentStatusDto>().RequireClientScope(ApplicationPermissionIds.MetadataRead.Value);
        group.MapPost("/universes/{qid}/refresh", RefreshUniverseAsync).Produces<SharedEntityRefreshDto>().RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataEnrichmentRun).RequireClientScope(ApplicationPermissionIds.MetadataEnrichmentRun.Value);
        group.MapGet("/universes/{qid}/entities/{id:guid}/context", GetEntityContextAsync).Produces<SharedEntityEditorContextDto>().RequireClientScope(ApplicationPermissionIds.MetadataRead.Value);
        group.MapGet("/universes/{qid}/entities/{id:guid}/details", GetEntityDetailsAsync).Produces<SharedEntityDetailsDto>().RequireClientScope(ApplicationPermissionIds.MetadataRead.Value);
        group.MapPut("/universes/{qid}/entities/{id:guid}/details", UpdateEntityDetailsAsync).Produces<SharedEntityDetailsDto>().RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite).RequireClientScope(ApplicationPermissionIds.MetadataWrite.Value);
        group.MapGet("/universes/{qid}/entities/{id:guid}/artwork", GetEntityArtworkAsync).Produces<IReadOnlyList<SharedEntityArtworkDto>>().RequireClientScope(ApplicationPermissionIds.ArtworkRead.Value);
        group.MapPut("/universes/{qid}/entities/{id:guid}/artwork", UpdateEntityArtworkAsync).Produces<IReadOnlyList<SharedEntityArtworkDto>>().RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite).RequireClientScope(ApplicationPermissionIds.MetadataWrite.Value);
        group.MapPost("/universes/{qid}/entities/{id:guid}/artwork/{assetType}/upload", UploadEntityArtworkAsync).Accepts<IFormFile>("multipart/form-data").Produces<IReadOnlyList<SharedEntityArtworkDto>>().RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite).RequireClientScope(ApplicationPermissionIds.MetadataWrite.Value).DisableAntiforgery();
        group.MapGet("/universes/{qid}/entities/{id:guid}/appearances", GetAppearancesAsync).Produces<IEnumerable<SharedEntityAppearanceDto>>().RequireClientScope(ApplicationPermissionIds.MetadataRead.Value);
        group.MapGet("/universes/{qid}/entities/{id:guid}/relationships", GetRelationshipsAsync).Produces<IEnumerable<SharedEntityRelationshipDto>>().RequireClientScope(ApplicationPermissionIds.MetadataRead.Value);
        group.MapGet("/universes/{qid}/entities/{id:guid}/timeline", GetTimelineAsync).Produces<IEnumerable<SharedEntityTimelineEntryDto>>().RequireClientScope(ApplicationPermissionIds.MetadataRead.Value);
        group.MapGet("/universes/{qid}/entities/{id:guid}/sources", GetSourcesAsync).Produces<IEnumerable<SharedEntitySourceDto>>().RequireClientScope(ApplicationPermissionIds.MetadataRead.Value);
        group.MapGet("/universes/{qid}/entities/{id:guid}/history", GetHistoryAsync).Produces<IEnumerable<SharedEntityHistoryEntryDto>>().RequireClientScope(ApplicationPermissionIds.MetadataRead.Value);
        group.MapGet("/universes/{qid}/entities/{id:guid}/enrichment", GetEnrichmentAsync).Produces<SharedEntityEnrichmentStatusDto>().RequireClientScope(ApplicationPermissionIds.MetadataRead.Value);
        group.MapPost("/universes/{qid}/entities/{id:guid}/refresh", RefreshEntityAsync).Produces<SharedEntityRefreshDto>().RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataEnrichmentRun).RequireClientScope(ApplicationPermissionIds.MetadataEnrichmentRun.Value);
        return app;
    }

    private static async Task<IResult> GetUniverseContextAsync(string qid, HttpContext http, INarrativeRootRepository roots, IFictionalEntityRepository entities, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var root = await AuthorizedRootAsync(qid, http, roots, entities, display, authorization, ApplicationPermissionIds.MetadataRead, ct);
        return root is null ? ApiErrors.NotFound("Universe not found.") : Results.Ok(new SharedEntityEditorContextDto(new(SharedEntityEditorTargetKinds.Universe, root.Qid, null, root.Qid), root.Label, "Universe", RootCapabilities(), "available"));
    }

    private static async Task<IResult> GetCategorySummariesAsync(string qid, HttpContext http, INarrativeRootRepository roots, IFictionalEntityRepository entities, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        if (await AuthorizedRootAsync(qid, http, roots, entities, display, authorization, ApplicationPermissionIds.MetadataRead, ct) is null)
        {
            return ApiErrors.NotFound("Universe not found.");
        }
        var visible = await VisibleWorksAsync(display, ct);
        var items = new List<SharedEntityCategorySummaryDto>(Categories.Count);
        foreach (var category in Categories)
        {
            var result = await entities.SearchVisibleByUniverseAsync(qid, visible, category.Id, null, 0, 1, ct);
            items.Add(new SharedEntityCategorySummaryDto(category.Id, category.Label, result.Total));
        }
        return Results.Ok(items);
    }

    private static async Task<IResult> GetSelectorAsync(string qid, string? category, string? search, int? offset, int? limit, HttpContext http, INarrativeRootRepository roots, IFictionalEntityRepository entities, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        if (await AuthorizedRootAsync(qid, http, roots, entities, display, authorization, ApplicationPermissionIds.MetadataRead, ct) is null)
        {
            return ApiErrors.NotFound("Universe not found.");
        }
        var selected = ResolveCategory(category);
        if (category is not null && selected is null)
        {
            return ApiErrors.BadRequest("Unsupported entity category.");
        }
        var request = PagedRequest.From(offset, limit, defaultLimit: 50, maxLimit: 100);
        var page = await entities.SearchVisibleByUniverseAsync(qid, await VisibleWorksAsync(display, ct), selected?.Id, search, request.Offset, request.Limit, ct);
        return Results.Ok(new SharedEntitySelectorPageDto(page.Items.Select(ToSelector).ToList(), request.Offset, request.Limit, page.Total, request.Offset + page.Items.Count < page.Total));
    }

    private static async Task<IResult> GetUniverseDetailsAsync(string qid, HttpContext http, INarrativeRootRepository roots, IFictionalEntityRepository entities, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var root = await AuthorizedRootAsync(qid, http, roots, entities, display, authorization, ApplicationPermissionIds.MetadataRead, ct);
        return root is null ? ApiErrors.NotFound("Universe not found.") : Results.Ok(ToRootDetails(root));
    }

    private static async Task<IResult> UpdateUniverseDetailsAsync(string qid, SharedEntityDetailsUpdateRequest request, HttpContext http, INarrativeRootRepository roots, IFictionalEntityRepository entities, IEntityTimelineRepository timeline, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var root = await AuthorizedRootAsync(qid, http, roots, entities, display, authorization, ApplicationPermissionIds.MetadataWrite, ct);
        if (root is null)
        {
            return ApiErrors.NotFound("Universe not found.");
        }
        if (string.IsNullOrWhiteSpace(request.label))
        {
            return ApiErrors.BadRequest("A label is required.");
        }
        await roots.UpdateUserDetailsAsync(root.Qid, request.label, request.description, ct);
        await RecordEditorEventAsync(timeline, RootHistoryId(root.Qid), "Universe", "user_field_edit", "Universe details edited in shared editor.", ct);
        return Results.Ok(ToRootDetails((await roots.FindByQidAsync(root.Qid, ct))!));
    }

    private static async Task<IResult> GetUniverseArtworkAsync(string qid, HttpContext http, INarrativeRootRepository roots, IFictionalEntityRepository entities, IEntityAssetRepository assets, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var root = await AuthorizedRootAsync(qid, http, roots, entities, display, authorization, ApplicationPermissionIds.ArtworkRead, ct);
        return root is null ? ApiErrors.NotFound("Universe not found.") : Results.Ok(ToArtwork(await assets.GetByEntityAsync(root.Qid, null, ct)));
    }

    private static async Task<IResult> UpdateUniverseArtworkAsync(string qid, SharedEntityArtworkUpdateRequest request, HttpContext http, INarrativeRootRepository roots, IFictionalEntityRepository entities, IEntityAssetRepository assets, IEntityTimelineRepository timeline, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var root = await AuthorizedRootAsync(qid, http, roots, entities, display, authorization, ApplicationPermissionIds.MetadataWrite, ct);
        if (root is null)
        {
            return ApiErrors.NotFound("Universe not found.");
        }
        var response = await UpsertUserArtworkAsync(root.Qid, SharedEntityEditorTargetKinds.Universe, request, assets, ct);
        await RecordEditorEventAsync(timeline, RootHistoryId(root.Qid), "Universe", "user_artwork_edit", "Universe artwork selected in shared editor.", ct);
        return response;
    }

    private static async Task<IResult> UploadUniverseArtworkAsync(string qid, string assetType, HttpRequest request, HttpContext http, INarrativeRootRepository roots, IFictionalEntityRepository entities, IEntityAssetRepository assets, IEntityTimelineRepository timeline, ArtworkScopeService artworkScope, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var root = await AuthorizedRootAsync(qid, http, roots, entities, display, authorization, ApplicationPermissionIds.MetadataWrite, ct);
        if (root is null)
        {
            return ApiErrors.NotFound("Universe not found.");
        }
        return await UploadArtworkAsync(root.Qid, SharedEntityEditorTargetKinds.Universe, RootHistoryId(root.Qid), assetType, request, assets, timeline, artworkScope, ct);
    }

    private static async Task<IResult> GetEntityContextAsync(string qid, Guid id, HttpContext http, INarrativeRootRepository roots, IFictionalEntityRepository entities, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var entity = await AuthorizedEntityAsync(qid, id, http, entities, display, authorization, ApplicationPermissionIds.MetadataRead, ct);
        if (entity is null)
        {
            return ApiErrors.NotFound("Entity not found.");
        }
        var root = await roots.FindByQidAsync(qid, ct);
        return Results.Ok(ToEntityContext(entity, qid, root?.Label ?? qid));
    }

    private static async Task<IResult> GetUniverseHistoryAsync(string qid, HttpContext http, INarrativeRootRepository roots, IFictionalEntityRepository entities, IEntityTimelineRepository timeline, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var root = await AuthorizedRootAsync(qid, http, roots, entities, display, authorization, ApplicationPermissionIds.MetadataRead, ct);
        if (root is null)
        {
            return ApiErrors.NotFound("Universe not found.");
        }
        return Results.Ok((await timeline.GetEventsByEntityAsync(RootHistoryId(root.Qid), ct)).Select(evt => new SharedEntityHistoryEntryDto(evt.Id, evt.EventType, evt.OccurredAt, evt.Detail)));
    }

    private static async Task<IResult> GetUniverseRelationshipsAsync(string qid, HttpContext http, INarrativeRootRepository roots, IFictionalEntityRepository entities, IEntityRelationshipRepository relationships, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        if (await AuthorizedRootAsync(qid, http, roots, entities, display, authorization, ApplicationPermissionIds.MetadataRead, ct) is null)
        {
            return ApiErrors.NotFound("Universe not found.");
        }
        var visible = await VisibleWorksAsync(display, ct);
        var entityQids = (await LoadAllVisibleEntitiesAsync(qid, entities, visible, ct)).Select(entity => entity.WikidataQid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = await relationships.GetByUniverseAsync(entityQids, ct);
        return Results.Ok(rows.Where(row => string.IsNullOrWhiteSpace(row.ContextWorkQid) || visible.Contains(row.ContextWorkQid!)).Select(row => new SharedEntityRelationshipDto(row.StatementKey, row.SubjectQid, row.RelationshipTypeValue, row.ObjectQid, row.Provenance, row.ContextWorkQid, row.Qualifiers.Select(q => new UniverseGraphQualifierDto(q.QualifierType, q.Value, q.ValueKind, q.Provenance, q.IsSupplemental, q.SourceProvider, q.Confidence)).ToList())));
    }

    private static async Task<IResult> GetUniverseTimelineAsync(string qid, HttpContext http, INarrativeRootRepository roots, IFictionalEntityRepository entities, IEntityRelationshipRepository relationships, ICanonicalValueRepository canonicals, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        if (await AuthorizedRootAsync(qid, http, roots, entities, display, authorization, ApplicationPermissionIds.MetadataRead, ct) is null)
        {
            return ApiErrors.NotFound("Universe not found.");
        }
        var visible = await VisibleWorksAsync(display, ct);
        var allEntities = await LoadAllVisibleEntitiesAsync(qid, entities, visible, ct);
        var links = await entities.GetWorkLinksAsync(allEntities.Select(entity => entity.Id), ct);
        var appearances = links.Where(link => visible.Contains(link.WorkQid) && (!string.IsNullOrWhiteSpace(link.NarrativeTimeIndex) || !string.IsNullOrWhiteSpace(link.StartTime) || !string.IsNullOrWhiteSpace(link.EndTime))).Select(link => new SharedEntityTimelineEntryDto("appearance", link.NarrativeTimeIndex ?? link.WorkLabel ?? link.WorkQid, link.StartTime, link.EndTime, link.WorkQid, link.Provenance));
        var rels = await relationships.GetByUniverseAsync(allEntities.Select(entity => entity.WikidataQid).ToHashSet(StringComparer.OrdinalIgnoreCase), ct);
        var scalarByEntity = await canonicals.GetByEntitiesAsync(allEntities.Select(entity => entity.Id).ToList(), ct);
        return Results.Ok(appearances.Concat(RelationshipTimeline(rels, visible)).Concat(allEntities.SelectMany(entity => CanonicalTimeline(entity, scalarByEntity.GetValueOrDefault(entity.Id) ?? []))));
    }

    private static async Task<IResult> GetUniverseSourcesAsync(string qid, HttpContext http, INarrativeRootRepository roots, IFictionalEntityRepository entities, IEntityRelationshipRepository relationships, IPluginLoreRepository pluginLore, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        if (await AuthorizedRootAsync(qid, http, roots, entities, display, authorization, ApplicationPermissionIds.MetadataRead, ct) is null)
        {
            return ApiErrors.NotFound("Universe not found.");
        }
        var visible = await VisibleWorksAsync(display, ct);
        var allEntities = await LoadAllVisibleEntitiesAsync(qid, entities, visible, ct);
        var links = await entities.GetWorkLinksAsync(allEntities.Select(entity => entity.Id), ct);
        var appearances = links.Where(link => visible.Contains(link.WorkQid)).Select(link => new SharedEntitySourceDto("appearance", link.AppearanceKey ?? link.WorkQid, link.Provenance, link.SourceProvider, link.WorkQid, link.IsSupplemental, link.Confidence));
        var rels = await relationships.GetByUniverseAsync(allEntities.Select(entity => entity.WikidataQid).ToHashSet(StringComparer.OrdinalIgnoreCase), ct);
        return Results.Ok(appearances.Concat(RelationshipSources(rels, visible)).Concat(await SupplementalSourcesAsync(qid, null, pluginLore, ct)));
    }

    private static async Task<IResult> GetUniverseEnrichmentAsync(string qid, HttpContext http, INarrativeRootRepository roots, IFictionalEntityRepository entities, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        if (await AuthorizedRootAsync(qid, http, roots, entities, display, authorization, ApplicationPermissionIds.MetadataRead, ct) is null)
        {
            return ApiErrors.NotFound("Universe not found.");
        }
        var page = await entities.SearchVisibleByUniverseAsync(qid, await VisibleWorksAsync(display, ct), null, null, 0, 1, ct);
        return Results.Ok(new SharedEntityEnrichmentStatusDto(page.Total == 0 ? "pending" : "available", null, null));
    }

    private static async Task<IResult> RefreshUniverseAsync(string qid, HttpContext http, INarrativeRootRepository roots, IFictionalEntityRepository entities, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, IMetadataHarvestingService harvesting, CancellationToken ct)
    {
        if (await AuthorizedRootAsync(qid, http, roots, entities, display, authorization, ApplicationPermissionIds.MetadataEnrichmentRun, ct) is null)
        {
            return ApiErrors.NotFound("Universe not found.");
        }
        await harvesting.EnqueueAsync(new HarvestRequest { EntityId = Guid.Empty, EntityType = EntityType.Character, MediaType = MediaType.Unknown, Hints = new Dictionary<string, string> { ["trigger_type"] = "universe_sweep", ["universe_qid"] = qid, ["requested_by"] = "shared_editor" } }, ct);
        return Results.Ok(new SharedEntityRefreshDto(true, "Universe enrichment refresh queued."));
    }

    private static async Task<IResult> GetEntityDetailsAsync(string qid, Guid id, HttpContext http, IFictionalEntityRepository entities, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var entity = await AuthorizedEntityAsync(qid, id, http, entities, display, authorization, ApplicationPermissionIds.MetadataRead, ct);
        return entity is null ? ApiErrors.NotFound("Entity not found.") : Results.Ok(ToEntityDetails(entity, qid));
    }

    private static async Task<IResult> UpdateEntityDetailsAsync(string qid, Guid id, SharedEntityDetailsUpdateRequest request, HttpContext http, IFictionalEntityRepository entities, IEntityTimelineRepository timeline, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var entity = await AuthorizedEntityAsync(qid, id, http, entities, display, authorization, ApplicationPermissionIds.MetadataWrite, ct);
        if (entity is null)
        {
            return ApiErrors.NotFound("Entity not found.");
        }
        if (string.IsNullOrWhiteSpace(request.label))
        {
            return ApiErrors.BadRequest("A label is required.");
        }
        await entities.UpdateUserDetailsAsync(entity.Id, request.label, request.description, ct);
        await RecordEditorEventAsync(timeline, entity.Id, SharedEntityEditorTargetKinds.FictionalEntity, "user_field_edit", "Fictional entity details edited in shared editor.", ct);
        return Results.Ok(ToEntityDetails((await entities.FindByIdAsync(entity.Id, ct))!, qid));
    }

    private static async Task<IResult> GetEntityArtworkAsync(string qid, Guid id, HttpContext http, IFictionalEntityRepository entities, IEntityAssetRepository assets, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var entity = await AuthorizedEntityAsync(qid, id, http, entities, display, authorization, ApplicationPermissionIds.ArtworkRead, ct);
        return entity is null ? ApiErrors.NotFound("Entity not found.") : Results.Ok(ToArtwork(await assets.GetByEntityAsync(entity.Id.ToString(), null, ct)));
    }

    private static async Task<IResult> UpdateEntityArtworkAsync(string qid, Guid id, SharedEntityArtworkUpdateRequest request, HttpContext http, IFictionalEntityRepository entities, IEntityAssetRepository assets, IEntityTimelineRepository timeline, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var entity = await AuthorizedEntityAsync(qid, id, http, entities, display, authorization, ApplicationPermissionIds.MetadataWrite, ct);
        if (entity is null)
        {
            return ApiErrors.NotFound("Entity not found.");
        }
        var response = await UpsertUserArtworkAsync(entity.Id.ToString(), SharedEntityEditorTargetKinds.FictionalEntity, request, assets, ct);
        await RecordEditorEventAsync(timeline, entity.Id, SharedEntityEditorTargetKinds.FictionalEntity, "user_artwork_edit", "Fictional entity artwork selected in shared editor.", ct);
        return response;
    }

    private static async Task<IResult> UploadEntityArtworkAsync(string qid, Guid id, string assetType, HttpRequest request, HttpContext http, IFictionalEntityRepository entities, IEntityAssetRepository assets, IEntityTimelineRepository timeline, ArtworkScopeService artworkScope, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var entity = await AuthorizedEntityAsync(qid, id, http, entities, display, authorization, ApplicationPermissionIds.MetadataWrite, ct);
        if (entity is null)
        {
            return ApiErrors.NotFound("Entity not found.");
        }
        return await UploadArtworkAsync(entity.Id.ToString(), SharedEntityEditorTargetKinds.FictionalEntity, entity.Id, assetType, request, assets, timeline, artworkScope, ct);
    }

    private static async Task<IResult> GetAppearancesAsync(string qid, Guid id, HttpContext http, IFictionalEntityRepository entities, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var entity = await AuthorizedEntityAsync(qid, id, http, entities, display, authorization, ApplicationPermissionIds.MetadataRead, ct);
        if (entity is null)
        {
            return ApiErrors.NotFound("Entity not found.");
        }
        var visible = await VisibleWorksAsync(display, ct);
        return Results.Ok((await entities.GetWorkLinksAsync(entity.Id, ct)).Where(link => visible.Contains(link.WorkQid)).Select(link => new SharedEntityAppearanceDto(link.WorkQid, link.WorkLabel, link.LinkType, link.AppearanceRole, link.WorkContext, link.AnchorKind, link.AnchorValue, link.NarrativeTimeIndex, link.StartTime, link.EndTime, link.SpoilerForWorkQid, link.Provenance)));
    }

    private static async Task<IResult> GetRelationshipsAsync(string qid, Guid id, HttpContext http, IFictionalEntityRepository entities, IEntityRelationshipRepository relationships, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var entity = await AuthorizedEntityAsync(qid, id, http, entities, display, authorization, ApplicationPermissionIds.MetadataRead, ct);
        if (entity is null)
        {
            return ApiErrors.NotFound("Entity not found.");
        }
        var visible = await VisibleWorksAsync(display, ct);
        return Results.Ok((await relationships.GetByEntityAsync(entity.WikidataQid, ct)).Where(row => string.IsNullOrWhiteSpace(row.ContextWorkQid) || visible.Contains(row.ContextWorkQid!)).Select(row => new SharedEntityRelationshipDto(row.StatementKey, row.SubjectQid, row.RelationshipTypeValue, row.ObjectQid, row.Provenance, row.ContextWorkQid, row.Qualifiers.Select(q => new UniverseGraphQualifierDto(q.QualifierType, q.Value, q.ValueKind, q.Provenance, q.IsSupplemental, q.SourceProvider, q.Confidence)).ToList())));
    }

    private static async Task<IResult> GetTimelineAsync(string qid, Guid id, HttpContext http, IFictionalEntityRepository entities, IEntityRelationshipRepository relationships, ICanonicalValueRepository canonicals, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var entity = await AuthorizedEntityAsync(qid, id, http, entities, display, authorization, ApplicationPermissionIds.MetadataRead, ct);
        if (entity is null)
        {
            return ApiErrors.NotFound("Entity not found.");
        }
        var visible = await VisibleWorksAsync(display, ct);
        var appearances = (await entities.GetWorkLinksAsync(entity.Id, ct)).Where(link => visible.Contains(link.WorkQid) && (!string.IsNullOrWhiteSpace(link.NarrativeTimeIndex) || !string.IsNullOrWhiteSpace(link.StartTime) || !string.IsNullOrWhiteSpace(link.EndTime))).Select(link => new SharedEntityTimelineEntryDto("appearance", link.NarrativeTimeIndex ?? link.WorkLabel ?? link.WorkQid, link.StartTime, link.EndTime, link.WorkQid, link.Provenance));
        return Results.Ok(appearances.Concat(RelationshipTimeline(await relationships.GetByEntityAsync(entity.WikidataQid, ct), visible)).Concat(CanonicalTimeline(entity, await canonicals.GetByEntityAsync(entity.Id, ct))));
    }

    private static async Task<IResult> GetSourcesAsync(string qid, Guid id, HttpContext http, IFictionalEntityRepository entities, IEntityRelationshipRepository relationships, IPluginLoreRepository pluginLore, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var entity = await AuthorizedEntityAsync(qid, id, http, entities, display, authorization, ApplicationPermissionIds.MetadataRead, ct);
        if (entity is null)
        {
            return ApiErrors.NotFound("Entity not found.");
        }
        var visible = await VisibleWorksAsync(display, ct);
        var appearances = (await entities.GetWorkLinksAsync(entity.Id, ct)).Where(link => visible.Contains(link.WorkQid)).Select(link => new SharedEntitySourceDto("appearance", link.AppearanceKey ?? link.WorkQid, link.Provenance, link.SourceProvider, link.WorkQid, link.IsSupplemental, link.Confidence));
        return Results.Ok(appearances.Concat(RelationshipSources(await relationships.GetByEntityAsync(entity.WikidataQid, ct), visible)).Concat(await SupplementalSourcesAsync(qid, entity.WikidataQid, pluginLore, ct)));
    }

    private static async Task<IResult> GetHistoryAsync(string qid, Guid id, HttpContext http, IFictionalEntityRepository entities, IEntityTimelineRepository timeline, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var entity = await AuthorizedEntityAsync(qid, id, http, entities, display, authorization, ApplicationPermissionIds.MetadataRead, ct);
        if (entity is null)
        {
            return ApiErrors.NotFound("Entity not found.");
        }
        return Results.Ok((await timeline.GetEventsByEntityAsync(entity.Id, ct)).Select(evt => new SharedEntityHistoryEntryDto(evt.Id, evt.EventType, evt.OccurredAt, evt.Detail)));
    }

    private static async Task<IResult> GetEnrichmentAsync(string qid, Guid id, HttpContext http, IFictionalEntityRepository entities, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, CancellationToken ct)
    {
        var entity = await AuthorizedEntityAsync(qid, id, http, entities, display, authorization, ApplicationPermissionIds.MetadataRead, ct);
        return entity is null ? ApiErrors.NotFound("Entity not found.") : Results.Ok(new SharedEntityEnrichmentStatusDto(entity.EnrichedAt is null ? "pending" : "available", entity.EnrichedAt, entity.WikidataRevisionId));
    }

    private static async Task<IResult> RefreshEntityAsync(string qid, Guid id, HttpContext http, IFictionalEntityRepository entities, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, IMetadataHarvestingService harvesting, CancellationToken ct)
    {
        var entity = await AuthorizedEntityAsync(qid, id, http, entities, display, authorization, ApplicationPermissionIds.MetadataEnrichmentRun, ct);
        if (entity is null)
        {
            return ApiErrors.NotFound("Entity not found.");
        }
        await harvesting.EnqueueAsync(new HarvestRequest
        {
            EntityId = entity.Id,
            EntityType = ToHarvestEntityType(entity.EntitySubType),
            MediaType = MediaType.Unknown,
            Hints = new Dictionary<string, string>
            {
                [BridgeIdKeys.WikidataQid] = entity.WikidataQid,
                ["fictional_entity_qid"] = entity.WikidataQid,
                ["refresh"] = "shared_editor",
            },
        }, ct);
        return Results.Ok(new SharedEntityRefreshDto(true, "Entity enrichment refresh queued."));
    }

    private static async Task<NarrativeRoot?> AuthorizedRootAsync(string qid, HttpContext http, INarrativeRootRepository roots, IFictionalEntityRepository entities, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, ApplicationPermissionId permission, CancellationToken ct)
    {
        var root = await roots.FindByQidAsync(qid, ct);
        if (root is null)
        {
            return null;
        }
        // A root may be valid before Stage 3 has discovered its first entity. Authorize
        // through the owned work that carries its canonical narrative provenance, never
        // merely because a caller supplied a raw root QID.
        var candidateWorkIds = await roots.FindWorkIdsByProvenanceQidAsync(qid, ct);
        foreach (var workId in candidateWorkIds)
        {
            if (await authorization.EvaluateEntityAsync(http, "Work", workId, permission, ct) == CatalogueResourceAccess.Allowed)
            {
                return root;
            }
        }
        return null;
    }

    private static async Task<FictionalEntity?> AuthorizedEntityAsync(string qid, Guid id, HttpContext http, IFictionalEntityRepository entities, IDisplayProjectionReadService display, CatalogueResourceAuthorizationService authorization, ApplicationPermissionId permission, CancellationToken ct)
    {
        var entity = await entities.FindByIdAsync(id, ct);
        if (entity is null || !string.Equals(entity.FictionalUniverseQid, qid, StringComparison.OrdinalIgnoreCase) || await authorization.EvaluateEntityAsync(http, SharedEntityEditorTargetKinds.FictionalEntity, id, permission, ct) != CatalogueResourceAccess.Allowed)
        {
            return null;
        }
        return (await entities.SearchVisibleByUniverseAsync(qid, await VisibleWorksAsync(display, ct), null, entity.WikidataQid, 0, 1, ct)).Items.Any(item => item.Id == id) ? entity : null;
    }

    private static async Task<IResult> UpsertUserArtworkAsync(string entityId, string entityType, SharedEntityArtworkUpdateRequest request, IEntityAssetRepository assets, CancellationToken ct)
    {
        var normalizedType = ArtworkScopeService.NormalizeUploadedArtworkType(request.asset_type);
        if (normalizedType is null || string.IsNullOrWhiteSpace(request.image_url) || !Uri.TryCreate(request.image_url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ApiErrors.BadRequest("Artwork must use a supported type and an absolute HTTP(S) URL.");
        }
        var asset = new EntityAsset { EntityId = entityId, EntityType = entityType, AssetTypeValue = normalizedType, ImageUrl = request.image_url, SourceProvider = "user_upload", IsUserOverride = true, IsPreferred = request.preferred, OwnerScope = entityType };
        await assets.UpsertAsync(asset, ct);
        if (request.preferred)
        {
            await assets.SetPreferredAsync(asset.Id, ct);
        }
        return Results.Ok(ToArtwork(await assets.GetByEntityAsync(entityId, null, ct)));
    }

    private static async Task<IResult> UploadArtworkAsync(string entityId, string entityType, Guid ownerId, string assetType, HttpRequest request, IEntityAssetRepository assets, IEntityTimelineRepository timeline, ArtworkScopeService artworkScope, CancellationToken ct)
    {
        var normalizedType = ArtworkScopeService.NormalizeUploadedArtworkType(assetType);
        if (normalizedType is null)
        {
            return ApiErrors.BadRequest("Unsupported artwork type.");
        }
        if (!request.HasFormContentType)
        {
            return ApiErrors.BadRequest("Expected multipart form data.");
        }
        var file = (await request.ReadFormAsync(ct)).Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            return ApiErrors.BadRequest("No file provided.");
        }
        if (file.Length > BoundedHttpContent.MaximumImageBytes || !ArtworkScopeService.IsArtworkUploadAllowed(file.ContentType, normalizedType))
        {
            return ApiErrors.BadRequest("Artwork must be a JPEG or PNG image no larger than 20 MB.");
        }
        var variantId = Guid.NewGuid();
        var localPath = artworkScope.BuildArtworkUploadPath(entityType, ownerId, normalizedType, variantId, file.ContentType);
        AssetPathService.EnsureDirectory(localPath);
        await using (var input = file.OpenReadStream())
        {
            await BoundedHttpContent.CopyImageToFileAtomicallyAsync(input, localPath, ct);
        }
        var asset = new EntityAsset { Id = variantId, EntityId = entityId, EntityType = entityType, AssetTypeValue = normalizedType, ImageUrl = $"/stream/artwork/{variantId}", LocalImagePath = localPath, SourceProvider = "user_upload", OwnerScope = entityType, IsPreferred = true, IsUserOverride = true, CreatedAt = DateTimeOffset.UtcNow };
        await assets.UpsertAsync(asset, ct);
        await assets.SetPreferredAsync(asset.Id, ct);
        await RecordEditorEventAsync(timeline, ownerId, entityType, "user_artwork_edit", "Managed artwork uploaded in shared editor.", ct);
        return Results.Ok(ToArtwork(await assets.GetByEntityAsync(entityId, null, ct)));
    }

    private static SharedEntityEditorContextDto ToEntityContext(FictionalEntity entity, string universeQid, string universeLabel) => new(new(SharedEntityEditorTargetKinds.FictionalEntity, universeQid, entity.Id, entity.WikidataQid), entity.Label, entity.EntitySubType, EntityCapabilities(entity.EntitySubType), entity.EnrichedAt is null ? "pending" : "available", [universeLabel, entity.Label]);
    private static SharedEntityDetailsDto ToRootDetails(NarrativeRoot root) => new(null, root.Qid, root.Label, "Universe", root.Description, root.Qid, root.Label);
    private static SharedEntityDetailsDto ToEntityDetails(FictionalEntity entity, string universeQid) => new(entity.Id, entity.WikidataQid, entity.Label, entity.EntitySubType, entity.Description, universeQid, entity.FictionalUniverseLabel);
    private static SharedEntitySelectorItemDto ToSelector(FictionalEntity entity) => new(entity.Id, entity.WikidataQid, entity.Label, entity.EntitySubType, entity.Description);
    private static IReadOnlyList<SharedEntityArtworkDto> ToArtwork(IReadOnlyList<EntityAsset> assets) => assets.Select(asset => new SharedEntityArtworkDto(asset.Id, asset.AssetTypeValue, asset.ImageUrl, asset.IsPreferred, asset.IsUserOverride, asset.SourceProvider)).ToList();
    private static Category? ResolveCategory(string? value) => string.IsNullOrWhiteSpace(value) ? null : Categories.FirstOrDefault(category => category.Id.Equals(value, StringComparison.OrdinalIgnoreCase));
    private static IReadOnlyList<SharedEntityEditorCapabilityDto> RootCapabilities() => [new(SharedEntityEditorSections.Details, true, true), new(SharedEntityEditorSections.Artwork, true, true), new(SharedEntityEditorSections.Entities, true, false), new(SharedEntityEditorSections.Relationships, true, false), new(SharedEntityEditorSections.Timeline, true, false), new(SharedEntityEditorSections.Sources, true, false), new(SharedEntityEditorSections.History, true, false), new(SharedEntityEditorSections.Enrichment, true, false)];
    private static IReadOnlyList<SharedEntityEditorCapabilityDto> EntityCapabilities(string subtype)
    {
        var subjectSection = subtype switch
        {
            "Organization" => SharedEntityEditorSections.Members,
            "Event" => SharedEntityEditorSections.Participants,
            _ => SharedEntityEditorSections.Appearances,
        };
        var capabilities = new List<SharedEntityEditorCapabilityDto>
        {
            new(SharedEntityEditorSections.Details, true, true),
            new(SharedEntityEditorSections.Artwork, true, true),
            new(subjectSection, true, false, "Refreshed from enrichment."),
            new(SharedEntityEditorSections.Relationships, true, false),
            new(SharedEntityEditorSections.Timeline, true, false),
        };
        if (subtype is "Organization" or "Event")
        {
            capabilities.Add(new(SharedEntityEditorSections.Appearances, true, false, "Refreshed from enrichment."));
        }
        capabilities.Add(new(SharedEntityEditorSections.Sources, true, false));
        capabilities.Add(new(SharedEntityEditorSections.History, true, false));
        capabilities.Add(new(SharedEntityEditorSections.Enrichment, true, false));
        return capabilities;
    }
    private static async Task<HashSet<string>> VisibleWorksAsync(IDisplayProjectionReadService display, CancellationToken ct) => (await display.LoadWorksAsync(ct)).Select(work => work.IdentityQid).Where(qid => !string.IsNullOrWhiteSpace(qid)).Select(qid => qid!).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static async Task<IReadOnlyList<FictionalEntity>> LoadAllVisibleEntitiesAsync(string universeQid, IFictionalEntityRepository entities, IReadOnlyCollection<string> visibleWorks, CancellationToken ct)
    {
        const int pageSize = 100;
        var offset = 0;
        var result = new List<FictionalEntity>();
        while (true)
        {
            var page = await entities.SearchVisibleByUniverseAsync(universeQid, visibleWorks, null, null, offset, pageSize, ct);
            result.AddRange(page.Items);
            offset += page.Items.Count;
            if (offset >= page.Total || page.Items.Count == 0)
            {
                return result;
            }
        }
    }
    private static IEnumerable<SharedEntityTimelineEntryDto> RelationshipTimeline(IEnumerable<EntityRelationship> rows, IReadOnlySet<string> visibleWorks) => rows.Where(row => string.IsNullOrWhiteSpace(row.ContextWorkQid) || visibleWorks.Contains(row.ContextWorkQid!)).SelectMany(row =>
    {
        var qualifierEntries = row.Qualifiers
            .Where(q => q.QualifierType is GraphQualifierType.PointInTime or GraphQualifierType.StartTime or GraphQualifierType.EndTime or GraphQualifierType.TimeIndex)
            .Select(q => new SharedEntityTimelineEntryDto("relationship", q.Value, q.QualifierType == GraphQualifierType.StartTime ? q.Value : row.StartTime, q.QualifierType == GraphQualifierType.EndTime ? q.Value : row.EndTime, row.ContextWorkQid, q.Provenance));

        return string.IsNullOrWhiteSpace(row.StartTime) && string.IsNullOrWhiteSpace(row.EndTime)
            ? qualifierEntries
            : qualifierEntries.Append(new SharedEntityTimelineEntryDto("relationship", row.StatementKey, row.StartTime, row.EndTime, row.ContextWorkQid, row.Provenance));
    });
    private static IEnumerable<SharedEntitySourceDto> RelationshipSources(IEnumerable<EntityRelationship> rows, IReadOnlySet<string> visibleWorks) => rows.Where(row => string.IsNullOrWhiteSpace(row.ContextWorkQid) || visibleWorks.Contains(row.ContextWorkQid!)).SelectMany(row => new[] { new SharedEntitySourceDto("relationship", row.StatementKey, row.Provenance, row.SourceProvider, row.ContextWorkQid, row.IsSupplemental, row.Confidence) }.Concat(row.Qualifiers.Select(q => new SharedEntitySourceDto("qualifier", q.QualifierType + ":" + q.Value, q.Provenance, q.SourceProvider, row.ContextWorkQid, q.IsSupplemental, q.Confidence))));

    internal static IEnumerable<SharedEntityTimelineEntryDto> CanonicalTimeline(FictionalEntity entity, IReadOnlyList<CanonicalValue> values) => values
        .Where(value => value.Key is "point_in_time" or "start_time" or "end_time" or "time_index")
        .Select(value => new SharedEntityTimelineEntryDto(
            "entity",
            value.Value,
            value.Key == "start_time" ? value.Value : null,
            value.Key == "end_time" ? value.Value : null,
            null,
            "Wikidata"));

    private static async Task<IReadOnlyList<SharedEntitySourceDto>> SupplementalSourcesAsync(string universeQid, string? entityQid, IPluginLoreRepository pluginLore, CancellationToken ct)
    {
        const int limit = 100;
        var sources = await pluginLore.GetApprovedSourcesAsync(universeQid, ct);
        if (!string.IsNullOrWhiteSpace(entityQid))
        {
            var sourceIds = new HashSet<Guid>();
            foreach (var entity in await pluginLore.GetEntitiesAsync(universeQid, approvedOnly: true, ct))
            {
                if (string.Equals(entity.WikidataQid, entityQid, StringComparison.OrdinalIgnoreCase))
                {
                    sourceIds.Add(entity.SourceId);
                }
            }
            foreach (var relationship in await pluginLore.GetRelationshipsAsync(universeQid, approvedOnly: true, ct))
            {
                if (string.Equals(relationship.SubjectQid, entityQid, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(relationship.ObjectQid, entityQid, StringComparison.OrdinalIgnoreCase))
                {
                    sourceIds.Add(relationship.SourceId);
                }
            }
            sources = sources.Where(source => sourceIds.Contains(source.Id)).ToList();
        }

        return sources
            .OrderBy(source => source.PluginId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.SourceName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(source => new SharedEntitySourceDto("plugin_lore", source.SourceName, "Plugin", source.PluginId, null, true, source.Confidence, source.BaseUrl))
            .ToList();
    }
    private static EntityType ToHarvestEntityType(string category) => category switch
    {
        "Character" => EntityType.Character,
        "Location" => EntityType.Location,
        "Organization" => EntityType.Organization,
        "Event" => EntityType.Event,
        "Object" => EntityType.Object,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unsupported fictional entity category."),
    };
    private static Guid RootHistoryId(string qid) => new(MD5.HashData(Encoding.UTF8.GetBytes("narrative-root:" + qid.Trim().ToUpperInvariant())));
    private static Task RecordEditorEventAsync(IEntityTimelineRepository timeline, Guid entityId, string entityType, string eventType, string detail, CancellationToken ct) => timeline.InsertEventAsync(new EntityEvent { EntityId = entityId, EntityType = entityType, EventType = eventType, Trigger = "api_request", Detail = detail }, ct);
}
