using MediaEngine.Domain.Contracts;

namespace MediaEngine.Domain.Services;

/// <summary>
/// Answers <see cref="UniverseIdentityRule"/>'s Library lookups from the Series shelves and the Universe records
/// (<c>narrative_roots</c>). One instance serves one decision: the shelf being decided is left out of the shelf lookup so
/// it can never confirm its own earlier answer.
/// </summary>
public sealed class LibraryUniverseLookup : IUniverseCanonicalLookup
{
    private readonly ICollectionRepository _collections;
    private readonly INarrativeRootRepository _narrativeRoots;
    private readonly Guid? _excludedShelfId;

    public LibraryUniverseLookup(
        ICollectionRepository collections,
        INarrativeRootRepository narrativeRoots,
        Guid? excludedShelfId = null)
    {
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentNullException.ThrowIfNull(narrativeRoots);
        _collections = collections;
        _narrativeRoots = narrativeRoots;
        _excludedShelfId = excludedShelfId;
    }

    /// <inheritdoc />
    public async Task<string?> FindShelfUniverseQidAsync(string seriesQid, CancellationToken ct)
    {
        var shelves = await _collections.FindSeriesShelvesByQidAsync(seriesQid, ct).ConfigureAwait(false);
        return shelves
            .Where(shelf => shelf.Id != _excludedShelfId)
            .Select(shelf => UniverseIdentityRule.NormaliseQid(shelf.UniverseQid))
            .FirstOrDefault(universeQid => universeQid is not null);
    }

    /// <inheritdoc />
    public async Task<UniverseRecord?> FindRecordAsync(string qid, CancellationToken ct)
    {
        var root = await _narrativeRoots.FindByQidAsync(qid, ct).ConfigureAwait(false);
        return root is null ? null : new UniverseRecord(root.Qid, root.Label, root.Level, root.ParentQid);
    }
}
