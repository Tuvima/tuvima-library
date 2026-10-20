using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Contracts;

/// <summary>One Series shelf as the Matching Series block shows it.</summary>
/// <param name="Scope">A <c>SeriesMembershipScopeNames</c> value; main sequence when no manifest lists the work.</param>
/// <param name="PositionLabel">The work's position in that Series (for example <c>3</c> or <c>2.5</c>), when the manifest gives one.</param>
public sealed record SeriesPlacementShelf(
    Guid CollectionId,
    string Label,
    string? SeriesQid,
    string MediaType,
    string Scope,
    string? PositionLabel);

/// <summary>What the Matching Series block needs to show for one work.</summary>
/// <param name="Current">The Series shelf the work is on now, or null when it is on none.</param>
/// <param name="IsPersonChoice">True when a person locked the placement (including "not in a Series").</param>
/// <param name="Listed">Series whose stored manifests list the work.</param>
/// <param name="Candidates">Other Series shelves of the work's media type matching the search, bounded.</param>
public sealed record SeriesPlacementView(
    Guid WorkId,
    string MediaType,
    SeriesPlacementShelf? Current,
    bool IsPersonChoice,
    IReadOnlyList<SeriesPlacementShelf> Listed,
    IReadOnlyList<SeriesPlacementShelf> Candidates);

/// <summary>
/// Every entity a Needs Review item about one work may hang on: the work and each of its files. <see cref="StableAssetId"/> is
/// the one file new items hang on (the work's earliest-stored Normal original), so repeated runs, new editions and orphaned
/// files never create a second item.
/// </summary>
public sealed record SeriesReviewEntities(Guid? StableAssetId, IReadOnlyList<Guid> EntityIds);

public enum SeriesPlacementChoiceResult
{
    Applied,
    WorkNotFound,
    ShelfNotFound,

    /// <summary>The shelf is not a Series shelf, or its media type is not the work's.</summary>
    NotASeriesShelfOfThisMediaType,
}

/// <summary>
/// Reads and writes a person's locked Series choice for a work (<c>works.series_placement_locked</c>). A lock means
/// <c>works.collection_id</c> is the person's choice (null = "not in a Series") and nothing automatic may change it.
/// </summary>
public interface ISeriesPlacementRepository
{
    /// <summary>The work an editor entity belongs to: the id itself when it is a work, else the work of that edition or file; null when unknown.</summary>
    Task<Guid?> ResolveWorkIdAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>The work's lock, or null when the Library decides.</summary>
    Task<SeriesPlacementLock?> GetLockAsync(Guid workId, CancellationToken ct = default);

    /// <summary>The work's media type name, or null when the work does not exist.</summary>
    Task<string?> GetWorkMediaTypeAsync(Guid workId, CancellationToken ct = default);

    /// <summary>
    /// Saves the person's choice: puts the work on the Series shelf (or on none when <paramref name="collectionId"/> is
    /// null), keeps <c>collection_items</c> consistent and sets the lock.
    /// </summary>
    Task<SeriesPlacementChoiceResult> SetChoiceAsync(Guid workId, Guid? collectionId, CancellationToken ct = default);

    /// <summary>
    /// Releases the person's choice: clears the lock and takes the work off its Series shelf, so the caller's re-run of
    /// placement is what decides where it goes (the Library decides again from scratch).
    /// </summary>
    Task ReleaseChoiceAsync(Guid workId, CancellationToken ct = default);

    /// <summary>The entities Needs Review items about this work may hang on, and the stable one for new items.</summary>
    Task<SeriesReviewEntities> GetReviewEntitiesAsync(Guid workId, CancellationToken ct = default);

    /// <summary>The Series block data for one work; null when the work does not exist.</summary>
    /// <param name="query">Optional name filter for <see cref="SeriesPlacementView.Candidates"/>.</param>
    /// <param name="candidateLimit">Maximum candidates returned.</param>
    Task<SeriesPlacementView?> GetViewAsync(Guid workId, string? query, int candidateLimit, CancellationToken ct = default);
}
