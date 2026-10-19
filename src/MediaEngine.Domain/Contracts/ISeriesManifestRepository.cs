using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Contracts;

public interface ISeriesManifestRepository
{
    /// <summary>The hydration of a Series QID for one media type (a book Series and an audiobook Series share a QID).</summary>
    Task<SeriesManifestHydration?> GetHydrationAsync(string seriesQid, string mediaType, CancellationToken ct = default);

    /// <summary>The stored items of a Series QID across every media type.</summary>
    Task<IReadOnlyList<SeriesManifestItemRecord>> GetItemsBySeriesQidAsync(
        string seriesQid,
        CancellationToken ct = default);

    /// <summary>The stored items of a Series QID for one media type.</summary>
    Task<IReadOnlyList<SeriesManifestItemRecord>> GetItemsBySeriesQidAsync(
        string seriesQid,
        string mediaType,
        CancellationToken ct = default);

    /// <summary>
    /// Every stored manifest row that names the work, with the shelf, media type and scope, ready for
    /// <see cref="SeriesPlacementRule"/>.
    /// </summary>
    Task<IReadOnlyList<SeriesManifestMembership>> GetMembershipsForWorkAsync(
        Guid workId,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, IReadOnlyList<Guid>>> FindWorkIdsByQidsAsync(
        IReadOnlyCollection<string> qids,
        CancellationToken ct = default);

    /// <summary>Owned works with these QIDs whose media type is <paramref name="mediaType"/> (a Series only holds its own media type).</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<Guid>>> FindWorkIdsByQidsAsync(
        IReadOnlyCollection<string> qids,
        string mediaType,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, IReadOnlyList<Guid>>> FindWorkIdsByExternalIdsAsync(
        string externalIdKey,
        IReadOnlyCollection<string> externalIds,
        CancellationToken ct = default);

    Task UpsertManifestAsync(
        SeriesManifestHydration hydration,
        IReadOnlyList<SeriesManifestItemRecord> items,
        CancellationToken ct = default);

    Task LinkOwnedWorksAsync(
        Guid collectionId,
        IReadOnlyList<SeriesManifestItemRecord> items,
        CancellationToken ct = default);

    Task<SeriesManifestViewDto?> GetViewByCollectionIdAsync(
        Guid collectionId,
        CancellationToken ct = default);
}
