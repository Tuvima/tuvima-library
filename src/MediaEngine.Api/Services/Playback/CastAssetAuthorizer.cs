using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Services.Playback;

/// <summary>Whether a cast grant's authority may still play one asset: the catalogue's own rules, including a Kids content limit.</summary>
public interface ICastAssetAuthorizer
{
    ValueTask<bool> AllowsPlaybackAsync(RequestAuthority authority, Guid assetId, CancellationToken ct = default);
}

/// <summary>The real check: <c>playback.read</c> on the asset through the catalogue authorization service, evaluated live.</summary>
internal sealed class CatalogueCastAssetAuthorizer(CatalogueResourceAuthorizationService catalogue) : ICastAssetAuthorizer
{
    public async ValueTask<bool> AllowsPlaybackAsync(RequestAuthority authority, Guid assetId, CancellationToken ct = default) =>
        await catalogue.EvaluateAssetAsync(authority, assetId, ApplicationPermissionIds.PlaybackRead, ct).ConfigureAwait(false)
        == CatalogueResourceAccess.Allowed;
}
