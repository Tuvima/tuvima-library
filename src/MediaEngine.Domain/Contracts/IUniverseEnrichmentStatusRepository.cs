namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Reads how far the Engine's universe enrichment (the stage that learns the people, places and things of a work's
/// fictional universe) has got for a set of works. It reads the same per-work evidence the item detail derives its
/// universe status from: the <c>stage3_enriched_at</c> mark and the work's identity jobs.
/// </summary>
public interface IUniverseEnrichmentStatusRepository
{
    /// <summary>
    /// Counts, among <paramref name="workIds"/>, the works that were enriched and the not-yet-enriched works that an
    /// identity job is currently carrying through the enrichment states.
    /// </summary>
    Task<UniverseEnrichmentProgress> GetProgressAsync(
        IReadOnlyCollection<Guid> workIds,
        CancellationToken ct = default);
}

/// <param name="WorkCount">Works found among the requested ids.</param>
/// <param name="EnrichedWorkCount">Works marked as enriched.</param>
/// <param name="ActiveWorkCount">Works not yet enriched that an identity job is currently working on.</param>
public sealed record UniverseEnrichmentProgress(int WorkCount, int EnrichedWorkCount, int ActiveWorkCount);
