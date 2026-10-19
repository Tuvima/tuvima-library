namespace MediaEngine.Domain.Contracts;

/// <summary>
/// The durable "inspection pending" marker: a media file whose technical inspection is waiting on a missing tool.
/// A hold survives a restart and goes away with its asset.
/// </summary>
public interface IMediaInspectionHoldRepository
{
    /// <summary>Records (or refreshes) that the asset's inspection is waiting on <paramref name="toolId"/>.</summary>
    Task UpsertAsync(Guid assetId, string toolId, DateTimeOffset heldAt, CancellationToken ct = default);

    /// <summary>Removes the asset's hold, if it has one.</summary>
    Task DeleteAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>The oldest held assets for a tool, <paramref name="skip"/> rows in, at most <paramref name="limit"/> of them.</summary>
    Task<IReadOnlyList<Guid>> ListAssetIdsByToolAsync(string toolId, int limit, int skip = 0, CancellationToken ct = default);

    /// <summary>How many assets are held waiting for the tool.</summary>
    Task<int> CountByToolAsync(string toolId, CancellationToken ct = default);

    /// <summary>Whether the asset currently has a hold.</summary>
    Task<bool> ExistsAsync(Guid assetId, CancellationToken ct = default);
}
