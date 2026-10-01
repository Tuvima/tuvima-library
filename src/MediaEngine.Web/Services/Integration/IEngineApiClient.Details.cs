using System.Text.Json;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /collections — full collection list with works and canonical values.</summary>
    Task<List<CollectionViewModel>> GetCollectionsAsync(CancellationToken ct = default);

    /// <summary>GET /library/works — paged flat list of works with canonical values (excludes staging).</summary>
    Task<List<WorkViewModel>> GetLibraryWorksAsync(int offset = 0, int limit = 500, CancellationToken ct = default);

    /// <summary>GET /works/{id} — one work with editions and assets.</summary>
    Task<WorkDetailViewModel?> GetWorkDetailAsync(Guid workId, CancellationToken ct = default);

    /// <summary>GET /works/{id}/editions — editions and assets for one work.</summary>
    Task<List<EditionViewModel>> GetWorkEditionsAsync(Guid workId, CancellationToken ct = default);

    /// <summary>GET /api/details/{entityType}/{id}?context=... - unified detail-page model.</summary>
    Task<DetailPageViewModel?> GetDetailPageAsync(
        DetailEntityType entityType,
        Guid id,
        DetailPresentationContext context = DetailPresentationContext.Default,
        string? containerId = null,
        Guid? profileId = null,
        CancellationToken ct = default);

    Task<bool> SetDefaultSequenceAsync(
        DetailEntityType entityType,
        Guid id,
        string containerId,
        string? containerTitle = null,
        CancellationToken ct = default);

    // ── Item preferences (/library/items/{entityId}/preferences) ────────────

    /// <summary>PUT /library/items/{entityId}/preferences - save user-preferred fields without replacing external IDs.</summary>
    Task<bool> SaveItemPreferencesAsync(Guid entityId, Dictionary<string, string> fields, CancellationToken ct = default);

    // ── Cover Art Upload ───────────────────────────────────────────────────

    /// <summary>POST /metadata/{entityId}/cover — upload cover art for a media asset.</summary>
    Task<bool> UploadCoverAsync(Guid entityId, Stream fileStream, string fileName, CancellationToken ct = default);

    /// <summary>GET /metadata/{entityId}/editor-context — resolve scope-aware editor context.</summary>
    Task<MediaEditorContextDto?> GetMediaEditorContextAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>GET /metadata/{entityId}/navigator — resolve a series-aware media editor navigator.</summary>
    Task<MediaEditorNavigatorDto?> GetMediaEditorNavigatorAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>GET /metadata/{entityId}/work-versions — bounded Work/Edition/Asset selector preserving launch identity.</summary>
    Task<MediaEditorWorkVersionSelectorDto?> GetMediaEditorWorkVersionsAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>GET /metadata/{entityId}/owned-children — page local children for parent identity correction.</summary>
    Task<MediaEditorOwnedChildSearchDto?> GetMediaEditorOwnedChildrenAsync(
        Guid entityId, string? query = null, int page = 1, int pageSize = 50,
        int? season = null, int? disc = null, int? volume = null,
        string? matchStatus = null, string? fileStatus = null, CancellationToken ct = default);

    /// <summary>Freeze every matching authorized owned-file ID and revision for explicit selection.</summary>
    Task<MediaEditorOwnedChildSelectionSnapshotDto?> GetMediaEditorOwnedChildSelectionSnapshotAsync(
        Guid entityId, string? query = null, int? season = null, int? disc = null, int? volume = null,
        string? matchStatus = null, string? fileStatus = null, CancellationToken ct = default);

    /// <summary>Read-only parent-first episode or exact-release track pairing preview.</summary>
    Task<MediaEditorPairingPreviewDto?> PreviewMediaEditorPairingAsync(
        Guid entityId, MediaEditorPairingPreviewRequestDto request, CancellationToken ct = default);

    /// <summary>Search the reviewed TV show's frozen episode catalogue for one selected file.</summary>
    Task<MediaEditorPairingChildSearchDto?> SearchMediaEditorPairingChildrenAsync(
        Guid entityId, MediaEditorPairingChildSearchRequestDto request, CancellationToken ct = default);

    /// <summary>Save an explicitly reviewed, token-backed TV pairing plan.</summary>
    Task<MediaEditorPairingSaveResultDto?> SaveMediaEditorPairingAsync(
        Guid entityId, MediaEditorPairingSaveRequestDto request, CancellationToken ct = default);

    /// <summary>Review one owned music file against an exact MusicBrainz release.</summary>
    Task<MusicTrackMovePreview?> PreviewMusicTrackMoveAsync(
        Guid entityId, MusicTrackMovePreviewRequest request, CancellationToken ct = default);

    /// <summary>Apply one exact release-track choice from a current review.</summary>
    Task<MediaEditorPairingSaveResultDto?> SaveMusicTrackMoveAsync(
        Guid entityId, MusicTrackMoveSaveRequest request, CancellationToken ct = default);

    /// <summary>Review one managed episode still and the complete post-pairing file impact.</summary>
    Task<MediaEditorPairingArtworkPreviewDto?> PreviewMediaEditorPairingArtworkAsync(
        Guid entityId, MediaEditorPairingArtworkPreviewRequestDto request, CancellationToken ct = default);

    /// <summary>Review one same-show TV show/season preference and its full affected-file set.</summary>
    Task<MediaEditorPairingSharedArtworkPreviewDto?> PreviewMediaEditorPairingSharedArtworkAsync(
        Guid entityId, MediaEditorPairingSharedArtworkPreviewRequestDto request, CancellationToken ct = default);

    /// <summary>Review an Edition-level cover preference and its complete inheriting file set.</summary>
    Task<MediaEditorEditionCoverPreviewDto?> PreviewMediaEditorEditionCoverAsync(
        Guid entityId, MediaEditorEditionCoverPreviewRequestDto request, CancellationToken ct = default);

    /// <summary>Save a previously reviewed Edition-level cover preference.</summary>
    Task<MediaEditorEditionCoverSaveResultDto?> SaveMediaEditorEditionCoverAsync(
        Guid entityId, MediaEditorEditionCoverSaveRequestDto request, CancellationToken ct = default);

    /// <summary>GET /metadata/{entityId}/membership-suggestions — autocomplete targets for membership correction.</summary>
    Task<List<MediaEditorMembershipSuggestionDto>> GetMediaEditorMembershipSuggestionsAsync(
        Guid entityId,
        string field,
        string? query = null,
        string? source = null,
        Guid? parentEntityId = null,
        string? parentValue = null,
        CancellationToken ct = default);

    /// <summary>POST /metadata/{entityId}/membership-preview — preview hierarchy changes before applying them.</summary>
    Task<MediaEditorMembershipPreviewDto?> PreviewMediaEditorMembershipAsync(
        Guid entityId,
        MediaEditorMembershipPreviewRequestDto request,
        CancellationToken ct = default);

    /// <summary>POST /metadata/{entityId}/membership-apply — apply a confirmed hierarchy change.</summary>
    Task<MediaEditorMembershipPreviewDto?> ApplyMediaEditorMembershipAsync(
        Guid entityId,
        MediaEditorMembershipPreviewRequestDto request,
        CancellationToken ct = default);

    /// <summary>PUT /library/items/{entityId}/display-overrides — save presentation-only display overrides.</summary>
    Task<bool> SaveItemDisplayOverridesAsync(Guid entityId, Dictionary<string, string> fields, CancellationToken ct = default);
    Task<bool> SaveCollectionDisplayOverridesAsync(Guid collectionId, Dictionary<string, string> fields, CancellationToken ct = default);
    Task<MediaEngine.Contracts.Items.ItemEditorPreferencesResponse?> GetItemEditorPreferencesAsync(Guid entityId, Guid profileId, CancellationToken ct = default);
    Task<ItemEditorPreferencesSaveResultDto> SaveItemEditorPreferencesAsync(Guid entityId, Guid profileId, MediaEngine.Contracts.Items.ItemEditorPreferencesRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetItemEditorSuggestionsAsync(string field, Guid? profileId = null, CancellationToken ct = default);

    /// <summary>GET /metadata/{entityId}/artwork/{scopeId} — load exact artwork for one editor scope.</summary>
    Task<ArtworkEditorDto?> GetScopeArtworkAsync(Guid entityId, string scopeId, CancellationToken ct = default);

    Task<ProviderArtworkRefreshDto?> RefreshScopeProviderArtworkAsync(Guid entityId, string scopeId, CancellationToken ct = default);

    /// <summary>POST /metadata/{entityId}/artwork/{scopeId}/{assetType} — append a new artwork variant for a scope owner.</summary>
    Task<bool> UploadScopeArtworkVariantAsync(Guid entityId, string scopeId, string assetType, Stream fileStream, string fileName, CancellationToken ct = default);

    /// <summary>POST /metadata/{entityId}/artwork/{scopeId}/{assetType}/from-url — append a new artwork variant for a scope owner from a remote image URL.</summary>
    Task<bool> UploadScopeArtworkFromUrlAsync(Guid entityId, string scopeId, string assetType, string imageUrl, CancellationToken ct = default);

    /// <summary>PUT /metadata/artwork/{variantId}/preferred — set the preferred artwork variant.</summary>
    Task<bool> SetPreferredArtworkAsync(Guid variantId, CancellationToken ct = default);

    /// <summary>DELETE /metadata/artwork/{variantId} — remove an artwork variant from the current item.</summary>
    Task<bool> DeleteArtworkAsync(Guid variantId, CancellationToken ct = default);

    // ── Parent Collection hierarchy (/collections/parents, /collections/{id}/children, /collections/{id}/parent) ──

    /// <summary>GET /collections/parents — returns all Parent Collections (franchise-level groupings).</summary>
    Task<List<CollectionViewModel>> GetParentCollectionsAsync(CancellationToken ct = default);

    /// <summary>GET /collections/{id}/children — returns child Collections of the given Parent Collection.</summary>
    Task<List<CollectionViewModel>> GetChildCollectionsAsync(Guid parentCollectionId, CancellationToken ct = default);

    /// <summary>GET /collections/{id}/parent — returns the Parent Collection of the given Collection, if any.</summary>
    Task<CollectionViewModel?> GetParentCollectionAsync(Guid collectionId, CancellationToken ct = default);

    // ── Library Overview ──

    /// <summary>GET /library/overview - aggregated operational health summary.</summary>
    Task<LibraryOverviewDto?> GetLibraryOverviewAsync(CancellationToken ct = default);

    /// <summary>POST /library/batch-edit - apply batch field edits to multiple items.</summary>
    Task<LibraryBatchEditResult?> BatchEditAsync(
        List<Guid> entityIds, Dictionary<string, string> fieldChanges, CancellationToken ct = default);

}
