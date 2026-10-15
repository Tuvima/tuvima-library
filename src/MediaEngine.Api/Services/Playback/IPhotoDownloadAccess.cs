using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Services.Playback;

public enum PhotoKeepAccess
{
    Allowed,

    /// <summary>Unknown, someone else's, a document, not a photo or video, or without a file: all the same answer, so ids cannot be probed.</summary>
    NotFound,

    /// <summary>The token or its Application does not hold the View scope that reads photos.</summary>
    ScopeMissing,

    /// <summary>
    /// The person may read the photo and it is in the index, but its file is not on disk right now (a library volume that is
    /// unmounted for a while). Keeping it is refused like an unknown id (the preview route answers 404 too), but a photo
    /// already kept stays on the list, so a device does not delete its copy over a temporary gap.
    /// </summary>
    FileMissing,
}

/// <summary>The photos among those asked about that the active person may still read (a missing file does not hide one), or that the token lacks the photo scope.</summary>
public sealed record PhotoKeepBulkResult(bool ScopeMissing, IReadOnlySet<Guid> Visible);

/// <summary>Whether the paired app's active person may keep one of their View photos or videos on the device.</summary>
public interface IPhotoDownloadAccess
{
    ValueTask<PhotoKeepAccess> EvaluateAsync(RequestAuthority authority, Guid itemId, CancellationToken ct = default);

    /// <summary>
    /// Which of these photos the person may still read, decided for the whole list at once: the capability and scope are
    /// resolved once and the items are looked up in bulk, with the same visibility as <see cref="EvaluateAsync"/> (an item is
    /// visible when that answers <see cref="PhotoKeepAccess.Allowed"/> or <see cref="PhotoKeepAccess.FileMissing"/>). No file
    /// is checked on disk.
    /// </summary>
    ValueTask<PhotoKeepBulkResult> EvaluateManyAsync(
        RequestAuthority authority, IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default);
}
