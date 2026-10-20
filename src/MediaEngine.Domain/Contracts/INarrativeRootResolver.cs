using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Resolves the narrative root (fictional universe, franchise, or series) for a work
/// based on its Wikidata canonical values.
///
/// <para>
/// Priority order (broadest first):
/// <list type="number">
/// <item>P1434 (fictional_universe) — e.g. "Dune universe" (Q3041974)</item>
/// <item>P8345 (franchise) — e.g. "Dune franchise" (Q3041966)</item>
/// <item>P179 (series) — e.g. "Dune Chronicles" (Q1227040)</item>
/// <item>Collection DisplayName — standalone fallback (no QID)</item>
/// </list>
/// </para>
///
/// <para>
/// Called after Stage 1 hydration when canonical values for the three P-codes are available.
/// Stores the resolved narrative root and writes <c>&lt;universe-ref&gt;</c> to the Collection sidecar.
/// </para>
/// </summary>
public interface INarrativeRootResolver
{
    /// <summary>
    /// Resolve the narrative root for a work entity.
    /// </summary>
    /// <param name="entityId">The database ID of the work entity.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The resolved <see cref="NarrativeRoot"/>, or <c>null</c> if no universe, franchise,
    /// or series could be determined (standalone work with no Wikidata link).
    /// </returns>
    Task<NarrativeRoot?> ResolveAsync(Guid entityId, CancellationToken ct = default);
}
