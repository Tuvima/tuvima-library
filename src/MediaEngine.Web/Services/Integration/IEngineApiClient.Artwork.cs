using MediaEngine.Contracts.Artwork;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    Task<ProviderArtworkDiscoveryDto?> DiscoverProviderArtworkAsync(Guid entityId, string scope, string role, CancellationToken ct = default);
    Task<ProviderArtworkImportResultDto?> ImportProviderArtworkAsync(Guid entityId, string scope, string role, IReadOnlyList<string> ids, CancellationToken ct = default);
    Task<ArtworkBrowsePageDto?> GetArtworkLibraryAsync(
        string? entityKind = null,
        string? artworkType = null,
        string? search = null,
        string? browseAs = null,
        string? mediaType = null,
        string? artworkState = null,
        string? sort = null,
        int offset = 0,
        int limit = 48,
        CancellationToken ct = default);

    Task<ArtworkAssetPageDto?> GetArtworkAssetsAsync(string? search = null, string? role = null, string? aspect = null, Guid? targetEntityId = null, int offset = 0, int limit = 48, CancellationToken ct = default);
    Task<ArtworkAssetPageDto?> GetArtworkAssetsAsync(ArtworkAssetQuery request, CancellationToken ct = default);
    Task<IReadOnlyList<ArtworkLibraryItemDto>> GetUniverseArtworkHierarchyAsync(Guid collectionId, CancellationToken ct = default);
    Task<ArtworkEntityWorkspaceDto?> GetEntityArtworkAsync(string entityType, Guid entityId, string? mediaType = null, string? groupKind = null, IReadOnlyList<string>? assetTypes = null, CancellationToken ct = default);
    Task<EffectiveArtworkSelection?> GetEffectiveWorkArtworkAsync(Guid workId, string role, string? sourceAssetType = null, CancellationToken ct = default);
    Task<ArtworkWritebackStatusDto?> GetArtworkWritebackStatusAsync(Guid mediaAssetId, CancellationToken ct = default);
    Task<IReadOnlyList<ArtworkWritebackStatusDto>> GetArtworkWritebackStatusesAsync(IReadOnlyList<Guid> mediaAssetIds, CancellationToken ct = default);
    Task<ArtworkWritebackStatusDto?> RetryArtworkWritebackAsync(Guid mediaAssetId, CancellationToken ct = default);
    Task<ArtworkWritebackSettingsDto?> GetArtworkWritebackSettingsAsync(CancellationToken ct = default);
    Task<ArtworkWritebackSettingsDto?> UpdateArtworkWritebackSettingsAsync(UpdateArtworkWritebackSettingsDto settings, CancellationToken ct = default);
    Task<ArtworkEntityWorkspaceDto?> LinkArtworkAssetAsync(string entityType, Guid entityId, ArtworkLinkRequest request, CancellationToken ct = default);
    Task<ArtworkAssetDto?> AddArtworkFromUrlAsync(string entityType, Guid entityId, ArtworkFromUrlRequest request, CancellationToken ct = default);
    Task<ArtworkAssetDto?> UploadCanonicalArtworkAsync(string entityType, Guid entityId, string role, Stream stream, string fileName, string? entityLabel = null, string? mediaType = null, string? year = null, string? sourceAssetType = null, CancellationToken ct = default);
    Task<bool> RemoveArtworkLinkAsync(Guid linkId, CancellationToken ct = default);
}
