namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Keeps optional local artwork exports in sync with the canonical central asset store.
/// </summary>
public interface IAssetExportService
{
    /// <summary>
    /// Reconcile local export state for one artwork owner + type pair.
    /// <paramref name="replaceExistingFile"/> is true when the person just chose the picture: a poster, fanart or logo file that is
    /// already in the folder (one the Engine did not write itself) is replaced by the chosen picture instead of being left as it was.
    /// </summary>
    Task ReconcileArtworkAsync(
        string entityId,
        string entityType,
        string assetType,
        CancellationToken ct = default,
        bool replaceExistingFile = false);

    /// <summary>
    /// Remove any managed local export for one artwork owner + type pair.
    /// </summary>
    Task ClearArtworkExportAsync(
        string entityId,
        string entityType,
        string assetType,
        CancellationToken ct = default);

    /// <summary>
    /// Reconcile every tracked artwork slot. Used at startup after legacy migration.
    /// </summary>
    Task ReconcileAllArtworkAsync(CancellationToken ct = default);
}
