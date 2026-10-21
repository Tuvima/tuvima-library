using MediaEngine.Contracts.Library;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>
    /// GET /library/items/{itemId}/move-destinations - the places this item can be moved into, filtered to those that accept its kind.
    /// The outcome tells 200, 403, 404 and unreachable apart.
    /// </summary>
    Task<LibraryMoveOutcome<LibraryMoveDestinationsResponse>> GetLibraryMoveDestinationsAsync(
        Guid itemId, string? query, CancellationToken ct = default);

    /// <summary>
    /// POST /library/items/move-preview - what moving these items into the destination would do: the destination's own list, the Engine's
    /// best guess for each item and a token to confirm with. Changes nothing.
    /// </summary>
    Task<LibraryMoveOutcome<LibraryMovePreview>> PreviewLibraryMoveAsync(
        LibraryMovePreviewRequest request, CancellationToken ct = default);

    /// <summary>
    /// POST /library/items/move - moves the items as the person chose, confirming the preview by its token. The outcome tells 200, 400,
    /// 403 (the person may not change these items), 404, 409 (something changed: nothing was moved, <see cref="LibraryMoveOutcome{T}.FreshPreview"/>
    /// holds the updated preview), an expired preview and unreachable apart.
    /// </summary>
    Task<LibraryMoveOutcome<LibraryMoveCommitResult>> MoveLibraryItemsAsync(
        LibraryMoveCommitRequest request, CancellationToken ct = default);
}
