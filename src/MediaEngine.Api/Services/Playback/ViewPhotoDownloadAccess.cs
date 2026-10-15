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
/// to keep is the same <see cref="PhotoKeepAccess.NotFound"/>, so ids cannot be probed. A photo the person may read whose
/// file is not on disk right now is <see cref="PhotoKeepAccess.FileMissing"/>, kept apart so a brief gap does not hide it.
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

        // No primary file row at all (the file source is gone for good) is not a photo to keep; a row whose file is not on
        // disk right now is only a gap (an unmounted volume), see FileMissing.
        var file = await resources.ResolveContentAsync(itemId, LocalAssetFileRoles.Primary, decision.Scope, ct).ConfigureAwait(false);
        if (file is null)
        {
            return PhotoKeepAccess.NotFound;
        }

        return File.Exists(file.FilePath) ? PhotoKeepAccess.Allowed : PhotoKeepAccess.FileMissing;
    }

    public async ValueTask<PhotoKeepBulkResult> EvaluateManyAsync(
        RequestAuthority authority, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        var decision = await authorization.AuthorizeManyAsync(authority, ViewResourceKind.Thumbnail, itemIds, ct).ConfigureAwait(false);
        if (decision.Outcome is ViewAccessOutcome.Forbidden or ViewAccessOutcome.Unauthenticated)
        {
            return new(true, new HashSet<Guid>());
        }

        // Readable and a photo or video, and still backed by a primary file row (a source that is gone for good ends the
        // photo). The file itself is deliberately not checked on disk here (see FileMissing).
        var kinds = assets.GetMediaKinds(decision.AllowedIds.ToList(), ct);
        var photos = decision.AllowedIds
            .Where(id => kinds.TryGetValue(id, out var kind) && kind is LocalAssetMediaKinds.Image or LocalAssetMediaKinds.Video)
            .ToList();
        var backed = await resources.FindItemsWithContentAsync(photos, LocalAssetFileRoles.Primary, ct).ConfigureAwait(false);
        return new(false, photos.Where(backed.Contains).ToHashSet());
    }
}
