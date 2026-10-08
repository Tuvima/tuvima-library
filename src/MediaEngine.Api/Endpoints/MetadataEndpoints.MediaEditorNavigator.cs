using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Canonical;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Application.ReadModels;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Authorization;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private static void MapMediaEditorNavigatorEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/{entityId:guid}/work-versions", async (
            Guid entityId,
            MediaEditorWorkVersionReadRepository versions,
            CancellationToken ct) =>
        {
            var result = await versions.GetAsync(entityId, ct);
            return result is null
                ? ApiErrors.NotFound($"Work, edition, or asset {entityId} not found.")
                : Results.Ok(result);
        })
        .WithName("GetMediaEditorWorkVersions")
        .WithSummary("Return a bounded Work, meaningful Edition, and physical Asset selector while preserving the launch identity.")
        .Produces<MediaEditorWorkVersionSelectorDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapGet("/{entityId:guid}/navigator", async (
            Guid entityId,
            IMediaEditorNavigationReadService navigationReadService,
            CancellationToken ct) =>
        {
            var navigator = await navigationReadService.GetNavigatorAsync(entityId, ct);
            return navigator is null
                ? ApiErrors.NotFound($"Navigator for {entityId} not found.")
                : Results.Ok(ToContract(navigator));
        })
        .WithName("GetMediaEditorNavigator")
        .WithSummary("Resolve series-aware editor navigation for a launch entity.")
        .Produces<MediaEditorNavigatorDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead);

        group.MapGet("/{entityId:guid}/owned-children", async (
            Guid entityId,
            string? q,
            int? page,
            int? pageSize,
            int? season,
            int? disc,
            int? volume,
            string? matchStatus,
            string? fileStatus,
            IMediaEditorOwnedChildReadService ownedChildReadService,
            HttpContext http,
            CatalogueResourceAuthorizationService catalogueAuthorization,
            CancellationToken ct) =>
        {
            var segments = await ownedChildReadService.GetAccessSegmentsAsync(entityId, ct);
            var allowedSegments = new List<string>(segments.Count);
            foreach (var segment in segments)
            {
                if (await catalogueAuthorization.EvaluateAssetAsync(http, segment.RepresentativeAssetId, ApplicationPermissionIds.MetadataRead, ct)
                    == CatalogueResourceAccess.Allowed)
                {
                    allowedSegments.Add(segment.Key);
                }
            }
            var result = await ownedChildReadService.SearchAsync(
                entityId, q, page ?? 1, pageSize ?? 50, season, disc, volume,
                matchStatus, fileStatus, ct, allowedSegments);
            return result is null
                ? ApiErrors.NotFound($"Editor parent {entityId} not found.")
                : Results.Ok(ToContract(result));
        })
        .WithName("SearchMediaEditorOwnedChildren")
        .WithSummary("Page locally-owned media children beneath an editor parent.")
        .Produces<MediaEditorOwnedChildSearchDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapGet("/{entityId:guid}/owned-children/selection-snapshot", async (
            Guid entityId,
            string? q,
            int? season,
            int? disc,
            int? volume,
            string? matchStatus,
            string? fileStatus,
            IMediaEditorOwnedChildReadService ownedChildReadService,
            HttpContext http,
            CatalogueResourceAuthorizationService catalogueAuthorization,
            CancellationToken ct) =>
        {
            var segments = await ownedChildReadService.GetAccessSegmentsAsync(entityId, ct);
            var allowedSegments = new List<string>(segments.Count);
            foreach (var segment in segments)
            {
                if (await catalogueAuthorization.EvaluateAssetAsync(http, segment.RepresentativeAssetId,
                        ApplicationPermissionIds.MetadataRead, ct) == CatalogueResourceAccess.Allowed)
                {
                    allowedSegments.Add(segment.Key);
                }
            }

            var snapshot = await ownedChildReadService.SnapshotMatchingAsync(
                entityId, q, season, disc, volume, matchStatus, fileStatus, ct, allowedSegments);
            if (snapshot is null)
            {
                return ApiErrors.NotFound($"Editor parent {entityId} not found.");
            }
            if (snapshot.ExceedsLimit)
            {
                return ApiErrors.Conflict("More than 1,000 files match. Narrow the filters and try again.");
            }

            // Access can change while the read transaction runs. Check each included
            // library/media segment again before releasing its frozen asset list.
            foreach (var segment in snapshot.Items.GroupBy(item => $"{item.LibraryId}|{item.MediaType}"))
            {
                if (await catalogueAuthorization.EvaluateAssetAsync(http, segment.First().AssetId,
                        ApplicationPermissionIds.MetadataRead, ct) != CatalogueResourceAccess.Allowed)
                {
                    return ApiErrors.Forbidden("Access to one or more selected files changed.");
                }
            }

            return Results.Ok(new MediaEditorOwnedChildSelectionSnapshotDto
            {
                ParentEntityId = snapshot.ParentEntityId,
                Count = snapshot.Items.Count,
                Items = snapshot.Items.Select(item => new MediaEditorOwnedChildSelectionItemDto
                {
                    AssetId = item.AssetId,
                    SelectionRevision = item.SelectionRevision,
                }).ToList(),
            });
        })
        .WithName("SnapshotMediaEditorOwnedChildSelection")
        .WithSummary("Freeze up to 1,000 locally-owned files matching editor filters.")
        .Produces<MediaEditorOwnedChildSelectionSnapshotDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead)
        .RequireAnyCatalogueEntityAccess(ApplicationPermissionIds.MetadataRead);

        group.MapGet("/{entityId:guid}/membership-suggestions", async (
            Guid entityId,
            string field,
            string? query,
            string? source,
            Guid? parentEntityId,
            string? parentValue,
            IMediaEditorMembershipReadService membershipReadService,
            IHttpClientFactory httpFactory,
            IMemoryCache cache,
            CancellationToken ct) =>
        {
            var suggestions = await membershipReadService.GetSuggestionsAsync(entityId, field, query, source, parentEntityId, parentValue, ct);
            var results = suggestions.Select(ToContract).ToList();
            await Task.WhenAll(suggestions.Select(async (suggestion, index) =>
                results[index].PreviewUrl = suggestion.LocalExisting
                    ? suggestion.ImageUrl
                    : await RetailCandidateArtworkPreview.LoadAsync(
                        suggestion.ProviderName, suggestion.ImageUrl, httpFactory, cache, ct)));
            return Results.Ok(results);
        })
        .WithName("GetMediaEditorMembershipSuggestions")
        .WithSummary("Return same-media-type autocomplete targets for membership correction.")
        .Produces<IReadOnlyList<MediaEditorMembershipSuggestionDto>>(StatusCodes.Status200OK)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead);

        group.MapPost("/{entityId:guid}/membership-preview", async (
            Guid entityId,
            MediaEditorMembershipPreviewRequestDto request,
            IHierarchyAlignmentService hierarchyAlignment,
            CancellationToken ct) =>
        {
            var preview = await hierarchyAlignment.PreviewAsync(entityId, ToInternal(request), ct);
            return preview is null
                ? ApiErrors.NotFound($"Membership preview for {entityId} not found.")
                : Results.Ok(ToContract(preview));
        })
        .WithName("PreviewMediaEditorMembershipChange")
        .WithSummary("Preview a hierarchy move or parent identity rename before applying it.")
        .Produces<MediaEditorMembershipPreviewDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataRead);

        group.MapPost("/{entityId:guid}/membership-apply", async (
            Guid entityId,
            MediaEditorMembershipPreviewRequestDto request,
            IHierarchyAlignmentService hierarchyAlignment,
            CancellationToken ct) =>
        {
            var result = await hierarchyAlignment.ApplyAsync(entityId, ToInternal(request), ct);
            return result is null
                ? ApiErrors.NotFound($"Membership apply for {entityId} not found.")
                : Results.Ok(ToContract(result));
        })
        .WithName("ApplyMediaEditorMembershipChange")
        .WithSummary("Apply a confirmed hierarchy move or parent identity rename.")
        .Produces<MediaEditorMembershipPreviewDto>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite);
    }

    private static MediaEditorNavigatorDto ToContract(
        MediaEditorNavigatorEnvelope source) => new()
        {
            Enabled = source.Enabled,
            MediaType = source.MediaType,
            ContainerEntityId = source.ContainerEntityId,
            SelectedEntityId = source.SelectedEntityId,
            ContainerLabel = source.ContainerLabel,
            ContainerTitle = source.ContainerTitle,
            ContainerSubtitle = source.ContainerSubtitle,
            Nodes = source.Nodes.Select(node => new MediaEditorNavigatorNodeDto
            {
                NodeId = node.NodeId,
                ParentNodeId = node.ParentNodeId,
                EntityId = node.EntityId,
                ScopeId = node.ScopeId,
                NodeKind = node.NodeKind,
                Label = node.Label,
                Title = node.Title,
                Subtitle = node.Subtitle,
                OrdinalLabel = node.OrdinalLabel,
                Depth = node.Depth,
                IsRoot = node.IsRoot,
                IsLeaf = node.IsLeaf,
                IsOwned = node.IsOwned,
                PrimaryAssetId = node.PrimaryAssetId,
                ArtworkUrl = node.ArtworkUrl,
                ArtworkShape = node.ArtworkShape,
                CompactOrdinalLabel = node.CompactOrdinalLabel,
                TechnicalBadges = node.TechnicalBadges.ToList(),
                IsClickable = node.IsClickable,
                CanSelectAsEditorTarget = node.CanSelectAsEditorTarget,
                CanQuarantine = node.CanQuarantine,
                QuarantineCount = node.QuarantineCount,
            }).ToList(),
        };

    private static MediaEditorOwnedChildSearchDto ToContract(
        MediaEditorOwnedChildSearchEnvelope source) => new()
        {
            ParentEntityId = source.ParentEntityId,
            Page = source.Page,
            PageSize = source.PageSize,
            TotalCount = source.TotalCount,
            Items = source.Items.Select(item => new MediaEditorOwnedChildDto
            {
                AssetId = item.AssetId,
                EditionId = item.EditionId,
                EditionLabel = item.EditionLabel,
                EditionAssetCount = item.EditionAssetCount,
                WorkEditionCount = item.WorkEditionCount,
                CollapseEdition = item.CollapseEdition,
                EditionReleaseId = item.EditionReleaseId,
                IdentityOwnerEntityId = item.IdentityOwnerEntityId,
                ArtworkOwnerEntityId = item.ArtworkOwnerEntityId,
                MetadataOwnerEntityId = item.MetadataOwnerEntityId,
                SelectionNodeKind = item.SelectionNodeKind,
                WorkId = item.WorkId,
                ParentWorkId = item.ParentWorkId,
                RootWorkId = item.RootWorkId,
                StructuralParentId = item.StructuralParentId,
                SelectionRevision = item.SelectionRevision,
                Title = item.Title,
                MatchedTitle = item.MatchedTitle,
                MatchedNumber = item.MatchedNumber,
                SourceFileName = item.SourceFileName,
                SourceFilePath = item.SourceFilePath,
                MatchState = item.MatchState,
                FileState = item.FileState,
                SeasonNumber = item.SeasonNumber,
                DiscNumber = item.DiscNumber,
                VolumeNumber = item.VolumeNumber,
            }).ToList(),
        };

    private static MediaEditorMembershipSuggestionDto ToContract(
        MembershipSuggestionEnvelope source) => new()
        {
            EntityId = source.EntityId,
            Source = source.Source,
            LocalExisting = source.LocalExisting,
            Kind = source.Kind,
            Label = source.Label,
            Subtitle = source.Subtitle,
            ProviderName = source.ProviderName,
            ProviderItemId = source.ProviderItemId,
            ExternalIdKey = source.ExternalIdKey,
            ExternalIdValue = source.ExternalIdValue,
        };

    private static MediaEditorMembershipPreviewDto ToContract(
        MembershipPreviewEnvelope source) => new()
        {
            Action = source.Action,
            CurrentPath = source.CurrentPath,
            TargetPath = source.TargetPath,
            RequiresNewTarget = source.RequiresNewTarget,
            CanApply = source.CanApply,
            Applied = source.Applied,
            SelectedEntityId = source.SelectedEntityId,
            TargetRootEntityId = source.TargetRootEntityId,
            TargetParentEntityId = source.TargetParentEntityId,
            Message = source.Message,
            ConflictMessage = source.ConflictMessage,
            Stage2TargetEntityId = source.Stage2TargetEntityId,
            SourceParentWillBeEmpty = source.SourceParentWillBeEmpty,
        };

    private static MembershipPreviewRequest ToInternal(
        MediaEditorMembershipPreviewRequestDto source) => new(
            source.ScopeId,
            source.FieldValues,
            source.SelectedTargetIds,
            source.SelectedSuggestions?.ToDictionary(
                pair => pair.Key,
                pair => new MembershipSuggestionSelection(
                    pair.Value.EntityId,
                    pair.Value.Source,
                    pair.Value.LocalExisting,
                    pair.Value.Kind,
                    pair.Value.Label,
                    pair.Value.Subtitle,
                    pair.Value.ProviderName,
                    pair.Value.ProviderItemId,
                    pair.Value.ExternalIdKey,
                    pair.Value.ExternalIdValue),
                StringComparer.OrdinalIgnoreCase));
}
