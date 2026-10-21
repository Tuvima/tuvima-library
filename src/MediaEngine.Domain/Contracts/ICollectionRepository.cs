using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Contracts;

public sealed record CollectionBackfillCandidate(Guid WorkId, Guid MediaAssetId);

/// <summary>A collection deleted by <see cref="ICollectionRepository.DeleteEmptyCuratedCollectionsAsync"/>.</summary>
public sealed record RemovedCollection(Guid Id, string Name);

/// <summary>
/// Persistence contract for loading <see cref="Collection"/> aggregates with their
/// child Works, Relationships, and associated CanonicalValues.
/// </summary>
public interface ICollectionRepository
{
    /// <summary>
    /// Returns all collections, each populated with their Works, Relationships,
    /// and each Work's CanonicalValues. Editions and MediaAssets are NOT loaded.
    /// </summary>
    Task<IReadOnlyList<Collection>> GetAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Finds a collection by its display name (case-insensitive).
    /// Returns null when no collection with that name exists.
    /// Used by Great Inhale to avoid creating duplicate collections.
    /// </summary>
    Task<Collection?> FindByDisplayNameAsync(string displayName, CancellationToken ct = default);

    /// <summary>
    /// Finds a collection that has a <see cref="CollectionRelationship"/> matching the given
    /// relationship type and Wikidata QID. Returns the Collection with relationships loaded,
    /// or null if no match exists.
    /// </summary>
    Task<Collection?> FindByRelationshipQidAsync(string relType, string qid, CancellationToken ct = default);

    /// <summary>
    /// Inserts a new Collection (identified by <see cref="Collection.Id"/>) if it does not
    /// yet exist, or updates <see cref="Collection.DisplayName"/> on an existing one.
    /// Returns the collection's <see cref="Collection.Id"/>.
    /// </summary>
    Task<Guid> UpsertAsync(Collection collection, CancellationToken ct = default);

    /// <summary>Creates one managed collection and its initial materialized members atomically.</summary>
    Task<Guid> CreateManagedCollectionAsync(
        Collection collection,
        IReadOnlyList<CollectionItem> items,
        CancellationToken ct = default) =>
        throw new NotSupportedException("This collection repository does not support atomic managed-collection creation.");

    /// <summary>
    /// Deletes every manually curated collection (a <c>Custom</c> collection with stored members) that
    /// has no items left, writes one system activity entry for each, and returns what was removed.
    /// Call it after any code path that deletes works: a curated collection is never allowed to be
    /// empty. Playlists, playlist folders, rule-driven and system collections are never touched.
    /// </summary>
    Task<IReadOnlyList<RemovedCollection>> DeleteEmptyCuratedCollectionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Saves a managed collection's changes and appends <paramref name="newItems"/> (works already in the
    /// collection are skipped) in one transaction. When <paramref name="requireMember"/> is set and the collection
    /// would hold no library item and no personal-media source afterwards, nothing is written and the result is
    /// <c>false</c>. Used for a curated collection, for example when a rule-driven one is switched to manual.
    /// </summary>
    Task<bool> UpdateManagedCollectionAsync(
        Collection collection,
        IReadOnlyList<CollectionItem> newItems,
        bool requireMember,
        CancellationToken ct = default);

    /// <summary>Replaces the selected-profile audience for one managed collection.</summary>
    Task ReplaceAudienceProfileIdsAsync(
        Guid collectionId,
        IReadOnlyCollection<Guid> profileIds,
        CancellationToken ct = default) =>
        throw new NotSupportedException("This collection repository does not support selected-profile audiences.");

    /// <summary>
    /// Bulk-inserts collection relationship rows. Idempotent — duplicates are ignored.
    /// </summary>
    Task InsertRelationshipsAsync(IReadOnlyList<CollectionRelationship> relationships, CancellationToken ct = default);

    /// <summary>
    /// Resolves the Work ID for a given MediaAsset ID by joining through the
    /// <c>editions</c> table. Returns null when the asset is not found.
    /// </summary>
    Task<Guid?> GetWorkIdByMediaAssetAsync(Guid mediaAssetId, CancellationToken ct = default);

    /// <summary>
    /// Resolves the Work lineage for a given MediaAsset ID, from the leaf Work
    /// up through any parent and grandparent Work rows. Returns an empty list
    /// when the asset is not found.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetWorkLineageIdsByMediaAssetAsync(Guid mediaAssetId, CancellationToken ct = default);

    /// <summary>
    /// Returns the <see cref="Collection.DisplayName"/> of the Collection that owns the given
    /// Work, using a single SQL JOIN across works → collections. Returns null when the
    /// Work has no Collection assignment or when the Collection has no display name.
    /// </summary>
    Task<string?> FindCollectionNameByWorkIdAsync(Guid workId, CancellationToken ct = default);

    /// <summary>
    /// Updates a Work's <c>collection_id</c> to assign it to a Collection.
    /// </summary>
    Task AssignWorkToCollectionAsync(Guid workId, Guid collectionId, CancellationToken ct = default);

    /// <summary>
    /// Merges <paramref name="mergeCollectionId"/> into <paramref name="keepCollectionId"/>:
    /// re-assigns all Works, moves all relationships, then deletes the merged Collection.
    /// </summary>
    Task MergeCollectionsAsync(Guid keepCollectionId, Guid mergeCollectionId, CancellationToken ct = default);

    /// <summary>
    /// Sets <c>universe_mismatch = 1</c> and <c>universe_mismatch_at</c> on a Work
    /// identified by <paramref name="workId"/>. Used when the user explicitly skips
    /// Universe (Wikidata) matching for a Work via the Needs Review tab.
    /// </summary>
    Task SetUniverseMismatchAsync(Guid workId, CancellationToken ct = default);

    /// <summary>
    /// Updates a Work's <c>wikidata_status</c> and stamps <c>wikidata_checked_at</c>.
    /// Used after Wikidata QID resolution to mark a Work as "confirmed" or leave it
    /// as "pending" with a timestamp.
    /// </summary>
    Task UpdateWorkWikidataStatusAsync(Guid workId, string status, CancellationToken ct = default);

    /// <summary>
    /// Updates Wikidata match state, source, lock, current QID, and rejected QIDs for an existing Work.
    /// Null values preserve the current column value.
    /// </summary>
    Task UpdateWorkWikidataMatchStateAsync(
        Guid workId,
        string status,
        string? source = null,
        bool? locked = null,
        string? wikidataQid = null,
        string? rejectedQidsJson = null,
        CancellationToken ct = default);

    /// <summary>
    /// Removes orphaned hierarchy records that no longer have children:
    /// <list type="number">
    ///   <item>Editions with zero MediaAssets.</item>
    ///   <item>Works with zero Editions (after the Edition pass).</item>
    ///   <item>Collections with zero Works (after the Work pass), including their
    ///     <c>collection_relationships</c> rows.</item>
    /// </list>
    /// Returns the total number of rows deleted across all three passes.
    /// Call this after bulk-deleting MediaAssets to keep the hierarchy clean.
    /// </summary>
    Task<int> PruneOrphanedHierarchyAsync(CancellationToken ct = default);

    /// <summary>Returns all child Collections of the given parent.</summary>
    Task<IReadOnlyList<Collection>> GetChildCollectionsAsync(Guid parentCollectionId, CancellationToken ct = default);

    /// <summary>Sets or clears the parent Collection for a given Collection.</summary>
    Task SetParentCollectionAsync(Guid collectionId, Guid? parentCollectionId, CancellationToken ct = default);

    /// <summary>
    /// Sets the Universe record a Series shelf belongs to (<c>universe_qid</c>) and its Universe shelf
    /// (<c>parent_collection_id</c>) together. Either can be null.
    /// </summary>
    Task SetUniverseAsync(Guid collectionId, string? universeQid, Guid? parentCollectionId, CancellationToken ct = default);

    /// <summary>
    /// Finds the Universe shelf (a Universe collection) that presents this Universe record. Null when the Library has no
    /// shelf for it.
    /// </summary>
    Task<Collection?> FindUniverseShelfByUniverseQidAsync(string universeQid, CancellationToken ct = default);

    /// <summary>Lists the Series shelves whose <c>universe_qid</c> is this Universe record, oldest first.</summary>
    Task<IReadOnlyList<Collection>> ListSeriesShelvesByUniverseQidAsync(string universeQid, CancellationToken ct = default);

    /// <summary>
    /// Lists the Series shelves (any media type) whose own Wikidata QID is <paramref name="qid"/>, oldest first.
    /// </summary>
    Task<IReadOnlyList<Collection>> FindSeriesShelvesByQidAsync(string qid, CancellationToken ct = default);

    /// <summary>
    /// Lists the Series shelves that have a rollup relationship (series, franchise, fictional universe, based on) naming
    /// <paramref name="qid"/>, oldest first.
    /// </summary>
    Task<IReadOnlyList<Collection>> ListShelvesWithRollupRelationshipQidAsync(string qid, CancellationToken ct = default);

    /// <summary>
    /// Deletes a Universe shelf in one conditional write, only when it has no child shelves and no works and was never
    /// edited (no display override, artwork or description). Returns whether it was deleted.
    /// </summary>
    Task<bool> DeleteUnusedUniverseShelfAsync(Guid universeShelfId, CancellationToken ct = default);

    /// <summary>
    /// Finds the Series shelf that holds a Work or any Work below it (a TV show holds its episodes). Null when no Work
    /// in that tree sits on a Series shelf.
    /// </summary>
    Task<Collection?> FindSeriesShelfForWorkTreeAsync(Guid rootWorkId, CancellationToken ct = default);

    /// <summary>
    /// Returns the root Work id (the show, album or book itself) of every Work that sits on this shelf. These are the
    /// Works whose canonical values hold the narrative root.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetRootWorkIdsOnShelfAsync(Guid collectionId, CancellationToken ct = default);

    /// <summary>
    /// Returns the relationships for a single Collection without loading Works or CanonicalValues.
    /// Used by <c>ParentCollectionResolver</c> to inspect franchise signals on a specific Collection.
    /// </summary>
    Task<IReadOnlyList<CollectionRelationship>> GetRelationshipsAsync(Guid collectionId, CancellationToken ct = default);

    /// <summary>
    /// Returns the <see cref="Collection"/> row for a single Collection by its ID (no Works or relationships loaded).
    /// Returns null when the Collection does not exist.
    /// </summary>
    Task<Collection?> GetByIdAsync(Guid collectionId, CancellationToken ct = default);

    /// <summary>
    /// Returns the requested Collection rows without loading Works or relationships.
    /// Missing IDs are omitted and results retain the first-occurrence order of the requested IDs.
    /// </summary>
    Task<IReadOnlyList<Collection>> GetByIdsAsync(
        IEnumerable<Guid> collectionIds,
        CancellationToken ct = default);

    /// <summary>
    /// Finds a Collection by its Wikidata QID. Returns null if no Collection has this QID.
    /// </summary>
    Task<Collection?> FindByQidAsync(string qid, CancellationToken ct = default);

    /// <summary>
    /// Finds the Universe (a parentless Collection of type Universe) with this Wikidata QID. A Series or other
    /// Collection that shares the QID never stands in for it. The default only filters
    /// <see cref="FindByQidAsync"/>; the data store implementation looks the Universe up directly.
    /// </summary>
    async Task<Collection?> FindUniverseByQidAsync(string qid, CancellationToken ct = default)
    {
        var found = await FindByQidAsync(qid, ct).ConfigureAwait(false);
        return found is { CollectionType: CollectionType.Universe, ParentCollectionId: null } ? found : null;
    }

    /// <summary>
    /// Finds the Series shelf (a ContentGroup collection) with this Wikidata Series QID and this media type. A Series is
    /// identified by its QID plus its media type, so a Series of another media type, a Universe, or any other
    /// collection that shares the QID is never returned. Returns null when there is no such Series shelf.
    /// </summary>
    Task<Collection?> FindSeriesShelfAsync(string qid, string mediaType, CancellationToken ct = default);

    /// <summary>Finds an Edition by its Wikidata QID. Returns null when no match exists.</summary>
    Task<Edition?> FindEditionByQidAsync(string wikidataQid, CancellationToken ct = default);

    /// <summary>Creates a new Edition under the given Work and returns the persisted entity.</summary>
    Task<Edition> CreateEditionAsync(Guid workId, string? formatLabel, string? wikidataQid, CancellationToken ct = default);

    /// <summary>Updates the <c>match_level</c> on a Work record.</summary>
    Task UpdateMatchLevelAsync(Guid workId, string matchLevel, CancellationToken ct = default);

    // ── Managed Collection methods (Smart, System, Mix, Playlist) ──────────────

    /// <summary>Returns all collections of a given type (e.g. "Smart", "System", "Mix", "Playlist").</summary>
    Task<IReadOnlyList<Collection>> GetByTypeAsync(string collectionType, CancellationToken ct = default);

    /// <summary>Returns all non-Universe collections for managed collection surfaces.</summary>
    Task<IReadOnlyList<Collection>> GetManagedCollectionsAsync(CancellationToken ct = default);

    /// <summary>Returns count of collections grouped by CollectionType for stats bar.</summary>
    Task<Dictionary<string, int>> GetCountsByTypeAsync(CancellationToken ct = default);

    /// <summary>Returns curated items for a collection (System Lists, Playlists, Mixes).</summary>
    Task<IReadOnlyList<CollectionItem>> GetCollectionItemsAsync(Guid collectionId, int limit = 20, CancellationToken ct = default);

    /// <summary>Returns total curated item count for a collection.</summary>
    Task<int> GetCollectionItemCountAsync(Guid collectionId, CancellationToken ct = default);

    /// <summary>Returns curated item counts for multiple collections in one read.</summary>
    Task<Dictionary<Guid, int>> GetCollectionItemCountsAsync(
        IEnumerable<Guid> collectionIds,
        CancellationToken ct = default);

    /// <summary>Toggles the is_enabled flag on a collection.</summary>
    Task UpdateCollectionEnabledAsync(Guid collectionId, bool enabled, CancellationToken ct = default);

    /// <summary>Toggles the is_featured flag on a collection.</summary>
    Task UpdateCollectionFeaturedAsync(Guid collectionId, bool featured, CancellationToken ct = default);

    /// <summary>Sets or clears one custom collection artwork slot.</summary>
    Task UpdateCollectionArtworkAsync(Guid collectionId, string slot, string? localPath, string? mimeType, CancellationToken ct = default);

    /// <summary>Adds a work to a collection's curated items.</summary>
    Task AddCollectionItemAsync(CollectionItem item, CancellationToken ct = default);

    /// <summary>Removes a curated item from a collection.</summary>
    Task RemoveCollectionItemAsync(Guid itemId, CancellationToken ct = default);

    /// <summary>
    /// Removes a curated item in one conditional write, refusing (<c>false</c>) when it is the collection's only
    /// member. An attached personal-media source (gallery or smart rule) counts as a member.
    /// </summary>
    Task<bool> RemoveCollectionItemUnlessLastMemberAsync(Guid collectionId, Guid itemId, CancellationToken ct = default);

    /// <summary>Persists the display order for curated collection items.</summary>
    Task ReorderCollectionItemsAsync(Guid collectionId, IReadOnlyList<Guid> itemIds, CancellationToken ct = default);

    /// <summary>
    /// Returns Universe-type collections that have at least one child Work assigned.
    /// These are Content Groups: albums, TV series, book series, movie series.
    /// Each collection is returned with its Works and each Work's CanonicalValues loaded.
    /// </summary>
    Task<IReadOnlyList<Collection>> GetContentGroupsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a single collection by ID with all child Works and their CanonicalValues loaded.
    /// Returns null when no collection with this ID exists.
    /// </summary>
    Task<Collection?> GetCollectionWithWorksAsync(Guid collectionId, CancellationToken ct = default);

    /// <summary>
    /// Returns the requested collections with their child Works and each Work's CanonicalValues loaded.
    /// Missing IDs are omitted and results retain the first-occurrence order of the requested IDs.
    /// </summary>
    Task<IReadOnlyList<Collection>> GetCollectionsWithWorksAsync(
        IEnumerable<Guid> collectionIds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the collection_id for a given work ID.
    /// Returns null when the work has no collection assignment or does not exist.
    /// Used by the Universe Enrichment service for collection-level group refresh.
    /// </summary>
    Task<Guid?> GetCollectionIdByWorkIdAsync(Guid workId, CancellationToken ct = default);

    /// <summary>Finds a collection by its rule hash for deduplication.</summary>
    Task<Collection?> FindByRuleHashAsync(string ruleHash, CancellationToken ct = default);

    /// <summary>
    /// Takes a work off its shelf (sets <c>works.collection_id</c> to null). Used when placement is ambiguous and the
    /// work must not sit on a Series it may not belong to.
    /// </summary>
    Task ClearWorkCollectionAsync(Guid workId, CancellationToken ct = default);

    /// <summary>Counts owned, visible works that have media assets but no shelf collection assignment.</summary>
    Task<int> CountCollectionBackfillCandidatesAsync(CancellationToken ct = default);

    /// <summary>Returns representative media assets for owned, visible works that need shelf assignment repair.</summary>
    Task<IReadOnlyList<CollectionBackfillCandidate>> GetCollectionBackfillCandidatesAsync(
        int limit,
        Guid? afterWorkId = null,
        CancellationToken ct = default);

    /// <summary>Returns all enabled collections for placement resolution.</summary>
    Task<IReadOnlyList<Collection>> GetAllCollectionsForLocationAsync(CancellationToken ct = default);
}
