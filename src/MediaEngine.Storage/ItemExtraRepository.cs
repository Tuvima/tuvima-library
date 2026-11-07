using System.Globalization;
using Dapper;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite store of the Extras found beside titles (Extras spec WP1, universal editor v5 WP2: supporting files). One row per file
/// (<c>file_path</c>), grouped under the folder the title lives in (<c>item_folder</c>). The values a person owns (own title and
/// description, role, scope, shown-alongside, a chosen group, tags) live in their own columns that a rescan never writes; the values
/// the Engine reads from folder and file names are stored beside them and recomputed on every pass. Every call uses a short-lived connection.
/// </summary>
public sealed class ItemExtraRepository(IDatabaseConnection db, IExtrasConfigurationSource? rules = null) : IItemExtraRepository
{
    private const int TagQueryChunk = 400;
    private const int MaxSearchWords = 6;

    private const string SelectSql = """
        SELECT e.id AS Id,
               e.library_id AS LibraryId,
               e.item_folder AS ItemFolder,
               e.file_path AS FilePath,
               e.kind AS Kind,
               e.origin AS Origin,
               e.title AS Title,
               e.group_key AS GroupKey,
               e.sort_order AS SortOrder,
               e.size_bytes AS SizeBytes,
               e.duration_seconds AS DurationSeconds,
               e.page_count AS PageCount,
               e.target_season AS TargetSeason,
               e.target_episode AS TargetEpisode,
               e.extra_kind AS ExtraKind,
               e.place_season AS PlaceSeason,
               e.place_episode AS PlaceEpisode,
               e.place_track AS PlaceTrack,
               e.found_at AS FoundAt,
               e.own_title AS OwnTitle,
               e.own_description AS OwnDescription,
               e.role AS Role,
               e.inferred_role AS InferredRole,
               e.group_id AS GroupId,
               g.name AS GroupName,
               e.group_source AS GroupSource,
               e.inferred_group AS InferredGroup,
               e.scope AS Scope,
               e.inferred_scope AS InferredScope,
               e.alongside AS Alongside,
               e.inferred_alongside AS InferredAlongside,
               e.missing_since AS MissingSince
        FROM item_extras e
        LEFT JOIN item_extra_groups g ON g.id = e.group_id
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
            SelectSql + " WHERE e.item_folder IN @folders AND e.origin <> 'user_episode' ORDER BY e.kind, e.sort_order, e.title COLLATE NOCASE, e.file_path;",
            new { folders = itemFolders.ToArray() },
            cancellationToken: ct)).ToList();
        return Task.FromResult<IReadOnlyList<ItemExtra>>(ToModels(connection, rows, ct));
    }

    public Task<IReadOnlyList<ItemExtra>> ListAllInItemFolderAsync(string itemFolder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemFolder);
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var rows = connection.Query<Row>(new CommandDefinition(
            SelectSql + " WHERE e.item_folder = @itemFolder ORDER BY e.file_path;",
            new { itemFolder },
            cancellationToken: ct)).ToList();
        return Task.FromResult<IReadOnlyList<ItemExtra>>(ToModels(connection, rows, ct));
    }

    public Task<ItemExtra?> GetAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var row = connection.QuerySingleOrDefault<Row>(new CommandDefinition(
            SelectSql + " WHERE e.id = @id AND e.origin <> 'user_episode';",
            new { id },
            cancellationToken: ct));
        return Task.FromResult(row is null ? null : ToModels(connection, [row], ct)[0]);
    }

    public Task<ItemExtra?> GetByPathAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var row = connection.QuerySingleOrDefault<Row>(new CommandDefinition(
            SelectSql + " WHERE e.file_path = @filePath;",
            new { filePath },
            cancellationToken: ct));
        return Task.FromResult(row is null ? null : ToModels(connection, [row], ct)[0]);
    }

    public Task SyncItemFolderAsync(
        string itemFolder,
        IReadOnlyList<ItemExtra> found,
        IReadOnlyCollection<string> goneFiles,
        DateTimeOffset? purgeMissingBefore = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemFolder);
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(goneFiles);
        ct.ThrowIfCancellationRequested();

        return db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var existing = connection.Query<Row>(new CommandDefinition(
                    SelectSql + " WHERE e.item_folder = @itemFolder;",
                    new { itemFolder },
                    transaction,
                    cancellationToken: token))
                .ToDictionary(row => row.FilePath, StringComparer.OrdinalIgnoreCase);
            var gone = new HashSet<string>(goneFiles, StringComparer.OrdinalIgnoreCase);
            var foundPaths = new HashSet<string>(found.Select(extra => extra.FilePath), StringComparer.OrdinalIgnoreCase);
            var now = DateTimeOffset.UtcNow;

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
                     duration_seconds, page_count, found_at, inferred_role, inferred_group, inferred_scope, inferred_alongside,
                     missing_since)
                VALUES
                    (@id, @libraryId, @itemFolder, @filePath, @kind, 'rule', @title, @groupKey, @sortOrder, @sizeBytes,
                     @durationSeconds, @pageCount, @foundAt, @inferredRole, @inferredGroup, @inferredScope, @inferredAlongside,
                     NULL)
                ON CONFLICT(file_path) DO UPDATE SET
                    library_id = excluded.library_id,
                    item_folder = excluded.item_folder,
                    kind = excluded.kind,
                    title = excluded.title,
                    group_key = excluded.group_key,
                    sort_order = excluded.sort_order,
                    size_bytes = excluded.size_bytes,
                    duration_seconds = excluded.duration_seconds,
                    page_count = excluded.page_count,
                    inferred_role = excluded.inferred_role,
                    inferred_group = excluded.inferred_group,
                    inferred_scope = excluded.inferred_scope,
                    inferred_alongside = excluded.inferred_alongside,
                    missing_since = NULL
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
                        foundAt = FormatTime(extra.FoundAt),
                        inferredRole = extra.InferredRole,
                        inferredGroup = Blank(extra.InferredGroup),
                        inferredScope = extra.InferredScope.Key,
                        inferredAlongside = extra.InferredAlongside?.Key,
                    },
                    transaction,
                    cancellationToken: token));

                // The group read from the folder, unless a person chose one for this file.
                if (current is not { GroupSource: ExtraGroupSources.You })
                {
                    var inferredGroup = Blank(extra.InferredGroup);
                    var groupId = inferredGroup is null ? (Guid?)null : EnsureGroup(connection, transaction, itemFolder, inferredGroup, ExtraGroupSources.Folder);
                    connection.Execute(new CommandDefinition(
                        "UPDATE item_extras SET group_id = @groupId, group_source = @source WHERE file_path = @filePath AND origin = 'rule';",
                        new { groupId, source = groupId is null ? null : ExtraGroupSources.Folder, filePath = extra.FilePath },
                        transaction,
                        cancellationToken: token));
                }
            }

            foreach (var row in existing.Values)
            {
                var fileIsGone = gone.Contains(row.FilePath);
                var stillFound = foundPaths.Contains(row.FilePath);
                if (row.Origin == ItemExtraOrigins.Rule && !stillFound && !fileIsGone)
                {
                    // The file is there but the rule no longer reads it as an Extra: the record goes,
                    // unless a person has set something on it. Their values are theirs to keep.
                    if (!HasPersonValues(connection, transaction, row, token))
                    {
                        DeleteRows(connection, transaction, "id = @id", new { id = row.Id }, token);
                    }

                    continue;
                }

                if (!fileIsGone)
                {
                    if (row.MissingSince is not null && row.Origin != ItemExtraOrigins.Rule)
                    {
                        connection.Execute(new CommandDefinition(
                            "UPDATE item_extras SET missing_since = NULL WHERE id = @id;", new { id = row.Id }, transaction, cancellationToken: token));
                    }

                    continue;
                }

                // The file is no longer on disk: keep the record as "File missing" until it has been missing for long enough.
                var missingSince = ParseTime(row.MissingSince);
                if (missingSince is null)
                {
                    connection.Execute(new CommandDefinition(
                        "UPDATE item_extras SET missing_since = @now WHERE id = @id;",
                        new { now = FormatTime(now), id = row.Id },
                        transaction,
                        cancellationToken: token));
                }
                else if (purgeMissingBefore is { } cutoff && missingSince < cutoff)
                {
                    DeleteRows(connection, transaction, "id = @id", new { id = row.Id }, token);
                }
            }

            // A group read from a folder that no longer holds a file goes with the folder; a group a person made stays until they delete it.
            connection.Execute(new CommandDefinition("""
                DELETE FROM item_extra_groups
                WHERE item_folder = @itemFolder AND source = 'folder'
                  AND NOT EXISTS (SELECT 1 FROM item_extras e WHERE e.group_id = item_extra_groups.id);
                """,
                new { itemFolder },
                transaction,
                cancellationToken: token));
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
                 duration_seconds, page_count, target_season, target_episode, extra_kind, place_season, place_episode,
                 place_track, found_at)
            VALUES
                (@id, @libraryId, @itemFolder, @filePath, @kind, @origin, @title, @groupKey, @sortOrder, @sizeBytes,
                 @durationSeconds, @pageCount, @targetSeason, @targetEpisode, @extraKind, @placeSeason, @placeEpisode,
                 @placeTrack, @foundAt)
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
                target_episode = excluded.target_episode,
                extra_kind = excluded.extra_kind,
                place_season = excluded.place_season,
                place_episode = excluded.place_episode,
                place_track = excluded.place_track,
                missing_since = NULL;
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
                extraKind = extra.ExtraKind,
                placeSeason = extra.PlaceSeason,
                placeEpisode = extra.PlaceEpisode,
                placeTrack = extra.PlaceTrack,
                foundAt = FormatTime(extra.FoundAt),
            },
            transaction,
            cancellationToken: token)), ct);
    }

    public Task<bool> RemoveByPathAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ct.ThrowIfCancellationRequested();

        return db.ExecuteWriteAsync((connection, transaction, token) =>
            DeleteRows(connection, transaction, "file_path = @filePath", new { filePath }, token) > 0, ct);
    }

    public Task<IReadOnlyList<ItemExtra>> SearchAsync(IReadOnlyList<string> words, int limit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(words);
        ct.ThrowIfCancellationRequested();
        var terms = words.Select(word => word?.Trim() ?? string.Empty).Where(word => word.Length > 0).Take(MaxSearchWords).ToList();
        if (terms.Count == 0 || limit <= 0)
        {
            return Task.FromResult<IReadOnlyList<ItemExtra>>([]);
        }

        // Every word must be found in the title shown, the description or the file name (the last part of the path, either slash).
        var parameters = new DynamicParameters();
        parameters.Add("limit", limit);
        var clauses = new List<string>();
        for (var index = 0; index < terms.Count; index++)
        {
            var name = $"w{index.ToString(CultureInfo.InvariantCulture)}";
            parameters.Add(name, "%" + EscapeLike(terms[index]) + "%");
            clauses.Add($$"""
                (COALESCE(NULLIF(e.own_title, ''), e.title) LIKE @{{name}} ESCAPE '\'
                 OR COALESCE(e.own_description, '') LIKE @{{name}} ESCAPE '\'
                 OR replace(replace(e.file_path, char(92), '/'), rtrim(replace(e.file_path, char(92), '/'), replace(replace(e.file_path, char(92), '/'), '/', '')), '')
                    LIKE @{{name}} ESCAPE '\')
                """);
        }

        using var connection = db.CreateConnection();
        var rows = connection.Query<Row>(new CommandDefinition(
            SelectSql + " WHERE e.origin <> 'user_episode' AND " + string.Join(" AND ", clauses)
                + " ORDER BY COALESCE(NULLIF(e.own_title, ''), e.title) COLLATE NOCASE, e.file_path LIMIT @limit;",
            parameters,
            cancellationToken: ct)).ToList();
        return Task.FromResult<IReadOnlyList<ItemExtra>>(ToModels(connection, rows, ct));
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    public Task<int> RemoveManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ct.ThrowIfCancellationRequested();
        if (ids.Count == 0)
        {
            return Task.FromResult(0);
        }

        return db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var removed = 0;
            foreach (var chunk in ids.Distinct().Chunk(TagQueryChunk))
            {
                token.ThrowIfCancellationRequested();
                removed += DeleteRows(connection, transaction, "id IN @ids", new { ids = chunk.Select(GuidSql.ToBlob).ToArray() }, token);
            }

            return removed;
        }, ct);
    }

    public Task<bool> RelocateFileAsync(Guid id, string newFilePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newFilePath);
        ct.ThrowIfCancellationRequested();

        return db.ExecuteWriteAsync((connection, transaction, token) =>
            connection.Execute(new CommandDefinition(
                "UPDATE item_extras SET file_path = @newFilePath WHERE id = @id;",
                new { id, newFilePath },
                transaction,
                cancellationToken: token)) > 0, ct);
    }

    public Task<int> RemoveUnderFolderAsync(string folder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ct.ThrowIfCancellationRequested();

        var trimmed = folder.TrimEnd('\\', '/');
        return db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var parameters = new { folder = trimmed, withSlash = trimmed + "/", withBackslash = trimmed + "\\" };
            const string below = "(item_folder = @folder OR substr(item_folder, 1, length(@folder) + 1) IN (@withSlash, @withBackslash))";
            var removed = DeleteRows(connection, transaction, below, parameters, token);

            // Groups are stored with the title and deleted with it.
            connection.Execute(new CommandDefinition(
                "DELETE FROM item_extra_groups WHERE " + below + ";",
                parameters,
                transaction,
                cancellationToken: token));
            return removed;
        }, ct);
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

    // ── Groups ──────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<ExtraGroup>> ListGroupsAsync(string itemFolder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemFolder);
        ct.ThrowIfCancellationRequested();

        using var connection = db.CreateConnection();
        var rows = connection.Query<GroupRow>(new CommandDefinition("""
            SELECT g.id AS Id, g.item_folder AS ItemFolder, g.name AS Name, g.sort_order AS SortOrder,
                   (SELECT COUNT(*) FROM item_extras e WHERE e.group_id = g.id AND e.origin <> 'user_episode') AS FileCount
            FROM item_extra_groups g
            WHERE g.item_folder = @itemFolder
            ORDER BY g.sort_order, g.name COLLATE NOCASE;
            """,
            new { itemFolder },
            cancellationToken: ct));
        return Task.FromResult<IReadOnlyList<ExtraGroup>>([.. rows.Select(row => new ExtraGroup(row.Id, row.ItemFolder, row.Name, row.SortOrder, row.FileCount))]);
    }

    public Task<ExtraGroup> EnsureGroupAsync(string itemFolder, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ct.ThrowIfCancellationRequested();

        return db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var id = EnsureGroup(connection, transaction, itemFolder, name.Trim(), ExtraGroupSources.You);
            var row = connection.QuerySingle<GroupRow>(new CommandDefinition(
                "SELECT id AS Id, item_folder AS ItemFolder, name AS Name, sort_order AS SortOrder, 0 AS FileCount FROM item_extra_groups WHERE id = @id;",
                new { id },
                transaction,
                cancellationToken: token));
            return new ExtraGroup(row.Id, row.ItemFolder, row.Name, row.SortOrder);
        }, ct);
    }

    public Task<bool> RenameGroupAsync(Guid groupId, string newName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        ct.ThrowIfCancellationRequested();

        var trimmed = newName.Trim();
        return db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var folder = connection.QuerySingleOrDefault<string>(new CommandDefinition(
                "SELECT item_folder FROM item_extra_groups WHERE id = @groupId;", new { groupId }, transaction, cancellationToken: token));
            if (folder is null)
            {
                return false;
            }

            var clash = connection.QuerySingleOrDefault<Guid?>(new CommandDefinition(
                "SELECT id FROM item_extra_groups WHERE item_folder = @folder AND name = @name AND id <> @groupId;",
                new { folder, name = trimmed, groupId },
                transaction,
                cancellationToken: token));
            if (clash is not null)
            {
                return false;
            }

            // A group a person renamed is theirs: the group stays, and so does the grouping of its files,
            // so a rescan neither recreates the old folder group nor drops the renamed one.
            var renamed = connection.Execute(new CommandDefinition(
                "UPDATE item_extra_groups SET name = @name, source = 'you' WHERE id = @groupId;",
                new { name = trimmed, groupId },
                transaction,
                cancellationToken: token)) > 0;
            connection.Execute(new CommandDefinition(
                "UPDATE item_extras SET group_source = 'you' WHERE group_id = @groupId;",
                new { groupId },
                transaction,
                cancellationToken: token));
            return renamed;
        }, ct);
    }

    public Task<bool> DeleteGroupAsync(Guid groupId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            connection.Execute(new CommandDefinition(
                "UPDATE item_extras SET group_id = NULL, group_source = NULL WHERE group_id = @groupId;",
                new { groupId },
                transaction,
                cancellationToken: token));
            return connection.Execute(new CommandDefinition(
                "DELETE FROM item_extra_groups WHERE id = @groupId;", new { groupId }, transaction, cancellationToken: token)) > 0;
        }, ct);
    }

    public Task ReorderGroupsAsync(string itemFolder, IReadOnlyList<Guid> orderedGroupIds, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemFolder);
        ArgumentNullException.ThrowIfNull(orderedGroupIds);
        ct.ThrowIfCancellationRequested();

        return db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var current = connection.Query<Guid>(new CommandDefinition(
                    "SELECT id FROM item_extra_groups WHERE item_folder = @itemFolder ORDER BY sort_order, name COLLATE NOCASE;",
                    new { itemFolder },
                    transaction,
                    cancellationToken: token))
                .ToList();
            var order = orderedGroupIds.Where(current.Contains).Distinct().ToList();
            order.AddRange(current.Where(id => !order.Contains(id)));
            for (var index = 0; index < order.Count; index++)
            {
                connection.Execute(new CommandDefinition(
                    "UPDATE item_extra_groups SET sort_order = @index WHERE id = @id;",
                    new { index, id = order[index] },
                    transaction,
                    cancellationToken: token));
            }
        }, ct);
    }

    // ── What a person owns ──────────────────────────────────────────────────────

    public async Task<ItemExtra?> UpdateOwnedAsync(Guid id, ExtraOwnedUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ct.ThrowIfCancellationRequested();

        var changed = await UpdateOwnedManyAsync([id], update, ct).ConfigureAwait(false);
        if (changed == 0)
        {
            return null;
        }

        using var connection = db.CreateConnection();
        var row = connection.QuerySingleOrDefault<Row>(new CommandDefinition(
            SelectSql + " WHERE e.id = @id;", new { id }, cancellationToken: ct));
        return row is null ? null : ToModels(connection, [row], ct)[0];
    }

    public Task<int> UpdateOwnedManyAsync(IReadOnlyCollection<Guid> ids, ExtraOwnedUpdate update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(update);
        ct.ThrowIfCancellationRequested();
        if (ids.Count == 0 || update.IsEmpty)
        {
            return Task.FromResult(0);
        }

        if (update.Scope is { State: ExtraValueState.Set, Value.Level: ExtraScopeLevel.None })
        {
            throw new ArgumentException("\"none\" is a value for shown-alongside only; clear the scope to return to the folder location.", nameof(update));
        }

        return db.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var changed = 0;
            foreach (var id in ids.Distinct())
            {
                token.ThrowIfCancellationRequested();
                var row = connection.QuerySingleOrDefault<Row>(new CommandDefinition(
                    SelectSql + " WHERE e.id = @id;", new { id }, transaction, cancellationToken: token));
                if (row is null)
                {
                    continue;
                }

                ApplyOwned(connection, transaction, row, update, rules?.Current, token);
                changed++;
            }

            return changed;
        }, ct);
    }

    /// <summary>True when a person has set anything on this record: title, description, role, scope, alongside, group or tags.</summary>
    private static bool HasPersonValues(SqliteConnection connection, SqliteTransaction transaction, Row row, CancellationToken token)
    {
        if (row.OwnTitle is not null || row.OwnDescription is not null || row.Role is not null
            || row.Scope is not null || row.Alongside is not null || row.GroupSource == ExtraGroupSources.You
            || row.PlaceSeason is not null || row.PlaceEpisode is not null || row.PlaceTrack is not null)
        {
            return true;
        }

        return connection.ExecuteScalar<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM item_extra_tag_links WHERE extra_id = @id;",
            new { id = row.Id },
            transaction,
            cancellationToken: token)) > 0;
    }

    private static void ApplyOwned(
        SqliteConnection connection, SqliteTransaction transaction, Row row, ExtraOwnedUpdate update, ExtrasConfiguration? rules, CancellationToken token)
    {
        var sets = new List<string>();
        var parameters = new DynamicParameters();
        parameters.Add("id", row.Id);

        void Set(string column, object? value)
        {
            sets.Add($"{column} = @{column}");
            parameters.Add(column, value);
        }

        if (update.Title.IsChanged)
        {
            Set("own_title", Blank(update.Title.Value));
        }

        if (update.Description.IsChanged)
        {
            Set("own_description", Blank(update.Description.Value));
        }

        if (update.Role.IsChanged)
        {
            if (update.Role.IsSet && Blank(update.Role.Value) is { } role)
            {
                // The roles a person may pick are the configured list for this kind of file (config/extras.json).
                if (rules is not null && !rules.RoleFits(row.Kind, role))
                {
                    throw new ExtraRoleNotAllowedException(role, row.Kind);
                }

                Set("role", role);
                Set("extra_kind", ExtraRoles.ToExtraKind(role, row.Kind));
            }
            else
            {
                Set("role", null);
                Set("extra_kind", null);
            }
        }

        if (update.Scope.IsChanged)
        {
            if (update.Scope.IsSet)
            {
                Set("scope", update.Scope.Value.Key);
            }
            else
            {
                // "Use folder location": the guess applies again, and so does nothing the v4 picker left behind.
                Set("scope", null);
                Set("place_season", null);
                Set("place_episode", null);
                Set("place_track", null);
            }
        }

        if (update.Alongside.IsChanged)
        {
            Set("alongside", update.Alongside.IsSet ? update.Alongside.Value.Key : null);
        }

        if (update.GroupName.IsChanged)
        {
            if (update.GroupName.IsSet && Blank(update.GroupName.Value) is { } name)
            {
                Set("group_id", EnsureGroup(connection, transaction, row.ItemFolder, name, ExtraGroupSources.You));
                Set("group_source", ExtraGroupSources.You);
            }
            else
            {
                // Back to the group the folder names, if it names one.
                var inferred = Blank(row.InferredGroup);
                Set("group_id", inferred is null ? null : EnsureGroup(connection, transaction, row.ItemFolder, inferred, ExtraGroupSources.Folder));
                Set("group_source", inferred is null ? null : ExtraGroupSources.Folder);
            }
        }

        if (sets.Count > 0)
        {
            connection.Execute(new CommandDefinition(
                "UPDATE item_extras SET " + string.Join(", ", sets) + " WHERE id = @id;",
                parameters,
                transaction,
                cancellationToken: token));
        }

        ApplyTags(connection, transaction, row.Id, update, token);
    }

    private static void ApplyTags(SqliteConnection connection, SqliteTransaction transaction, Guid extraId, ExtraOwnedUpdate update, CancellationToken token)
    {
        if (update.ReplaceTags is not null)
        {
            connection.Execute(new CommandDefinition(
                "DELETE FROM item_extra_tag_links WHERE extra_id = @extraId;", new { extraId }, transaction, cancellationToken: token));
        }

        foreach (var name in update.RemoveTags.Select(Blank).Where(tag => tag is not null))
        {
            connection.Execute(new CommandDefinition("""
                DELETE FROM item_extra_tag_links
                WHERE extra_id = @extraId AND tag_id IN (SELECT id FROM tags WHERE name = @name);
                """,
                new { extraId, name },
                transaction,
                cancellationToken: token));
        }

        foreach (var name in (update.ReplaceTags ?? update.AddTags).Select(Blank).Where(tag => tag is not null).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tagId = EnsureTag(connection, transaction, name!);
            connection.Execute(new CommandDefinition(
                "INSERT OR IGNORE INTO item_extra_tag_links (extra_id, tag_id, added_at) VALUES (@extraId, @tagId, @addedAt);",
                new { extraId, tagId, addedAt = FormatTime(DateTimeOffset.UtcNow) },
                transaction,
                cancellationToken: token));
        }
    }

    private static Guid EnsureTag(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        var existing = connection.QuerySingleOrDefault<Guid?>(new CommandDefinition(
            "SELECT id FROM tags WHERE name = @name;", new { name }, transaction));
        if (existing is { } found)
        {
            return found;
        }

        var id = Guid.NewGuid();
        connection.Execute(new CommandDefinition(
            "INSERT INTO tags (id, name, created_at) VALUES (@id, @name, @createdAt);",
            new { id, name, createdAt = FormatTime(DateTimeOffset.UtcNow) },
            transaction));
        return id;
    }

    private static Guid EnsureGroup(SqliteConnection connection, SqliteTransaction transaction, string itemFolder, string name, string source)
    {
        var existing = connection.QuerySingleOrDefault<Guid?>(new CommandDefinition(
            "SELECT id FROM item_extra_groups WHERE item_folder = @itemFolder AND name = @name;",
            new { itemFolder, name },
            transaction));
        if (existing is { } found)
        {
            return found;
        }

        var id = Guid.NewGuid();
        connection.Execute(new CommandDefinition("""
            INSERT INTO item_extra_groups (id, item_folder, name, sort_order, source, created_at)
            VALUES (@id, @itemFolder, @name,
                    COALESCE((SELECT MAX(sort_order) + 1 FROM item_extra_groups WHERE item_folder = @itemFolder), 0),
                    @source, @createdAt);
            """,
            new { id, itemFolder, name, source, createdAt = FormatTime(DateTimeOffset.UtcNow) },
            transaction));
        return id;
    }

    /// <summary>Deletes rows (and the tag links that hang on them) matching a condition over item_extras' own columns.</summary>
    private static int DeleteRows(SqliteConnection connection, SqliteTransaction transaction, string condition, object parameters, CancellationToken token)
    {
        connection.Execute(new CommandDefinition(
            "DELETE FROM item_extra_tag_links WHERE extra_id IN (SELECT id FROM item_extras WHERE " + condition + ");",
            parameters,
            transaction,
            cancellationToken: token));
        return connection.Execute(new CommandDefinition(
            "DELETE FROM item_extras WHERE " + condition + ";",
            parameters,
            transaction,
            cancellationToken: token));
    }

    // ── Reading ─────────────────────────────────────────────────────────────────

    private static List<ItemExtra> ToModels(SqliteConnection connection, List<Row> rows, CancellationToken ct)
    {
        var tags = new Dictionary<Guid, List<string>>();
        foreach (var chunk in rows.Select(row => row.Id).Chunk(TagQueryChunk))
        {
            foreach (var link in connection.Query<TagRow>(new CommandDefinition("""
                SELECT l.extra_id AS ExtraId, t.name AS Name
                FROM item_extra_tag_links l
                JOIN tags t ON t.id = l.tag_id
                WHERE l.extra_id IN @ids
                ORDER BY t.name COLLATE NOCASE;
                """,
                new { ids = chunk.Select(GuidSql.ToBlob).ToArray() },
                cancellationToken: ct)))
            {
                if (!tags.TryGetValue(link.ExtraId, out var list))
                {
                    tags[link.ExtraId] = list = [];
                }

                list.Add(link.Name);
            }
        }

        return [.. rows.Select(row => ToModel(row, tags.TryGetValue(row.Id, out var found) ? found : []))];
    }

    private static ItemExtra ToModel(Row row, IReadOnlyList<string> tags) => new(
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
        ParseTime(row.FoundAt) ?? DateTimeOffset.MinValue,
        row.TargetSeason,
        row.TargetEpisode,
        row.ExtraKind,
        row.PlaceSeason,
        row.PlaceEpisode,
        row.PlaceTrack,
        row.OwnTitle,
        row.OwnDescription,
        row.Role,
        row.InferredRole,
        row.GroupId,
        row.GroupName,
        row.GroupSource,
        row.InferredGroup,
        ExtraScope.ParseOrNull(row.Scope) ?? FromPlace(row),
        ExtraScope.ParseOrNull(row.InferredScope) ?? ExtraScope.Root,
        ExtraScope.ParseOrNull(row.Alongside),
        ExtraScope.ParseOrNull(row.InferredAlongside),
        ParseTime(row.MissingSince),
        tags);

    /// <summary>The place a person gave in the v4 "What is this file?" picker, read as the scope it amounts to.</summary>
    private static ExtraScope? FromPlace(Row row)
    {
        if (row.PlaceTrack is > 0)
        {
            return ExtraScope.ForTrack(row.PlaceTrack.Value);
        }

        if (row.PlaceSeason is null)
        {
            return null;
        }

        return row.PlaceEpisode is > 0
            ? ExtraScope.ForEpisode(row.PlaceSeason.Value, row.PlaceEpisode.Value)
            : ExtraScope.ForSeason(row.PlaceSeason.Value);
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string FormatTime(DateTimeOffset time) => time.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;

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
        public string? ExtraKind { get; init; }
        public int? PlaceSeason { get; init; }
        public int? PlaceEpisode { get; init; }
        public int? PlaceTrack { get; init; }
        public string? FoundAt { get; init; }
        public string? OwnTitle { get; init; }
        public string? OwnDescription { get; init; }
        public string? Role { get; init; }
        public string? InferredRole { get; init; }
        public Guid? GroupId { get; init; }
        public string? GroupName { get; init; }
        public string? GroupSource { get; init; }
        public string? InferredGroup { get; init; }
        public string? Scope { get; init; }
        public string? InferredScope { get; init; }
        public string? Alongside { get; init; }
        public string? InferredAlongside { get; init; }
        public string? MissingSince { get; init; }
    }

    private sealed class GroupRow
    {
        public Guid Id { get; init; }
        public string ItemFolder { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public int SortOrder { get; init; }
        public int FileCount { get; init; }
    }

    private sealed class TagRow
    {
        public Guid ExtraId { get; init; }
        public string Name { get; init; } = string.Empty;
    }
}
