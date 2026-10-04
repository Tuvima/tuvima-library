using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Web.Services.Integration;

/// <summary>View stays a View identity. Grants are minted anew on each adaptation and viewer open.</summary>
public sealed class RecentViewAssetAdapter(ViewMediaGrantService grants, IEngineApiClient api)
{
    public string ThumbnailUrl(DisplayRecentViewAssetDto asset, Guid profileId) => Url(asset.LibraryId, asset.AssetId, profileId, ViewMediaResourceKind.Thumbnail);
    public string PreviewUrl(LocalAssetDto asset, Guid profileId) => Url(asset.LibraryId, asset.Id, profileId, ViewMediaResourceKind.Preview);
    public string OriginalUrl(LocalAssetDto asset, Guid profileId) => Url(asset.LibraryId, asset.Id, profileId, ViewMediaResourceKind.Content);
    public string ThumbnailUrl(LocalAssetDto asset, Guid profileId) => Url(asset.LibraryId, asset.Id, profileId, ViewMediaResourceKind.Thumbnail);
    public Task<LocalAssetDto?> OpenAsync(DisplayRecentViewAssetDto asset, CancellationToken ct = default) => api.GetViewItemAsync(asset.AssetId, ViewScopeKind.Mine, ct: ct);
    private string Url(Guid libraryId, Guid assetId, Guid profileId, ViewMediaResourceKind kind) =>
        "/view-media/" + grants.Create(profileId, libraryId, assetId, kind, scopeKind: ViewScopeKind.Mine).Value;
}
