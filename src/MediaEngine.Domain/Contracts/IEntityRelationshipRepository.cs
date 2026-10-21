using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// CRUD operations for <see cref="EntityRelationship"/> graph edges.
/// </summary>
public interface IEntityRelationshipRepository
{
    /// <summary>
    /// Insert a relationship fact. Idempotency is based on its full statement key,
    /// so equally-shaped triples with different scope or qualifiers survive.
    /// </summary>
    Task CreateAsync(EntityRelationship edge, CancellationToken ct = default);

    /// <summary>
    /// Return all outgoing edges from a given subject QID.
    /// </summary>
    Task<IReadOnlyList<EntityRelationship>> GetBySubjectAsync(
        string subjectQid, CancellationToken ct = default);

    /// <summary>
    /// Return all incoming edges pointing to a given object QID.
    /// </summary>
    Task<IReadOnlyList<EntityRelationship>> GetByObjectAsync(
        string objectQid, CancellationToken ct = default);

    /// <summary>
    /// Return all incoming edges pointing to any supplied object QID in bounded batches.
    /// </summary>
    Task<IReadOnlyList<EntityRelationship>> GetByObjectsAsync(
        IEnumerable<string> objectQids, CancellationToken ct = default);

    /// <summary>
    /// Return all edges (both directions) involving a given QID.
    /// </summary>
    Task<IReadOnlyList<EntityRelationship>> GetByEntityAsync(
        string qid, CancellationToken ct = default);

    /// <summary>
    /// Return all edges where both subject and object belong to the given universe.
    /// Used when building the full universe graph.
    /// </summary>
    Task<IReadOnlyList<EntityRelationship>> GetByUniverseAsync(
        IReadOnlyCollection<string> entityQids, CancellationToken ct = default);

    /// <summary>
    /// Return the edges of the given relationship types where both subject and object belong to the supplied QIDs.
    /// Only those types are read from the data store.
    /// </summary>
    Task<IReadOnlyList<EntityRelationship>> GetByUniverseAsync(
        IReadOnlyCollection<string> entityQids,
        IReadOnlyCollection<string> relationshipTypes,
        CancellationToken ct = default);

    /// <summary>Return facts carrying the supplied normalized qualifier value.</summary>
    Task<IReadOnlyList<EntityRelationship>> GetByQualifierAsync(
        string qualifierType,
        string value,
        CancellationToken ct = default);

    /// <summary>Load normalized qualifiers for one fact.</summary>
    Task<IReadOnlyList<EntityRelationshipQualifier>> GetQualifiersAsync(
        Guid relationshipId,
        CancellationToken ct = default);

    /// <summary>
    /// Delete the facts with the given statement keys, with their qualifiers. Returns how many facts were removed.
    /// </summary>
    Task<int> DeleteByStatementKeysAsync(IReadOnlyCollection<string> statementKeys, CancellationToken ct = default);

    /// <summary>Return total edge count (for stats).</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}
