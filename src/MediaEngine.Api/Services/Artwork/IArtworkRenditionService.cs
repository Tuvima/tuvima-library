using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Services.Artwork;

/// <summary>A picture file chosen for a request, and whether it is the exact size that was asked for.</summary>
/// <param name="Path">The file to send.</param>
/// <param name="IsRequestedSize">
/// True when this is the copy of the size asked for (or the original when no size was asked for).
/// False when it is a stand-in (a bigger copy, or the original because a copy could not be made),
/// which a browser must not keep for long.
/// </param>
public sealed record ResolvedArtworkFile(string Path, bool IsRequestedSize);

/// <summary>
/// The one place that picks the file for a picture at a size (right-sized pictures spec, D3/D5):
/// the exact copy when it exists; otherwise the next bigger copy while the missing one is made in the
/// background; and when only the original exists, the copies are made first and then the asked size is sent.
/// </summary>
public interface IArtworkRenditionService
{
    /// <summary>
    /// Picks the file for <paramref name="asset"/> at <paramref name="size"/> (<c>t</c>, <c>s</c>, <c>m</c> or <c>l</c>).
    /// A missing, empty or unrecognised size means the original. Returns null when the asset has no file at all.
    /// </summary>
    Task<ResolvedArtworkFile?> ResolveAsync(EntityAsset asset, string? size, CancellationToken ct);

    /// <summary>
    /// Creates the copies the stored asset is missing from its original and records them. Used by the background
    /// queue; a no-op when the asset is gone or already complete.
    /// </summary>
    Task CreateMissingCopiesAsync(Guid assetId, CancellationToken ct);
}
