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

    /// <summary>
    /// Applies <see cref="SeriesPlacementRule"/> to every owned work these rows name, and to the works listed in the
    /// parent collections these rows list, moving each to the shelf the rule chooses (idempotent). Works that two Series
    /// claim equally as main sequence are taken off both and returned, so the caller can raise one Needs Review item each.
    /// </summary>
    Task<IReadOnlyList<AmbiguousSeriesPlacement>> LinkOwnedWorksAsync(
        Guid collectionId,
        IReadOnlyList<SeriesManifestItemRecord> items,
        CancellationToken ct = default);

    /// <summary>
    /// Like <see cref="LinkOwnedWorksAsync"/>, and also reports every work it decided without a tie (including works reached
    /// through a parent collection), so the caller can close their "which Series?" questions in one batch.
    /// </summary>
    Task<SeriesLinkOutcome> LinkOwnedWorksWithOutcomeAsync(
        Guid collectionId,
        IReadOnlyList<SeriesManifestItemRecord> items,
        CancellationToken ct = default);

    /// <summary>
    /// Applies <see cref="SeriesPlacementRule"/> to one work from the manifests that name it (no retail identity), for a work
    /// that has no file to run collection assignment through. Returns the placement when two Series claim the work equally.
    /// </summary>
    Task<AmbiguousSeriesPlacement?> PlaceWorkFromManifestsAsync(Guid workId, CancellationToken ct = default);

    /// <summary>
    /// The owned-work rows of several shelves' manifests in one read (no collection items, no missing rows), for readers
    /// that order the members of many Series at once.
    /// </summary>
    Task<IReadOnlyList<SeriesManifestItemRecord>> GetLinkedItemsByCollectionIdsAsync(
        IReadOnlyCollection<Guid> collectionIds,
        CancellationToken ct = default);

    Task<SeriesManifestViewDto?> GetViewByCollectionIdAsync(
        Guid collectionId,
        CancellationToken ct = default);
}
