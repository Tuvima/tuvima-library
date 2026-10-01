using MediaEngine.Application.ReadModels;
using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Application.Services;

public interface IMediaEditorNavigationReadService
{
    Task<MediaEditorNavigatorEnvelope?> GetNavigatorAsync(
        Guid entityId,
        CancellationToken ct);
}

public interface IMediaEditorOwnedChildReadService
{
    /// <summary>Recomputes the same frozen selection revisions for arbitrary owned asset IDs.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetSelectionRevisionsForAssetsAsync(
        Guid parentEntityId, IReadOnlyList<Guid> assetIds, CancellationToken ct);
    Task<IReadOnlyList<MediaEditorAssetAccessSegment>> GetAccessSegmentsAsync(Guid parentEntityId, CancellationToken ct);
    Task<MediaEditorOwnedChildSelectionSnapshotEnvelope?> SnapshotMatchingAsync(
        Guid parentEntityId,
        string? query,
        int? season,
        int? disc,
        int? volume,
        string? matchStatus,
        string? fileStatus,
        CancellationToken ct,
        IReadOnlyList<string> allowedSegments);
    Task<MediaEditorOwnedChildSearchEnvelope?> SearchAsync(
        Guid parentEntityId,
        string? query,
        int page,
        int pageSize,
        int? season,
        int? disc,
        int? volume,
        string? matchStatus,
        string? fileStatus,
        CancellationToken ct,
        IReadOnlyList<string>? allowedSegments = null);
}

public interface IMediaEditorMembershipReadService
{
    Task<IReadOnlyList<MembershipSuggestionEnvelope>> GetSuggestionsAsync(
        Guid entityId,
        string field,
        string? query,
        string? source,
        Guid? parentEntityId,
        string? parentValue,
        CancellationToken ct);

    Task<MembershipPreviewEnvelope?> PreviewAsync(
        Guid entityId,
        MembershipPreviewRequest request,
        CancellationToken ct);

    Task<MembershipPreviewEnvelope?> ApplyAsync(
        Guid entityId,
        MembershipPreviewRequest request,
        CancellationToken ct);
}

/// <summary>
/// Plans and applies a metadata-driven hierarchy alignment. Preview is always
/// side-effect free; Apply owns the bounded SQLite transaction and returns the
/// same typed impact that the editor displayed before confirmation.
/// </summary>
public interface IHierarchyAlignmentService
{
    Task<MembershipPreviewEnvelope?> PreviewAsync(
        Guid entityId,
        MembershipPreviewRequest request,
        CancellationToken ct);

    Task<MembershipPreviewEnvelope?> ApplyAsync(
        Guid entityId,
        MembershipPreviewRequest request,
        CancellationToken ct);

    /// <summary>
    /// Applies a confirmed retail identity and its structural placement as one
    /// SQLite write. Enrichment is intentionally not queued here; callers do
    /// that only after this operation commits.
    /// </summary>
    Task<MembershipPreviewEnvelope?> ApplyRetailIdentityAsync(
        Guid entityId,
        MembershipPreviewRequest request,
        HierarchyIdentityMutation identityMutation,
        CancellationToken ct);
}

public interface IMetadataClaimHistoryReadService
{
    Task<List<ClaimDto>> GetClaimHistoryAsync(Guid entityId, CancellationToken ct);
}
