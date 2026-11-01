using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Contracts;

/// <summary>
/// Storage for imported lists (Collections and lists spec D5): the source of a list collection, every entry of the list with
/// what it matched, and the collection's items with who added them. The refresh job and the import both write through
/// <see cref="SaveAsync"/>, so a source, its entries and its items always change together.
/// </summary>
public interface ICollectionListRepository
{
    Task<CollectionListSource?> GetSourceAsync(Guid collectionId, CancellationToken ct = default);

    /// <summary>Every list source, for a read that decorates many collections at once (the Collections page).</summary>
    Task<IReadOnlyList<CollectionListSource>> GetAllSourcesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<CollectionListEntry>> GetEntriesAsync(Guid collectionId, CancellationToken ct = default);

    Task<CollectionListEntry?> GetEntryAsync(Guid collectionId, Guid entryId, CancellationToken ct = default);

    /// <summary>Every item of the collection with who added it, in the collection's order.</summary>
    Task<IReadOnlyList<ListCollectionItem>> GetItemsAsync(Guid collectionId, CancellationToken ct = default);

    /// <summary>
    /// In one write: stores the source row, replaces the collection's entries with <paramref name="entries"/>, and makes the
    /// collection's items equal <paramref name="items"/> (rows not listed are removed, new ones added, the rest get the new
    /// order and adder). The caller decides what the items should be; hand-added titles are passed through unchanged.
    /// </summary>
    Task SaveAsync(
        CollectionListSource source,
        IReadOnlyList<CollectionListEntry> entries,
        IReadOnlyList<ListCollectionItemPlacement> items,
        CancellationToken ct = default);

    /// <summary>Changes only the choices on the source (order, refresh, sync mode) and when it is next due.</summary>
    Task UpdateSettingsAsync(Guid collectionId, string orderBy, string refresh, string syncMode, DateTimeOffset? nextRefreshAt, CancellationToken ct = default);

    /// <summary>The collections whose refresh is due: a refresh choice other than never and a due time that has passed.</summary>
    Task<IReadOnlyList<Guid>> GetDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default);

    /// <summary>True when the collection is a list collection.</summary>
    Task<bool> IsListCollectionAsync(Guid collectionId, CancellationToken ct = default);
}
