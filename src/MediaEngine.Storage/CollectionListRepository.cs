using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Imported lists (Collections and lists spec D5): the source of a list collection, its entries and its items with who added
/// them. A list collection's titles live in <c>collection_items</c> like any hand-picked collection's; <c>added_by</c> is what
/// lets a refresh change only the titles the list put there.
/// </summary>
public sealed class CollectionListRepository : ICollectionListRepository
{
    private const string SelectSource =
        """
        SELECT collection_id AS CollectionId, source_kind AS SourceKind, source_label AS SourceLabel, source_name AS SourceName,
               source_address AS SourceAddress, shelf AS Shelf, order_by AS OrderBy, refresh AS Refresh, sync_mode AS SyncMode,
               last_refreshed_at AS LastRefreshedAt, next_refresh_at AS NextRefreshAt, entry_count AS EntryCount,
               matched_count AS MatchedCount, unmatched_count AS UnmatchedCount, connection_profile_id AS ConnectionProfileId,
               created_at AS CreatedAt, updated_at AS UpdatedAt
        FROM collection_list_sources
        """;

    private const string SelectEntry =
        """
        SELECT id AS Id, collection_id AS CollectionId, position AS Position, title AS Title, year AS Year, creator AS Creator,
               shelf AS Shelf, ids_json AS IdsJson, media_types AS MediaTypes, entry_key AS EntryKey, status AS Status, matched_work_id AS MatchedWorkId,
               match_basis AS MatchBasis, on_list AS OnList
        FROM collection_list_entries
        """;

    private readonly IDatabaseConnection _db;

    public CollectionListRepository(IDatabaseConnection db) => _db = db;

    public Task<CollectionListSource?> GetSourceAsync(Guid collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var source = conn.QuerySingleOrDefault<CollectionListSource>(
            SelectSource + " WHERE collection_id = @collectionId", new { collectionId });
        return Task.FromResult(source);
    }

    public Task<IReadOnlyList<CollectionListSource>> GetAllSourcesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        return Task.FromResult<IReadOnlyList<CollectionListSource>>(conn.Query<CollectionListSource>(SelectSource).ToList());
    }

    public Task<IReadOnlyList<CollectionListEntry>> GetEntriesAsync(Guid collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var entries = conn.Query<CollectionListEntry>(
            SelectEntry + " WHERE collection_id = @collectionId ORDER BY position, title COLLATE NOCASE", new { collectionId });
        return Task.FromResult<IReadOnlyList<CollectionListEntry>>(entries.ToList());
    }

    public Task<CollectionListEntry?> GetEntryAsync(Guid collectionId, Guid entryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var entry = conn.QuerySingleOrDefault<CollectionListEntry>(
            SelectEntry + " WHERE collection_id = @collectionId AND id = @entryId", new { collectionId, entryId });
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<ListCollectionItem>> GetItemsAsync(Guid collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var items = conn.Query<ListCollectionItem>(
            """
            SELECT id AS Id, work_id AS WorkId, sort_order AS SortOrder, added_by AS AddedBy
            FROM collection_items
            WHERE collection_id = @collectionId
            ORDER BY sort_order, added_at
            """,
            new { collectionId });
        return Task.FromResult<IReadOnlyList<ListCollectionItem>>(items.ToList());
    }

    public Task SaveAsync(
        CollectionListSource source,
        IReadOnlyList<CollectionListEntry> entries,
        IReadOnlyList<ListCollectionItemPlacement> items,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(items);
        ct.ThrowIfCancellationRequested();

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute(
                """
                INSERT INTO collection_list_sources
                    (collection_id, source_kind, source_label, source_name, source_address, shelf, order_by, refresh, sync_mode,
                     last_refreshed_at, next_refresh_at, entry_count, matched_count, unmatched_count, connection_profile_id, created_at, updated_at)
                VALUES
                    (@CollectionId, @SourceKind, @SourceLabel, @SourceName, @SourceAddress, @Shelf, @OrderBy, @Refresh, @SyncMode,
                     @LastRefreshedAt, @NextRefreshAt, @EntryCount, @MatchedCount, @UnmatchedCount, @ConnectionProfileId, @CreatedAt, @UpdatedAt)
                ON CONFLICT(collection_id) DO UPDATE SET
                    source_kind = excluded.source_kind, source_label = excluded.source_label, source_name = excluded.source_name,
                    source_address = excluded.source_address, shelf = excluded.shelf, order_by = excluded.order_by,
                    refresh = excluded.refresh, sync_mode = excluded.sync_mode, last_refreshed_at = excluded.last_refreshed_at,
                    next_refresh_at = excluded.next_refresh_at, entry_count = excluded.entry_count,
                    matched_count = excluded.matched_count, unmatched_count = excluded.unmatched_count,
                    connection_profile_id = excluded.connection_profile_id, updated_at = excluded.updated_at
                """,
                source, tx);

            conn.Execute("DELETE FROM collection_list_entries WHERE collection_id = @CollectionId", new { source.CollectionId }, tx);
            foreach (var entry in entries)
            {
                conn.Execute(
                    """
                    INSERT INTO collection_list_entries
                        (id, collection_id, position, title, year, creator, shelf, ids_json, media_types, entry_key, status, matched_work_id,
                         match_basis, on_list)
                    VALUES
                        (@Id, @CollectionId, @Position, @Title, @Year, @Creator, @Shelf, @IdsJson, @MediaTypes, @EntryKey, @Status,
                         @MatchedWorkId, @MatchBasis, @OnList)
                    """,
                    entry, tx);
            }

            SyncItems(conn, tx, source.CollectionId, items);
        }, ct);
    }

    public Task UpdateSettingsAsync(
        Guid collectionId, string orderBy, string refresh, string syncMode, DateTimeOffset? nextRefreshAt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute(
                """
                UPDATE collection_list_sources
                SET order_by = @orderBy, refresh = @refresh, sync_mode = @syncMode, next_refresh_at = @nextRefreshAt,
                    updated_at = @now
                WHERE collection_id = @collectionId
                """,
                new { collectionId, orderBy, refresh, syncMode, nextRefreshAt, now = DateTimeOffset.UtcNow },
                tx);
        }, ct);
    }

    public Task<IReadOnlyList<Guid>> GetDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        // Stored as ISO-8601 text with a "Z"/offset, so the text order is the time order for UTC values.
        var due = conn.Query<Guid>(
            """
            SELECT collection_id
            FROM collection_list_sources
            WHERE refresh <> 'never' AND (next_refresh_at IS NULL OR next_refresh_at <= @now)
            ORDER BY COALESCE(next_refresh_at, '') LIMIT @limit
            """,
            new { now = now.ToUniversalTime().ToString("o"), limit });
        return Task.FromResult<IReadOnlyList<Guid>>(due.ToList());
    }

    public Task<bool> IsListCollectionAsync(Guid collectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var exists = conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM collection_list_sources WHERE collection_id = @collectionId", new { collectionId });
        return Task.FromResult(exists > 0);
    }

    // Makes the collection's rows equal the wanted placements. A row that stays keeps its id and added_at.
    private static void SyncItems(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        Guid collectionId,
        IReadOnlyList<ListCollectionItemPlacement> wanted)
    {
        var existing = conn.Query<ListCollectionItem>(
            "SELECT id AS Id, work_id AS WorkId, sort_order AS SortOrder, added_by AS AddedBy FROM collection_items WHERE collection_id = @collectionId",
            new { collectionId }, tx).ToList();
        var wantedByWork = new Dictionary<Guid, ListCollectionItemPlacement>();
        foreach (var placement in wanted)
        {
            wantedByWork.TryAdd(placement.WorkId, placement);
        }

        var seen = new HashSet<Guid>();
        foreach (var row in existing)
        {
            if (!wantedByWork.TryGetValue(row.WorkId, out var placement) || !seen.Add(row.WorkId))
            {
                conn.Execute("DELETE FROM collection_items WHERE id = @Id", new { row.Id }, tx);
                continue;
            }

            if (row.SortOrder != placement.SortOrder || !string.Equals(row.AddedBy, placement.AddedBy, StringComparison.Ordinal))
            {
                conn.Execute(
                    "UPDATE collection_items SET sort_order = @SortOrder, added_by = @AddedBy WHERE id = @Id",
                    new { placement.SortOrder, placement.AddedBy, row.Id }, tx);
            }
        }

        foreach (var placement in wantedByWork.Values.Where(item => !seen.Contains(item.WorkId)))
        {
            conn.Execute(
                $"""
                INSERT INTO collection_items (id, collection_id, work_id, sort_order, added_by, added_at)
                SELECT @Id, @collectionId, @WorkId, @SortOrder, @AddedBy, @addedAt
                WHERE {VaultSql.NotVaultEntity("@WorkId")}
                """,
                new
                {
                    Id = Guid.NewGuid(),
                    collectionId,
                    placement.WorkId,
                    placement.SortOrder,
                    placement.AddedBy,
                    addedAt = DateTimeOffset.UtcNow.ToString("O"),
                },
                tx);
        }
    }
}
