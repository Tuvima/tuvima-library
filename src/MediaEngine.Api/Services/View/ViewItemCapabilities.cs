using MediaEngine.Contracts.LocalAssets;

namespace MediaEngine.Api.Services.View;

/// <summary>
/// Narrows the per-item flags the data store sets (which know the item, not who is asking) to what one viewer may do.
/// </summary>
public static class ViewItemCapabilities
{
    /// <summary>
    /// <c>can_trash</c> stays true only for the item's own owner: the data store already clears it for Shared Library
    /// items and items in a read-only folder, and Move to Trash needs the Mine scope and ownership.
    /// </summary>
    public static LocalAssetDto ForViewer(LocalAssetDto item, Guid? viewerProfileId)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.CanTrash && (viewerProfileId is null || item.OwnerProfileId != viewerProfileId)
            ? item with { CanTrash = false }
            : item;
    }

    public static IReadOnlyList<LocalAssetDto> ForViewer(IReadOnlyList<LocalAssetDto> items, Guid? viewerProfileId)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Select(item => ForViewer(item, viewerProfileId)).ToList();
    }
}
