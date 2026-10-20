using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// The only place a personal item's records are removed for good (Empty Trash). A group of items that share a file goes in one
/// transaction, and only while every item is still in its owner's Trash and still has only the indexed places the plan saw.
/// </summary>
public interface IViewTrashPurgeRepository
{
    /// <summary>
    /// Removes each item row (which clears details, tags, document fields, Gallery memberships, notes, search entries and offline
    /// download entries), the indexed places named in its request, and any file record left with no item and no place. All checks
    /// run before anything is changed; unless the outcome is <see cref="ViewTrashPurgeOutcome.Removed"/>, nothing was changed.
    /// </summary>
    Task<ViewTrashPurgeResult> PurgeItemsAsync(IReadOnlyList<ViewTrashPurgeRequest> requests, CancellationToken ct = default);

    /// <summary>Whether a personal item with this id is still in the data store, in Trash or not.</summary>
    Task<bool> ItemExistsAsync(Guid itemId, CancellationToken ct = default);
}
