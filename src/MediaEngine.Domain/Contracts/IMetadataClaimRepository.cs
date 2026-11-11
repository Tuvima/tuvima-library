using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Persistence contract for <see cref="MetadataClaim"/> records.
///
/// The <c>metadata_claims</c> table is append-only: rows are NEVER deleted.
/// The full claim history is retained to allow re-scoring when provider weights
/// change (Spec: Phase 4 – Invariants § Claim History).
///
/// Implementations live in <c>MediaEngine.Storage</c>.
/// </summary>
public interface IMetadataClaimRepository
{
    /// <summary>
    /// Inserts a batch of claims into the <c>metadata_claims</c> table.
    /// Each claim is inserted unconditionally; duplicate insertion of the same
    /// logical claim is prevented by the caller, not this method.
    /// </summary>
    /// <param name="claims">The claims to insert. May be empty; no-op if so.</param>
    /// <param name="ct">Cancellation token.</param>
    Task InsertBatchAsync(
        IReadOnlyList<MetadataClaim> claims,
        CancellationToken ct = default);

    /// <summary>
    /// Takes every current claim of the entity for the supplied fields out of scoring, the person's locked ones included. The rows stay as
    /// history; nothing is deleted. Used when a person removes a value they had set (the Wikidata link), so no later scoring brings it back.
    /// </summary>
    Task RetireCurrentClaimsAsync(
        Guid entityId,
        IReadOnlyCollection<string> fieldKeys,
        CancellationToken ct = default) =>
        throw new NotSupportedException("This claim store cannot retire claims.");

    /// <summary>
    /// Atomically replaces a provider's current observations for the supplied
    /// fields. Old rows remain available as history but are excluded from scoring.
    /// </summary>
    async Task ReplaceCurrentProviderClaimsAsync(
        Guid entityId,
        Guid providerId,
        IReadOnlyCollection<string> fieldKeys,
        IReadOnlyList<MetadataClaim> claims,
        CancellationToken ct = default)
    {
        await InsertBatchAsync(claims, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns current claims for a given entity, ordered by <see cref="MetadataClaim.ClaimedAt"/>
    /// ascending (oldest first).
    /// </summary>
    /// <param name="entityId">The entity whose claims to retrieve.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<MetadataClaim>> GetByEntityAsync(
        Guid entityId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns claims for several entities. Storage implementations should execute
    /// this as bounded bulk reads; the default keeps lightweight test doubles valid.
    /// </summary>
    async Task<IReadOnlyDictionary<Guid, IReadOnlyList<MetadataClaim>>> GetByEntitiesAsync(
        IReadOnlyList<Guid> entityIds,
        CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, IReadOnlyList<MetadataClaim>>();
        foreach (var entityId in entityIds.Where(id => id != Guid.Empty).Distinct())
        {
            ct.ThrowIfCancellationRequested();
            result[entityId] = await GetByEntityAsync(entityId, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Deletes all claims for the given <paramref name="entityId"/>.
    /// Used during orphan cleanup when the asset record is being removed.
    /// </summary>
    Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default);
}
