using System.Globalization;
using System.Data;
using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <inheritdoc cref="IViewMissingFileRepository"/>
public sealed class ViewMissingFileRepository(IDatabaseConnection database) : IViewMissingFileRepository
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <summary>Roles whose loss leaves nothing to look at (a Live Photo's video or a sidecar does not count).</summary>
    private static readonly string[] ViewableRoles =
    [
        LocalAssetFileRoles.Primary, LocalAssetFileRoles.Original, LocalAssetFileRoles.Raw, LocalAssetFileRoles.Jpeg,
    ];

    private sealed class PlaceRow
    {
        public Guid Id { get; init; }
        public Guid FileId { get; init; }
        public Guid LibraryId { get; init; }
        public Guid SourceId { get; init; }
        public string FilePath { get; init; } = string.Empty;
        public long ByteSize { get; init; }
        public string? MissingSince { get; init; }
    }

    private sealed class ItemStateRow
    {
        public string? MissingSince { get; init; }
        public long PartMissing { get; init; }
    }

    private sealed class FileStateRow
    {
        public string Role { get; init; } = string.Empty;
        public long Missing { get; init; }
        public string? LastMissingSince { get; init; }
    }

    public Task<IReadOnlyList<ViewIndexedPlace>> GetPlacesAsync(Guid sourceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<PlaceRow>(new CommandDefinition("""
            SELECT lfs.id AS Id, lfs.file_id AS FileId, lfs.library_id AS LibraryId, lfs.source_id AS SourceId,
                   lfs.file_path AS FilePath, lf.byte_size AS ByteSize, lfs.missing_since AS MissingSince
              FROM local_file_sources lfs
              JOIN local_files lf ON lf.id = lfs.file_id
             WHERE lfs.source_id = @sourceId;
            """, new { sourceId }, cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<ViewIndexedPlace>>(rows
            .Select(row => new ViewIndexedPlace(row.Id, row.FileId, row.LibraryId, row.SourceId, row.FilePath, row.ByteSize, Parse(row.MissingSince)))
            .ToList());
    }

    public Task<int> ApplyAsync(
        IReadOnlyCollection<Guid> markMissing, IReadOnlyCollection<Guid> clearMissing, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(markMissing);
        ArgumentNullException.ThrowIfNull(clearMissing);
        if (markMissing.Count == 0 && clearMissing.Count == 0)
        {
            return Task.FromResult(0);
        }

        var stamp = Format(now);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var touchedItems = new HashSet<Guid>();
            foreach (var placeId in markMissing)
            {
                token.ThrowIfCancellationRequested();
                connection.Execute("""
                    UPDATE local_file_sources SET missing_since = @stamp WHERE id = @placeId AND missing_since IS NULL;
                    """, new { placeId, stamp }, transaction);
                AddItemsOfPlace(connection, transaction, placeId, touchedItems);
            }

            foreach (var placeId in clearMissing)
            {
                token.ThrowIfCancellationRequested();
                connection.Execute("UPDATE local_file_sources SET missing_since = NULL WHERE id = @placeId;", new { placeId }, transaction);
                AddItemsOfPlace(connection, transaction, placeId, touchedItems);
            }

            var changed = 0;
            foreach (var itemId in touchedItems)
            {
                if (Recompute(connection, transaction, itemId))
                {
                    changed++;
                }
            }

            return changed;
        }, ct);
    }

    public Task SetFolderReachableAsync(Guid sourceId, bool reachable, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(
                "UPDATE view_sources SET folder_reachable = @value WHERE id = @sourceId AND folder_reachable <> @value;",
                new { sourceId, value = reachable ? 1 : 0 }, transaction);
        }, ct);

    public Task<IReadOnlyList<Guid>> GetPurgeCandidatesAsync(DateTimeOffset cutoff, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var ids = connection.Query<Guid>(new CommandDefinition($"""
            SELECT li.id
              FROM local_items li
             WHERE li.missing_since IS NOT NULL AND li.missing_since <= @cutoff
               AND {AllFoldersReachableSql}
             ORDER BY li.missing_since
             LIMIT @limit;
            """, new { cutoff = Format(cutoff), limit }, cancellationToken: ct)).ToList();
        return Task.FromResult<IReadOnlyList<Guid>>(ids);
    }

    public Task<bool> PurgeItemAsync(Guid itemId, DateTimeOffset cutoff, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            // Checked again inside the transaction: a file that came back, or a folder that went offline, a moment ago keeps the item.
            var stillEligible = connection.ExecuteScalar<long>($"""
                SELECT COUNT(*) FROM local_items li
                 WHERE li.id = @itemId AND li.missing_since IS NOT NULL AND li.missing_since <= @cutoff
                   AND {AllFoldersReachableSql};
                """, new { itemId, cutoff = Format(cutoff) }, transaction);
            if (stillEligible == 0)
            {
                return false;
            }

            var fileIds = connection.Query<Guid>(
                "SELECT DISTINCT file_id FROM local_item_files WHERE item_id = @itemId;", new { itemId }, transaction).ToList();
            var libraryId = connection.ExecuteScalar<Guid>("SELECT library_id FROM local_items WHERE id = @itemId;", new { itemId }, transaction);

            // The item row first: its cascades clear details, file links, tags, document rows, Gallery memberships, notes, search keys and
            // offline entries. A Gallery that used the item as its cover falls back through its own SET NULL link; Galleries stay.
            connection.Execute("DELETE FROM local_items WHERE id = @itemId;", new { itemId }, transaction);
            foreach (var fileId in fileIds)
            {
                // Only places that are themselves missing go, and only when no other item still uses the file.
                connection.Execute("""
                    DELETE FROM local_file_sources
                     WHERE file_id = @fileId AND library_id = @libraryId AND missing_since IS NOT NULL
                       AND NOT EXISTS (SELECT 1 FROM local_item_files WHERE file_id = @fileId);
                    """, new { fileId, libraryId }, transaction);
                connection.Execute("""
                    DELETE FROM local_files
                     WHERE id = @fileId
                       AND NOT EXISTS (SELECT 1 FROM local_item_files WHERE file_id = @fileId)
                       AND NOT EXISTS (SELECT 1 FROM local_file_sources WHERE file_id = @fileId);
                    """, new { fileId }, transaction);
            }

            return true;
        }, ct);

    /// <summary>An item is purged only when no place of its viewable files sits in a folder that could not be reached (D4).</summary>
    private const string AllFoldersReachableSql = """
        NOT EXISTS (SELECT 1
                      FROM local_item_files lif
                      JOIN local_file_sources lfs ON lfs.file_id = lif.file_id AND lfs.library_id = li.library_id
                      JOIN view_sources vs ON vs.id = lfs.source_id
                     WHERE lif.item_id = li.id AND vs.folder_reachable = 0)
        """;

    private static void AddItemsOfPlace(IDbConnection connection, IDbTransaction transaction, Guid placeId, HashSet<Guid> items)
    {
        foreach (var itemId in connection.Query<Guid>("""
            SELECT DISTINCT lif.item_id
              FROM local_file_sources lfs
              JOIN local_items li ON li.library_id = lfs.library_id
              JOIN local_item_files lif ON lif.item_id = li.id AND lif.file_id = lfs.file_id
             WHERE lfs.id = @placeId;
            """, new { placeId }, transaction))
        {
            items.Add(itemId);
        }
    }

    /// <summary>
    /// Works out again whether an item is Missing (no viewable file has a place left) or only partly missing (a companion has gone), from
    /// the marks on its files' places. A file with no places at all is not counted as lost. True when the item's state changed.
    /// </summary>
    internal static bool Recompute(IDbConnection connection, IDbTransaction transaction, Guid itemId)
    {
        var files = connection.Query<FileStateRow>("""
            SELECT lif.role AS Role,
                   CASE WHEN EXISTS (SELECT 1 FROM local_file_sources a WHERE a.file_id = lif.file_id AND a.library_id = li.library_id AND a.missing_since IS NOT NULL)
                         AND NOT EXISTS (SELECT 1 FROM local_file_sources b WHERE b.file_id = lif.file_id AND b.library_id = li.library_id AND b.missing_since IS NULL)
                        THEN 1 ELSE 0 END AS Missing,
                   (SELECT MAX(c.missing_since) FROM local_file_sources c WHERE c.file_id = lif.file_id AND c.library_id = li.library_id) AS LastMissingSince
              FROM local_item_files lif
              JOIN local_items li ON li.id = lif.item_id
             WHERE lif.item_id = @itemId;
            """, new { itemId }, transaction).ToList();
        var viewable = files.Where(file => ViewableRoles.Contains(file.Role, StringComparer.Ordinal)).ToList();
        var itemMissing = viewable.Count > 0 && viewable.All(file => file.Missing == 1);
        var missingSince = itemMissing ? viewable.Max(file => file.LastMissingSince) : null;
        var partMissing = !itemMissing && files.Any(file => file.Missing == 1);

        var current = connection.QuerySingleOrDefault<ItemStateRow>(
            "SELECT missing_since AS MissingSince, part_missing AS PartMissing FROM local_items WHERE id = @itemId;",
            new { itemId }, transaction);
        if (current is null)
        {
            return false;
        }

        // Keep the earlier time while the item stays Missing, so the retention clock is never restarted by a later recheck.
        if (itemMissing && current.MissingSince is not null)
        {
            missingSince = current.MissingSince;
        }

        var changed = (current.MissingSince is null) != (missingSince is null)
            || current.PartMissing != (partMissing ? 1 : 0);
        connection.Execute(
            "UPDATE local_items SET missing_since = @missingSince, part_missing = @part WHERE id = @itemId;",
            new { itemId, missingSince, part = partMissing ? 1 : 0 }, transaction);
        return changed;
    }

    internal static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset? Parse(string? value) =>
        value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
}
