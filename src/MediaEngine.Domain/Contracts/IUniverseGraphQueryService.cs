namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Local in-memory graph query service for the universe graph.
/// Loads fictional entities and relationships from SQLite into an EntityGraph.
/// Queries run locally with zero network calls.
/// </summary>
public interface IUniverseGraphQueryService
{
    /// <summary>
    /// Find all paths between two entities in a universe, up to a maximum hop count.
    /// Returns paths as ordered lists of entity QIDs.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyList<string>>> FindPathsAsync(
        string universeQid,
        string fromQid,
        string toQid,
        int maxHops = 4,
        CancellationToken ct = default);

    /// <summary>
    /// Get the family tree (ancestors and descendants) of a character up to a given depth.
    /// Returns entity QIDs grouped by generation relative to the center entity.
    /// </summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> GetFamilyTreeAsync(
        string universeQid,
        string characterQid,
        int generations = 3,
        CancellationToken ct = default);

    /// <summary>
    /// Invalidate the cached in-memory graph for a universe, forcing
    /// a reload from SQLite on next query.
    /// </summary>
    void InvalidateCache(string universeQid);
}
