using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite store of the Extras found beside titles (Extras spec WP1). One row per file (<c>file_path</c>), grouped under the folder
/// the title lives in (<c>item_folder</c>). Every call uses a short-lived connection.
/// </summary>
public sealed class ItemExtraRepository(IDatabaseConnection db) : IItemExtraRepository
{
    private const string SelectSql = """
        SELECT id AS Id,
               library_id AS LibraryId,
               item_folder AS ItemFolder,
               file_path AS FilePath,
               kind AS Kind,
               origin AS Origin,
               title AS Title,
               group_key AS GroupKey,
               sort_order AS SortOrder,
               size_bytes AS SizeBytes,
               duration_seconds AS DurationSeconds,
               page_count AS PageCount,
               target_season AS TargetSeason,
               target_episode AS TargetEpisode,
               found_at AS FoundAt
        FROM item_extras
        """;

    public Task<IReadOnlyList<ItemExtra>> ListByItemFoldersAsync(IReadOnlyCollection<string> itemFolders, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemFolders);
        ct.ThrowIfCancellationRequested();
        if (itemFolders.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ItemExtra>>([]);
        }

        using var connection = db.CreateConnection();
        var rows = connection.Query<Row>(new CommandDefinition(
            SelectSql + " WHERE item_folder IN @folders AND origin <> 'user_episode' ORDER BY kind, sort_order, title COLLATE NOCASE, file_path;",
            new { folders = itemFolders.ToArray() },
            cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<ItemExtra>>([.. rows.Select(ToModel)]);
    }

    public Task<IReadOnlyList<ItemExtra>> ListAllInItemFolderAsync(string itemFolder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemFolder);
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var rows = connection.Query<Row>(new CommandDefinition(
            SelectSql + " WHERE item_folder = @itemFolder ORDER BY file_path;",
            new { itemFolder },
            cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<ItemExtra>>([.. rows.Select(ToModel)]);
    }

    public Task<ItemExtra?> GetAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var row = connection.QuerySingleOrDefault<Row>(new CommandDefinition(
            SelectSql + " WHERE id = @id AND origin <> 'user_episode';",
            new { id },
            cancellationToken: ct));
        return Task.FromResult(row is null ? null : ToModel(row));
    }

    public Task<ItemExtra?> GetByPathAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var row = connection.QuerySingleOrDefault<Row>(new CommandDefinition(
            SelectSql + " WHERE file_path = @filePath;",
            new { filePath },
            cancellationToken: ct));
        return Task.FromResult(row is null ? null : ToModel(row));
    }

    public Task SyncItemFolderAsync(
        string itemFolder,
        IReadOnlyList<ItemExtra> found,
        IReadOnlyCollection<string> goneFiles,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemFolder);
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(goneFiles);
        ct.ThrowIfCancellationRequested();

        return db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var existing = connection.Query<Row>(new CommandDefinition(
                    SelectSql + " WHERE item_folder = @itemFolder;",
                    new { itemFolder },
                    transaction,
                    cancellationToken: token))
                .ToDictionary(row => row.FilePath, StringComparer.OrdinalIgnoreCase);
            var gone = new HashSet<string>(goneFiles, StringComparer.OrdinalIgnoreCase);
            var foundPaths = new HashSet<string>(found.Select(extra => extra.FilePath), StringComparer.OrdinalIgnoreCase);

            foreach (var extra in found)
            {
                // A person's decision about this file stands; the rule neither adds over it nor removes it.
                if (existing.TryGetValue(extra.FilePath, out var current) && current.Origin != ItemExtraOrigins.Rule)
                {
                    continue;
                }

                connection.Execute(new CommandDefinition("""
                INSERT INTO item_extras
                    (id, library_id, item_folder, file_path, kind, origin, title, group_key, sort_order, size_bytes,
                     duration_seconds, page_count, found_at)
                VALUES
                    (@id, @libraryId, @itemFolder, @filePath, @kind, 'rule', @title, @groupKey, @sortOrder, @sizeBytes,
                     @durationSeconds, @pageCount, @foundAt)
                ON CONFLICT(file_path) DO UPDATE SET
                    library_id = excluded.library_id,
                    item_folder = excluded.item_folder,
                    kind = excluded.kind,
                    title = excluded.title,
                    group_key = excluded.group_key,
                    sort_order = excluded.sort_order,
                    size_bytes = excluded.size_bytes,
                    duration_seconds = excluded.duration_seconds,
                    page_count = excluded.page_count
                WHERE item_extras.origin = 'rule';
                """,
                    new
                    {
                        id = existing.TryGetValue(extra.FilePath, out var known) ? known.Id : extra.Id,
                        libraryId = extra.LibraryId,
                        itemFolder,
                        filePath = extra.FilePath,
                        kind = extra.Kind,
                        title = extra.Title,
                        groupKey = extra.GroupKey,
                        sortOrder = extra.SortOrder,
                        sizeBytes = extra.SizeBytes,
                        durationSeconds = extra.DurationSeconds,
                        pageCount = extra.PageCount,
                        foundAt = extra.FoundAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
                    },
                    transaction,
                    cancellationToken: token));
            }

            foreach (var row in existing.Values)
            {
                var remove = row.Origin == ItemExtraOrigins.Rule
                    ? !foundPaths.Contains(row.FilePath)
                    : gone.Contains(row.FilePath);
                if (remove)
                {
                    connection.Execute(new CommandDefinition(
                        "DELETE FROM item_extras WHERE id = @id;",
                        new { id = row.Id },
                        transaction,
                        cancellationToken: token));
                }
            }

        }, ct);
    }

    public Task SetDecisionAsync(ItemExtra extra, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(extra);
        if (extra.Origin == ItemExtraOrigins.Rule)
        {
            throw new ArgumentException("A person's decision is never recorded as the rule's.", nameof(extra));
        }

        ct.ThrowIfCancellationRequested();
        return db.ExecuteWriteAsync((connection, transaction, token) => connection.Execute(new CommandDefinition("""
            INSERT INTO item_extras
                (id, library_id, item_folder, file_path, kind, origin, title, group_key, sort_order, size_bytes,
                 duration_seconds, page_count, target_season, target_episode, found_at)
            VALUES
                (@id, @libraryId, @itemFolder, @filePath, @kind, @origin, @title, @groupKey, @sortOrder, @sizeBytes,
                 @durationSeconds, @pageCount, @targetSeason, @targetEpisode, @foundAt)
            ON CONFLICT(file_path) DO UPDATE SET
                library_id = excluded.library_id,
                item_folder = excluded.item_folder,
                kind = excluded.kind,
                origin = excluded.origin,
                title = excluded.title,
                group_key = excluded.group_key,
                sort_order = excluded.sort_order,
                size_bytes = excluded.size_bytes,
                duration_seconds = excluded.duration_seconds,
                page_count = excluded.page_count,
                target_season = excluded.target_season,
                target_episode = excluded.target_episode;
            """,
            new
            {
                id = extra.Id,
                libraryId = extra.LibraryId,
                itemFolder = extra.ItemFolder,
                filePath = extra.FilePath,
                kind = extra.Kind,
                origin = extra.Origin,
                title = extra.Title,
                groupKey = extra.GroupKey,
                sortOrder = extra.SortOrder,
                sizeBytes = extra.SizeBytes,
                durationSeconds = extra.DurationSeconds,
                pageCount = extra.PageCount,
                targetSeason = extra.TargetSeason,
                targetEpisode = extra.TargetEpisode,
                foundAt = extra.FoundAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            },
            transaction,
            cancellationToken: token)), ct);
    }

    public Task<bool> RemoveByPathAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ct.ThrowIfCancellationRequested();

        return db.ExecuteWriteAsync((connection, transaction, token) => connection.Execute(new CommandDefinition(
            "DELETE FROM item_extras WHERE file_path = @filePath;",
            new { filePath },
            transaction,
            cancellationToken: token)) > 0, ct);
    }

    public Task<int> RemoveUnderFolderAsync(string folder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ct.ThrowIfCancellationRequested();

        var trimmed = folder.TrimEnd('\\', '/');
        return db.ExecuteWriteAsync((connection, transaction, token) => connection.Execute(new CommandDefinition("""
            DELETE FROM item_extras
            WHERE item_folder = @folder
               OR substr(item_folder, 1, length(@folder) + 1) IN (@withSlash, @withBackslash);
            """,
            new { folder = trimmed, withSlash = trimmed + "/", withBackslash = trimmed + "\\" },
            transaction,
            cancellationToken: token)), ct);
    }

    public Task<IReadOnlyList<string>> ListItemFoldersAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var folders = connection.Query<string>(new CommandDefinition(
            "SELECT DISTINCT item_folder FROM item_extras ORDER BY item_folder;",
            cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<string>>([.. folders]);
    }

    private static ItemExtra ToModel(Row row) => new(
        row.Id,
        row.LibraryId,
        row.ItemFolder,
        row.FilePath,
        row.Kind,
        row.Origin,
        row.Title,
        row.GroupKey,
        row.SortOrder,
        row.SizeBytes,
        row.DurationSeconds,
        row.PageCount,
        DateTimeOffset.TryParse(row.FoundAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var found)
            ? found
            : DateTimeOffset.MinValue,
        row.TargetSeason,
        row.TargetEpisode);

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string LibraryId { get; init; } = string.Empty;
        public string ItemFolder { get; init; } = string.Empty;
        public string FilePath { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string Origin { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string? GroupKey { get; init; }
        public int SortOrder { get; init; }
        public long SizeBytes { get; init; }
        public double? DurationSeconds { get; init; }
        public int? PageCount { get; init; }
        public int? TargetSeason { get; init; }
        public int? TargetEpisode { get; init; }
        public string? FoundAt { get; init; }
    }
}
