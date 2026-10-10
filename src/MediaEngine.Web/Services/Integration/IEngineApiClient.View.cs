using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    Task<ViewScopeResolutionDto?> GetViewScopesAsync(ViewScopeKind? scope = null, Guid? scopeProfileId = null, CancellationToken ct = default);
    Task<ViewPreferencesDto?> GetViewPreferencesAsync(CancellationToken ct = default);
    /// <summary>Other households a server administrator can open, read-only. Refused (null) for everyone else.</summary>
    Task<ViewOtherPeopleDto?> GetViewOtherPeopleAsync(CancellationToken ct = default);
    /// <summary>Who opened the caller's photos (the whole household for a household administrator), newest first.</summary>
    Task<ViewPhotoViewsPageDto?> GetViewPhotoViewsAsync(int offset = 0, int limit = 25, CancellationToken ct = default);
    Task<LibraryCapacityDto?> GetLibraryCapacityAsync(string area, CancellationToken ct = default);
    Task<ViewPreferencesDto?> UpdateViewPreferencesAsync(ViewScopeKind scope, Guid? scopeProfileId, ViewTimelineDensity timelineDensity, bool viewerInfoOpen = true, CancellationToken ct = default);
    Task<ViewAssetTimelinePageDto?> GetViewAssetsAsync(ViewAssetQueryOptions options, CancellationToken ct = default);
    Task<ViewTimelineIndexDto?> GetViewTimelineIndexAsync(ViewAssetQueryOptions options, CancellationToken ct = default);
    Task<LocalAssetDto?> GetViewItemAsync(Guid itemId, ViewScopeKind scope, Guid? scopeProfileId = null, CancellationToken ct = default);
    Task<ViewFolderPageDto?> GetViewFoldersAsync(ViewFolderQueryOptions options, CancellationToken ct = default);
    Task<bool> SetViewFolderPinAsync(ViewFolderPinRequest request, CancellationToken ct = default);
    Task<bool> SetViewFolderTimelinePolicyAsync(ViewFolderTimelinePolicyRequest request, CancellationToken ct = default);
    Task<ViewPeoplePageDto?> GetViewPeopleAsync(ViewDiscoveryQueryOptions options, CancellationToken ct = default);
    Task<ViewPlacesPageDto?> GetViewPlacesAsync(ViewDiscoveryQueryOptions options, CancellationToken ct = default);
    Task<ViewAtlasPageDto?> GetViewAtlasAsync(ViewAtlasQueryOptions options, CancellationToken ct = default);
    Task<ViewPlaceMediaPageDto?> GetViewPlaceMediaAsync(string placeKey, ViewAtlasQueryOptions options, int offset = 0, int limit = 250, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetViewTagSuggestionsAsync(string? search = null, int limit = 20, CancellationToken ct = default);
    Task<ViewUploadResult> UploadViewMediaAsync(Stream fileStream, string fileName, string? contentType = null, CancellationToken ct = default);
    Task<bool> SetViewItemFavoriteAsync(Guid itemId, bool value, CancellationToken ct = default);
    Task<bool> SetViewItemHiddenAsync(Guid itemId, bool value, CancellationToken ct = default);
    Task<bool> ArchiveViewItemAsync(Guid itemId, CancellationToken ct = default);
    Task<bool> TrashViewItemAsync(Guid itemId, CancellationToken ct = default);
    Task<bool> RestoreViewItemAsync(Guid itemId, CancellationToken ct = default);
    Task<LocalAssetDto?> UpdateViewItemDescriptionAsync(Guid itemId, string? description, CancellationToken ct = default);
    Task<LocalAssetDto?> UpdateViewItemCapturedAtAsync(Guid itemId, UpdateLocalAssetCapturedAtRequest request, CancellationToken ct = default);
    Task<LocalAssetDto?> UpdateViewItemTagsAsync(Guid itemId, IReadOnlyCollection<string> tags, CancellationToken ct = default);
    Task<LocalAssetDto?> UpdateViewItemPeopleAsync(Guid itemId, IReadOnlyCollection<string> people, CancellationToken ct = default);
    Task<LocalAssetDto?> UpdateViewItemLocationAsync(Guid itemId, UpdateLocalAssetLocationRequest request, CancellationToken ct = default);
    Task<ViewSharedContributionPreviewDto?> PreviewViewSharedContributionAsync(ViewSharedContributionPreviewRequest request, CancellationToken ct = default);
    Task<ViewSharedContributionDto?> SubmitViewSharedContributionAsync(ViewSharedContributionSubmitRequest request, CancellationToken ct = default);
    Task<ViewSharedContributionPageDto?> GetViewSharedContributionsAsync(string mode = "mine", string? status = null, int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<ViewSharedContributionDto?> GetViewSharedContributionAsync(Guid contributionId, CancellationToken ct = default);
    Task<ViewSharedContributionDto?> CancelViewSharedContributionAsync(Guid contributionId, int expectedRevision, CancellationToken ct = default);
    Task<ViewSharedContributionDto?> DecideViewSharedContributionAsync(Guid contributionId, ViewSharedContributionDecisionRequest request, CancellationToken ct = default);
    Task<ViewSharedContributionDto?> RetryViewSharedContributionAsync(Guid contributionId, int expectedRevision, CancellationToken ct = default);
    Task<ViewSharedContributionDto?> AddViewItemsDirectlyToSharedLibraryAsync(ViewSharedDirectAddRequest request, CancellationToken ct = default);
    Task<ViewGalleryListResponse?> GetViewGalleriesAsync(CancellationToken ct = default);
    Task<ViewGalleryDto?> GetViewGalleryAsync(Guid galleryId, CancellationToken ct = default);
    Task<ViewGalleryDto?> CreateViewGalleryAsync(ViewGalleryRequest request, CancellationToken ct = default);
    Task<ViewGalleryDto?> UpdateViewGalleryAsync(Guid galleryId, ViewGalleryRequest request, CancellationToken ct = default);
    Task<bool> DeleteViewGalleryAsync(Guid galleryId, CancellationToken ct = default);
    Task<IReadOnlyList<ViewGalleryShareTargetDto>?> GetViewGalleryShareTargetsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ViewGalleryShareDto>?> GetViewGallerySharesAsync(Guid galleryId, CancellationToken ct = default);
    Task<bool> ReplaceViewGallerySharesAsync(Guid galleryId, IReadOnlyCollection<ViewGalleryShareRequest> shares, CancellationToken ct = default);
    Task<AddViewGalleryItemsResponseDto?> AddViewGalleryItemsAsync(Guid galleryId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);
    Task<ViewItemsRemovedResponse?> RemoveViewGalleryItemsAsync(Guid galleryId, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);
    Task<ViewPersonalSpaceAdminReviewDto?> GetViewProfileSourcesAsync(Guid profileId, CancellationToken ct = default);
    Task<ViewSourceAdminDto?> CreateViewProfileSourceAsync(Guid profileId, CreateViewSourceRequest request, CancellationToken ct = default);
    Task<ViewSourceAdminDto?> UpdateViewProfileSourceAsync(Guid profileId, Guid sourceId, UpdateViewSourceRequest request, CancellationToken ct = default);
    Task<bool> DeleteViewProfileSourceAsync(Guid profileId, Guid sourceId, CancellationToken ct = default);
    Task<LocalAssetScanResultDto?> ReconcileViewPersonalSpaceAsync(Guid profileId, CancellationToken ct = default);
}

public sealed record ViewAssetQueryOptions(
    ViewScopeKind Scope = ViewScopeKind.Shared,
    Guid? ScopeProfileId = null,
    string? Cursor = null,
    string? Search = null,
    IReadOnlyList<string>? Kinds = null,
    bool FavoritesOnly = false,
    bool HiddenOnly = false,
    string Lifecycle = "active",
    Guid? GalleryId = null,
    int Limit = 120,
    DateTimeOffset? AnchorBefore = null,
    bool WithoutLocation = false,
    string? PersonKey = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

public sealed record ViewUploadResult(bool Success, ViewUploadResponseDto? Upload = null, string? ErrorMessage = null);

public sealed record ViewDiscoveryQueryOptions(
    ViewScopeKind Scope = ViewScopeKind.Shared,
    Guid? ScopeProfileId = null,
    string? Search = null,
    string? Cursor = null,
    int Limit = 100);

public sealed record ViewAtlasQueryOptions(
    ViewScopeKind Scope = ViewScopeKind.Shared,
    Guid? ScopeProfileId = null,
    string? Search = null,
    int? Year = null,
    string? MediaKind = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string TimelineResolution = "year",
    bool FavoritesOnly = false);

public sealed record ViewFolderQueryOptions(
    ViewScopeKind Scope = ViewScopeKind.Shared,
    Guid? ScopeProfileId = null,
    Guid? SourceId = null,
    string? RelativePath = null,
    bool IncludeDescendants = false,
    string? Search = null,
    int Offset = 0,
    int Limit = 100);
