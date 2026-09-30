using MediaEngine.Api.Http;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Api.Services.Canonical;
using MediaEngine.Application.ReadModels;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Domain.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace MediaEngine.Api.Endpoints;

public static partial class MetadataEndpoints
{
    private static void MapMediaEditorNavigatorEndpoints(RouteGroupBuilder group)
    {
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
