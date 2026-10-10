using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Stores which episodes a single physical file covers (see <see cref="MediaAssetCoverage"/>).
/// </summary>
public interface IMediaAssetCoverageRepository
{
    /// <summary>
    /// Replaces the full coverage list of <paramref name="assetId"/> in one transaction.
    /// An empty list clears coverage. Throws <see cref="ArgumentException"/> for duplicate
    /// works, duplicate or non-positive positions, an unknown source, or an end before its start.
    /// </summary>
    Task ReplaceForAssetAsync(Guid assetId, IReadOnlyList<MediaAssetCoverage> coverage, CancellationToken ct = default);

    /// <summary>Coverage rows for one file, ordered by position.</summary>
    Task<IReadOnlyList<MediaAssetCoverage>> ListByAssetAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>Coverage rows (files) that cover one episode.</summary>
    Task<IReadOnlyList<MediaAssetCoverage>> ListByWorkAsync(Guid workId, CancellationToken ct = default);

    /// <summary>Batch lookup of the files covering any of the given episodes (one query, not one per episode).</summary>
    Task<IReadOnlyList<MediaAssetCoverage>> ListByWorksAsync(IReadOnlyCollection<Guid> workIds, CancellationToken ct = default);

    /// <summary>Removes all coverage for one file.</summary>
    Task DeleteForAssetAsync(Guid assetId, CancellationToken ct = default);
}
