namespace MediaEngine.Domain.Contracts;

/// <summary>
/// CRUD contract for the <c>bridge_ids</c> table.
///
/// Bridge IDs are cross-platform identifiers (ISBN, Apple Books ID, TMDB ID, etc.)
/// that link a library entity to external catalogues and Wikidata. Stored separately
/// from canonical_values for clean querying and self-documenting schema.
/// </summary>
public interface IBridgeIdRepository
{
    /// <summary>Returns all bridge IDs for the given entity.</summary>
    Task<IReadOnlyList<BridgeIdEntry>> GetByEntityAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>
    /// Returns all bridge IDs for a batch of entities in a single query.
    /// The result is grouped by entity ID for O(1) per-job lookup.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<BridgeIdEntry>>> GetByEntitiesAsync(
        IReadOnlyList<Guid> entityIds, CancellationToken ct = default);

    /// <summary>Finds a single bridge ID by entity + type (e.g. "isbn").</summary>
    Task<BridgeIdEntry?> FindAsync(Guid entityId, string idType, CancellationToken ct = default);

    /// <summary>Finds entities that have the specified bridge ID type and value.</summary>
    Task<IReadOnlyList<BridgeIdEntry>> FindByValueAsync(string idType, string idValue, CancellationToken ct = default);

    /// <summary>Inserts or updates a bridge ID (upsert on entity_id + id_type).</summary>
    Task UpsertAsync(BridgeIdEntry entry, CancellationToken ct = default);

    /// <summary>Inserts or updates multiple bridge IDs in a single transaction.</summary>
    Task UpsertBatchAsync(IReadOnlyList<BridgeIdEntry> entries, CancellationToken ct = default);

    /// <summary>Deletes all bridge IDs for the given entity.</summary>
    Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>Removes the entity's bridge ids of these types and nothing else (a removed match drops its provider ids, not the Wikidata id).</summary>
    Task DeleteByTypesAsync(Guid entityId, IReadOnlyCollection<string> idTypes, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// A single cross-platform identifier linking a library entity to an external catalogue.
/// </summary>
public sealed class BridgeIdEntry
{
    /// <summary>Row primary key.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The library entity this bridge ID belongs to (media asset, work, edition).</summary>
    public Guid EntityId { get; set; }

    /// <summary>Identifier type key (e.g. "isbn", "asin", "apple_books_id", "tmdb_id").</summary>
    public string IdType { get; set; } = "";

    /// <summary>The identifier value (e.g. "978-0-14-118776-1", "B003JTHWKU").</summary>
    public string IdValue { get; set; } = "";

    /// <summary>Wikidata property code for this ID type (e.g. "P212", "P5848"). Null if unknown.</summary>
    public string? WikidataProperty { get; set; }

    /// <summary>Provider that deposited this bridge ID. Null if from file metadata.</summary>
    public string? ProviderId { get; set; }

    /// <summary>When this entry was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
