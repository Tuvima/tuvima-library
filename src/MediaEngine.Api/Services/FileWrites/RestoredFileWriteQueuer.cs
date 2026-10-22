using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.FileWrites;

/// <summary>
/// A file put back from the Recycle bin gets a fresh entry on the file write list (batched write-back D11), so its tags are brought up
/// to date on the next run. A file whose item was deleted has no media asset any more: it comes back through ingestion, which queues
/// its own write, so nothing is added here for it.
/// </summary>
public sealed class RestoredFileWriteQueuer(IMediaAssetRepository assets, IFileWriteQueue fileWrites)
{
    /// <summary>Returns true when the restored file's item was still in the library and a write was queued for it.</summary>
    public async Task<bool> QueueAsync(string restoredPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(restoredPath) || await assets.FindByPathRootAsync(restoredPath, ct) is not { } asset)
        {
            return false;
        }

        await fileWrites.EnqueueAssetAsync(asset.Id, MediaFileWriteTriggers.Restore, ct);
        return true;
    }
}
