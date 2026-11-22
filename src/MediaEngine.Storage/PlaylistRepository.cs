using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IPlaylistRepository"/> (spec listen-playlists WP1). Every action works on the whole
/// playlist inside SQL, so a playlist of any size behaves the same; nothing here reads the items through a capped list.
/// </summary>
public sealed class PlaylistRepository : IPlaylistRepository
{
    private readonly IDatabaseConnection _db;

    public PlaylistRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public Task<bool> MoveItemAsync(Guid collectionId, Guid itemId, int position, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();

            // Where the item sits now (1-based, in the order readers show: sort_order, then insertion order) and how many items there are.
            var current = conn.QuerySingleOrDefault<MovePlace>(
                """
                WITH ranked AS (
                    SELECT id, ROW_NUMBER() OVER (ORDER BY sort_order, rowid) AS rn
                    FROM collection_items
                    WHERE collection_id = @CollectionId)
                SELECT (SELECT rn FROM ranked WHERE id = @ItemId) AS FromPlace,
                       (SELECT COUNT(*) FROM ranked) AS Total
                """,
                new { CollectionId = collectionId, ItemId = itemId },
                tx);

            if (current is null || current.FromPlace is not { } from)
            {
                return false;
            }

            var to = Math.Clamp(position, 1, current.Total);

            // One statement renumbers the whole playlist to 1..N with the item in its new place. The ranking is materialized first so
            // the renumbering cannot disturb the order it is read from.
            conn.Execute(
                """
                WITH ranked AS MATERIALIZED (
                    SELECT id, ROW_NUMBER() OVER (ORDER BY sort_order, rowid) AS rn
                    FROM collection_items
                    WHERE collection_id = @CollectionId),
                renumbered AS MATERIALIZED (
                    SELECT id,
                           CASE
                               WHEN id = @ItemId THEN @ToPlace
                               WHEN @FromPlace < @ToPlace AND rn > @FromPlace AND rn <= @ToPlace THEN rn - 1
                               WHEN @FromPlace > @ToPlace AND rn >= @ToPlace AND rn < @FromPlace THEN rn + 1
                               ELSE rn
                           END AS new_order
                    FROM ranked)
                UPDATE collection_items
                SET sort_order = renumbered.new_order
                FROM renumbered
                WHERE collection_items.id = renumbered.id
                """,
                new { CollectionId = collectionId, ItemId = itemId, FromPlace = from, ToPlace = to },
                tx);

            return true;
        }, ct);
    }

    /// <inheritdoc/>
    public Task<PlaylistAddResult> AddWorksAsync(Guid collectionId, IReadOnlyList<Guid> workIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            var nextSortOrder = conn.ExecuteScalar<int>(
                "SELECT COALESCE(MAX(sort_order), 0) + 1 FROM collection_items WHERE collection_id = @CollectionId",
                new { CollectionId = collectionId },
                tx);
            var added = 0;
            var alreadyThere = 0;

            foreach (var workId in workIds.Where(id => id != Guid.Empty).Distinct())
            {
                innerCt.ThrowIfCancellationRequested();

                var isThere = conn.ExecuteScalar<int>(
                    "SELECT EXISTS (SELECT 1 FROM collection_items WHERE collection_id = @CollectionId AND work_id = @WorkId)",
                    new { CollectionId = collectionId, WorkId = workId },
                    tx) == 1;
                if (isThere)
                {
                    alreadyThere++;
                    continue;
                }

                // A Vault work is never a member of a shared collection (Videos spec D27): the insert quietly adds nothing for one.
                var inserted = conn.Execute(
                    $"""
                    INSERT INTO collection_items (id, collection_id, work_id, sort_order, progress_state, added_at)
                    SELECT @Id, @CollectionId, @WorkId, @SortOrder, 'not_started', @AddedAt
                    WHERE {VaultSql.NotVaultEntity("@WorkId")}
                    """,
                    new
                    {
                        Id = Guid.NewGuid(),
                        CollectionId = collectionId,
                        WorkId = workId,
                        SortOrder = nextSortOrder,
                        AddedAt = DateTimeOffset.UtcNow.ToString("o"),
                    },
                    tx);
                if (inserted > 0)
                {
                    added++;
                    nextSortOrder++;
                }
            }

            return new PlaylistAddResult(added, alreadyThere);
        }, ct);
    }

    /// <inheritdoc/>
    public Task SetPinnedAsync(Guid profileId, Guid collectionId, bool pinned, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            if (pinned)
            {
                conn.Execute(
                    """
                    INSERT OR IGNORE INTO profile_pinned_playlists (profile_id, collection_id, pinned_at)
                    VALUES (@ProfileId, @CollectionId, @PinnedAt)
                    """,
                    new { ProfileId = profileId, CollectionId = collectionId, PinnedAt = DateTimeOffset.UtcNow.ToString("o") },
                    tx);
                return;
            }

            conn.Execute(
                "DELETE FROM profile_pinned_playlists WHERE profile_id = @ProfileId AND collection_id = @CollectionId",
                new { ProfileId = profileId, CollectionId = collectionId },
                tx);
        }, ct);
    }

    /// <inheritdoc/>
    public Task<IReadOnlySet<Guid>> GetPinnedCollectionIdsAsync(Guid profileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var ids = conn.Query<Guid>(
            "SELECT collection_id FROM profile_pinned_playlists WHERE profile_id = @ProfileId",
            new { ProfileId = profileId });
        return Task.FromResult<IReadOnlySet<Guid>>(ids.ToHashSet());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<Guid, double>> GetTotalDurationSecondsAsync(
        IReadOnlyCollection<Guid> collectionIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (collectionIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<Guid, double>>(new Dictionary<Guid, double>());
        }

        // A song's length is stored on its file (media asset) as duration_seconds. A song with two files counts once, by its longest.
        using var conn = _db.CreateConnection();
        var rows = conn.Query<DurationRow>(
            """
            WITH playlist_items AS (
                SELECT collection_id, work_id
                FROM collection_items
                WHERE collection_id IN (SELECT unhex(value) FROM json_each(@CollectionIds))),
            song_lengths AS (
                SELECT e.work_id AS work_id, MAX(CAST(cv.value AS REAL)) AS seconds
                FROM editions e
                JOIN media_assets ma ON ma.edition_id = e.id
                JOIN canonical_values cv ON cv.entity_id = ma.id AND cv.key IN ('duration_seconds', 'duration_sec')
                WHERE e.work_id IN (SELECT work_id FROM playlist_items)
                GROUP BY e.work_id)
            SELECT pi.collection_id AS CollectionId, SUM(sl.seconds) AS Seconds
            FROM playlist_items pi
            JOIN song_lengths sl ON sl.work_id = pi.work_id
            GROUP BY pi.collection_id
            """,
            new { CollectionIds = GuidSql.ToJsonHexArray(collectionIds) });

        return Task.FromResult<IReadOnlyDictionary<Guid, double>>(
            rows.Where(row => row.Seconds > 0).ToDictionary(row => row.CollectionId, row => row.Seconds));
    }

    private sealed class MovePlace
    {
        public int? FromPlace { get; init; }
        public int Total { get; init; }
    }

    private sealed class DurationRow
    {
        public Guid CollectionId { get; init; }
        public double Seconds { get; init; }
    }
}
