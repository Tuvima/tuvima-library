using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Services.Playback;

public enum PhotoKeepAccess
{
    Allowed,

    /// <summary>Unknown, someone else's, a document, not a photo or video, or without a file: all the same answer, so ids cannot be probed.</summary>
    NotFound,

    /// <summary>The token or its Application does not hold the View scope that reads photos.</summary>
    ScopeMissing,
}

/// <summary>Whether the paired app's active person may keep one of their View photos or videos on the device.</summary>
public interface IPhotoDownloadAccess
{
    ValueTask<PhotoKeepAccess> EvaluateAsync(RequestAuthority authority, Guid itemId, CancellationToken ct = default);
}
