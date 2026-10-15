using MediaEngine.Api.Services.View;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.Playback;

/// <summary>
/// Whether a paired app's active person may keep one of their photos or videos on the device. It asks exactly what the
/// photo preview route asks: the View authorization service for the <see cref="ViewScopeRequest.Mine"/> scope (which also
/// requires <c>view.personal.read</c> on the token and its Application), then that the item is not a document (so a
/// profile without Documents learns nothing) and is a photo or video with a file on disk. Every reason there is nothing
/// to keep is the same <see cref="PhotoKeepAccess.NotFound"/>, so ids cannot be probed.
/// </summary>
public sealed class ViewPhotoDownloadAccess(
    IViewResourceAuthorizationService authorization,
    IViewResourceStore resources,
    ILocalAssetRepository assets) : IPhotoDownloadAccess
{
    public async ValueTask<PhotoKeepAccess> EvaluateAsync(RequestAuthority authority, Guid itemId, CancellationToken ct = default)
    {
        var decision = await authorization.AuthorizeAsync(
            authority, new ViewResourceRequest(ViewScopeRequest.Mine, ViewResourceKind.Thumbnail, itemId), ct).ConfigureAwait(false);
        if (decision.Outcome == ViewAccessOutcome.Forbidden || decision.Outcome == ViewAccessOutcome.Unauthenticated)
        {
            return PhotoKeepAccess.ScopeMissing;
        }

        if (!decision.IsAllowed || decision.Scope is null || assets.IsDocument(itemId, ct)
            || assets.Find(itemId, ct) is not { } item
            || item.MediaKind is not (LocalAssetMediaKinds.Image or LocalAssetMediaKinds.Video))
        {
            return PhotoKeepAccess.NotFound;
        }

        var file = await resources.ResolveContentAsync(itemId, LocalAssetFileRoles.Primary, decision.Scope, ct).ConfigureAwait(false);
        return file is not null && File.Exists(file.FilePath) ? PhotoKeepAccess.Allowed : PhotoKeepAccess.NotFound;
    }
}
