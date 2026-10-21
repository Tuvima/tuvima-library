using MediaEngine.Contracts.Items;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>POST /library/items/{id}/move-destinations - the places this title can move to (only places that accept this kind).</summary>
    Task<ItemMoveOutcome<ItemMoveDestinationsResponse>> FindItemMoveDestinationsAsync(
        Guid workId, string? query, CancellationToken ct = default);

    /// <summary>POST /library/items/{id}/move-preview - the destination's list, the Engine's best guess, the sentence to confirm and a fingerprint. Changes nothing.</summary>
    Task<ItemMoveOutcome<ItemMovePreview>> GetItemMovePreviewAsync(
        Guid workId, Guid destinationId, Guid? entryId, string? mode, CancellationToken ct = default);

    /// <summary>POST /library/items/{id}/move - moves the title the person confirmed, sending back the preview's fingerprint. A changed plan answers Conflict and nothing moves.</summary>
    Task<ItemMoveOutcome<ItemMoveResultDto>> MoveItemAsync(ItemMoveRequest request, CancellationToken ct = default);
}
