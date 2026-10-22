namespace MediaEngine.Domain.Contracts;

/// <summary>
/// The one way anything asks for a write into a file (batched write-back D1). A change is saved in Library first, then calls this to
/// put the file on the durable to-do list; a scheduled run later writes the file from Library's values at that moment. Asking again
/// for a file that is already waiting joins its entry, so only the final version is written.
/// Nothing is queued when write-back is turned off or the trigger's own switch is off (<c>config/writeback.json</c>).
/// </summary>
public interface IFileWriteQueue
{
    /// <summary>Puts one media file on the list. An id that is not a media file is ignored.</summary>
    Task EnqueueAssetAsync(Guid assetId, string trigger, CancellationToken ct = default);

    /// <summary>Puts several media files on the list in one go.</summary>
    Task EnqueueAssetsAsync(IReadOnlyCollection<Guid> assetIds, string trigger, CancellationToken ct = default);

    /// <summary>Puts one View item's file on the list (the View writer arrives with the profile-name work).</summary>
    Task EnqueueViewItemAsync(Guid localItemId, string trigger, CancellationToken ct = default);
}
