using System.Data;
using System.Text.Json;
using Dapper;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Api.Services.Vault;

/// <summary>What the scrub of a backup copy kept and dropped, for the manifest.</summary>
internal sealed record VaultBackupScrubResult(int VaultLibraries, int BookmarksKept, int BookmarkPacksDropped);

/// <summary>
/// Removes the Vault catalogue from a backup COPY (never from the live data store). A backup keeps the configuration and the personal state
/// of the Vault: which Vault libraries exist and their settings, NSFW settings and PIN hashes, access grants, view preferences and each
/// person's bookmarks, the last kept only as "how to recognise the title" so they can be re-linked after a rescan.
/// Everything that names or describes a Vault video, image, gallery, performer, studio or tag is dropped: the files and editions and works of
/// the Vault libraries, their metadata, artwork, search entries, fingerprints, matches, activity, playback records and any row that
/// names a Vault folder. A rescan after a restore rebuilds all of it.
/// </summary>
internal static class VaultBackupScrubber
{
    // Never touched by the sweeps: the Vault library list itself, the registration of the Vault image libraries (no names), the kept
    // bookmarks, and the data store's own bookkeeping and setup state.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "vault_libraries", "vault_image_libraries", "vault_pending_bookmarks", "data_store_upgrades", "storage_metadata",
        "metadata_providers", "provider_config",
    };

    private static readonly string[] VaultCatalogueTables =
    [
        "vault_activity", "vault_artwork", "vault_asset_performers", "vault_asset_studios", "vault_asset_tags", "vault_field_sources",
        "vault_file_fingerprints", "vault_gallery_items", "vault_galleries", "vault_image_tags", "vault_image_items", "vault_image_folders",
        "vault_matches", "vault_performers", "vault_studios", "vault_tags",
    ];

    private static readonly string[] FullTextTables = ["search_index", "local_item_search", "artwork_asset_search"];

    public static VaultBackupScrubResult Scrub(string databaseCopyPath, string configDirectory)
    {
        DapperConfiguration.Configure();
        using var connection = SqliteBackupTarget.Create(databaseCopyPath);
        connection.ExecuteScalar("PRAGMA journal_mode = DELETE;");
        var libraries = connection.Query<string>("SELECT library_id FROM vault_libraries;").ToList();
        if (libraries.Count == 0)
        {
            return new VaultBackupScrubResult(0, 0, 0);
        }

        // The copy is a private file, so nothing else reads it while it is scrubbed. References are resolved by ResolveReferences,
        // which honours each foreign key's own delete action, so the order of the deletes does not matter.
        connection.Execute("PRAGMA foreign_keys = OFF;");
        connection.Execute("PRAGMA synchronous = OFF;");
        var roots = ReadVaultFolders(configDirectory, libraries);
        CollectIds(connection, roots);
        var packs = connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM bookmark_packs WHERE asset_id IN (SELECT id FROM _scrub_assets);");
        var kept = KeepBookmarks(connection);
        DeleteCatalogue(connection);
        SweepIds(connection);
        SweepFolders(connection);
        RemoveVaultArtwork(connection);
        ResolveReferences(connection);

        foreach (var table in FullTextTables)
        {
            connection.Execute($"INSERT INTO {table}({table}) VALUES ('optimize');");
        }

        connection.Execute("VACUUM;");
        return new VaultBackupScrubResult(libraries.Count, kept, packs);
    }

    private static void CollectIds(SqliteConnection connection, IReadOnlyList<string> roots)
    {
        connection.Execute(
            """
            CREATE TEMP TABLE _scrub_assets (id BLOB NOT NULL PRIMARY KEY);
            CREATE TEMP TABLE _scrub_ids (id BLOB NOT NULL PRIMARY KEY);
            CREATE TEMP TABLE _scrub_text (id TEXT NOT NULL PRIMARY KEY);
            CREATE TEMP TABLE _scrub_artwork (id BLOB NOT NULL PRIMARY KEY);
            CREATE TEMP TABLE _scrub_roots (root TEXT NOT NULL PRIMARY KEY);

            INSERT OR IGNORE INTO _scrub_assets
                SELECT ma.id FROM media_assets ma JOIN vault_libraries vl ON vl.library_id = ma.library_id COLLATE NOCASE;
            INSERT OR IGNORE INTO _scrub_assets
                SELECT id FROM media_assets WHERE derived_from_asset_id IN (SELECT id FROM _scrub_assets);

            INSERT OR IGNORE INTO _scrub_ids SELECT id FROM _scrub_assets;
            INSERT OR IGNORE INTO _scrub_ids
                SELECT edition_id FROM media_assets WHERE id IN (SELECT id FROM _scrub_assets);
            INSERT OR IGNORE INTO _scrub_ids
                SELECT work_id FROM editions WHERE id IN (SELECT id FROM _scrub_ids);

            INSERT OR IGNORE INTO _scrub_ids
                SELECT li.id FROM local_items li JOIN vault_image_libraries vil ON vil.library_id = li.library_id;
            INSERT OR IGNORE INTO _scrub_ids SELECT id FROM vault_performers;
            INSERT OR IGNORE INTO _scrub_ids SELECT id FROM vault_studios;
            INSERT OR IGNORE INTO _scrub_ids SELECT id FROM vault_tags;
            INSERT OR IGNORE INTO _scrub_ids SELECT id FROM vault_galleries;
            INSERT OR IGNORE INTO _scrub_ids SELECT id FROM vault_image_folders;
            """);

        // A show or album that only the Vault videos hang from goes with them; one that also holds an ordinary child stays.
        int added;
        do
        {
            added = connection.Execute(
                """
                INSERT OR IGNORE INTO _scrub_ids
                    SELECT p.id FROM works p
                    WHERE p.id IN (SELECT parent_work_id FROM works WHERE id IN (SELECT id FROM _scrub_ids) AND parent_work_id IS NOT NULL)
                      AND NOT EXISTS (SELECT 1 FROM works c WHERE c.parent_work_id = p.id AND c.id NOT IN (SELECT id FROM _scrub_ids));
                """);
        }
        while (added > 0);

        connection.Execute(
            """
            INSERT OR IGNORE INTO _scrub_text SELECT lower(hex(id)) FROM _scrub_ids;
            INSERT OR IGNORE INTO _scrub_artwork
                SELECT artwork_asset_id FROM artwork_asset_context WHERE entity_id IN (SELECT id FROM _scrub_ids);
            """);
        foreach (var root in roots)
        {
            connection.Execute("INSERT OR IGNORE INTO _scrub_roots(root) VALUES (@root);", new { root });
        }
    }

    /// <summary>Keeps each person's Vault bookmarks as "how to recognise the title" plus the bookmark, then lets the sweeps drop the rest.</summary>
    private static int KeepBookmarks(SqliteConnection connection)
    {
        var bookmarks = connection.Query<BookmarkRow>(
            """
            SELECT profile_id AS ProfileId, asset_id AS AssetId, is_global AS IsGlobal, chapter_index AS ChapterIndex,
                   chapter_title AS ChapterTitle, position_seconds AS PositionSeconds, duration_seconds AS DurationSeconds,
                   end_seconds AS EndSeconds, label AS Label, note AS Note, description AS Description, created_at AS CreatedAt
            FROM audiobook_bookmarks
            WHERE asset_id IN (SELECT id FROM _scrub_assets);
            """).ToList();
        if (bookmarks.Count == 0)
        {
            return 0;
        }

        var videos = VaultRelinkIdentityReader.Read(connection, bookmarks.Select(row => row.AssetId).Distinct().ToList());
        var kept = 0;
        foreach (var bookmark in bookmarks)
        {
            if (!videos.TryGetValue(bookmark.AssetId, out var video))
            {
                continue;
            }

            var identity = video.Identity.RunningTimeSeconds is null && bookmark.DurationSeconds is > 0
                ? video.Identity with { RunningTimeSeconds = bookmark.DurationSeconds }
                : video.Identity;
            double? chapterStart = string.IsNullOrWhiteSpace(bookmark.ChapterTitle)
                ? null
                : video.Chapters
                    .Where(chapter => string.Equals(chapter.Title, bookmark.ChapterTitle.Trim(), StringComparison.OrdinalIgnoreCase)
                        && chapter.StartSeconds <= bookmark.PositionSeconds)
                    .OrderByDescending(chapter => chapter.StartSeconds)
                    .Select(chapter => (double?)chapter.StartSeconds)
                    .FirstOrDefault();
            connection.Execute(
                """
                INSERT INTO vault_pending_bookmarks
                    (id, profile_id, is_global, identity_json, position_seconds, end_seconds, duration_seconds, chapter_index,
                     chapter_title, chapter_start_seconds, label, note, description, created_at)
                VALUES
                    (@id, @profileId, @isGlobal, @identity, @position, @end, @duration, @chapterIndex,
                     @chapterTitle, @chapterStart, @label, @note, @description, @createdAt);
                """,
                new
                {
                    id = Guid.NewGuid(),
                    profileId = bookmark.ProfileId,
                    isGlobal = bookmark.IsGlobal,
                    identity = VaultRelinkIdentityReader.Serialize(identity),
                    position = bookmark.PositionSeconds,
                    end = bookmark.EndSeconds,
                    duration = bookmark.DurationSeconds,
                    chapterIndex = bookmark.ChapterIndex,
                    chapterTitle = bookmark.ChapterTitle,
                    chapterStart,
                    label = bookmark.Label,
                    note = bookmark.Note,
                    description = bookmark.Description,
                    createdAt = bookmark.CreatedAt,
                });
            kept++;
        }

        return kept;
    }

    private static void DeleteCatalogue(SqliteConnection connection)
    {
        // Full-text entries first: they are keyed through rows the deletes below remove.
        connection.Execute(
            """
            DELETE FROM local_item_search
             WHERE rowid IN (SELECT rowid FROM local_item_search_keys WHERE item_id IN (SELECT id FROM _scrub_ids));
            DELETE FROM search_index WHERE entity_id IN (SELECT id FROM _scrub_ids);
            """);
        foreach (var table in VaultCatalogueTables)
        {
            connection.Execute($"DELETE FROM {table};");
        }

        connection.Execute(
            """
            DELETE FROM works WHERE id IN (SELECT id FROM _scrub_ids);
            DELETE FROM editions WHERE id IN (SELECT id FROM _scrub_ids);
            DELETE FROM media_assets WHERE id IN (SELECT id FROM _scrub_ids);
            DELETE FROM local_items WHERE id IN (SELECT id FROM _scrub_ids);
            DELETE FROM nsfw_session_unlocks;
            """);
    }

    /// <summary>Drops every row anywhere that points at a removed id (a blob column, or a text column named like an id).</summary>
    private static void SweepIds(SqliteConnection connection)
    {
        foreach (var table in SweepableTables(connection))
        {
            foreach (var column in Columns(connection, table))
            {
                if (string.Equals(column.Type, "BLOB", StringComparison.OrdinalIgnoreCase))
                {
                    connection.Execute(
                        $"DELETE FROM {Quote(table)} WHERE {Quote(column.Name)} IN (SELECT id FROM _scrub_ids);");
                }
                else if (IsText(column.Type) && column.Name.EndsWith("id", StringComparison.OrdinalIgnoreCase))
                {
                    connection.Execute(
                        $"DELETE FROM {Quote(table)} WHERE lower(replace({Quote(column.Name)}, '-', '')) IN (SELECT id FROM _scrub_text);");
                }
            }
        }
    }

    /// <summary>Drops every row that names a Vault folder, whatever the slashes, the case or the escaping.</summary>
    private static void SweepFolders(SqliteConnection connection)
    {
        if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM _scrub_roots;") == 0)
        {
            return;
        }

        foreach (var table in SweepableTables(connection))
        {
            foreach (var column in Columns(connection, table).Where(column => IsText(column.Type)))
            {
                var text = $"lower(replace(replace({Quote(column.Name)}, char(92), '/'), '//', '/'))";
                connection.Execute(
                    $"DELETE FROM {Quote(table)} WHERE EXISTS (SELECT 1 FROM _scrub_roots r WHERE instr({text}, r.root || '/') > 0 OR {text} = r.root);");
            }
        }
    }

    private static void RemoveVaultArtwork(SqliteConnection connection) => connection.Execute(
        """
        DELETE FROM artwork_assets
         WHERE id IN (SELECT id FROM _scrub_artwork)
           AND NOT EXISTS (SELECT 1 FROM artwork_asset_context c WHERE c.artwork_asset_id = artwork_assets.id);
        """);

    /// <summary>
    /// Finishes what the foreign keys would have done: a row left pointing at a removed row is deleted (cascade, or no action allowed)
    /// or has the reference cleared (set null), until the copy is consistent. Rows lost this way lose their own children next pass.
    /// </summary>
    private static void ResolveReferences(SqliteConnection connection)
    {
        for (var pass = 0; pass < 25; pass++)
        {
            var broken = connection.Query<BrokenRow>(
                "SELECT \"table\" AS TableName, rowid AS RowId, fkid AS ForeignKeyId FROM pragma_foreign_key_check;").ToList();
            if (broken.Count == 0)
            {
                return;
            }

            foreach (var group in broken.Where(row => row.RowId is not null).GroupBy(row => (row.TableName, row.ForeignKeyId)))
            {
                var references = connection.Query<ReferenceRow>(
                    $"SELECT \"from\" AS FromColumn, on_delete AS OnDelete FROM pragma_foreign_key_list({Quote(group.Key.TableName, '\'')}) WHERE id = @id;",
                    new { id = group.Key.ForeignKeyId }).ToList();
                var clear = references.Count > 0 && references.All(row => string.Equals(row.OnDelete, "SET NULL", StringComparison.OrdinalIgnoreCase));
                foreach (var chunk in group.Select(row => row.RowId!.Value).Chunk(500))
                {
                    var rowIds = string.Join(',', chunk);
                    var statement = clear
                        ? $"UPDATE {Quote(group.Key.TableName)} SET {string.Join(", ", references.Select(row => Quote(row.FromColumn) + " = NULL"))} WHERE rowid IN ({rowIds});"
                        : $"DELETE FROM {Quote(group.Key.TableName)} WHERE rowid IN ({rowIds});";
                    connection.Execute(statement);
                }
            }
        }

        throw new InvalidOperationException("The backup copy could not be made consistent after the Vault catalogue was removed.");
    }

    private static List<string> SweepableTables(SqliteConnection connection)
    {
        var all = connection.Query<MasterRow>(
            "SELECT name AS Name, sql AS Sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\';").ToList();
        var virtualTables = all.Where(row => row.Sql?.StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase) == true)
            .Select(row => row.Name).ToList();
        return all.Select(row => row.Name)
            .Where(name => !Protected.Contains(name)
                && !virtualTables.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !virtualTables.Any(virtualName => name.StartsWith(virtualName + "_", StringComparison.OrdinalIgnoreCase)
                    && name[(virtualName.Length + 1)..] is "data" or "idx" or "content" or "docsize" or "config")
                && !name.StartsWith("onboarding_", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>The named columns of a table and their declared types, read from an empty result so hidden columns are left out.</summary>
    private static List<ColumnRow> Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {Quote(table)} LIMIT 0;";
        using var reader = command.ExecuteReader();
        var columns = new List<ColumnRow>();
        for (var index = 0; index < reader.FieldCount; index++)
        {
            columns.Add(new ColumnRow { Name = reader.GetName(index), Type = reader.GetDataTypeName(index) });
        }

        return columns;
    }

    private static bool IsText(string type) =>
        type.Length == 0 || type.Contains("TEXT", StringComparison.OrdinalIgnoreCase)
        || type.Contains("CHAR", StringComparison.OrdinalIgnoreCase) || type.Contains("CLOB", StringComparison.OrdinalIgnoreCase);

    private static string Quote(string name, char quote = '"') =>
        quote + name.Replace(quote.ToString(), new string(quote, 2), StringComparison.Ordinal) + quote;

    /// <summary>The folders of every Vault library, as <c>libraries.json</c> names them, lower-cased with forward slashes.</summary>
    private static List<string> ReadVaultFolders(string configDirectory, IReadOnlyList<string> vaultLibraryIds)
    {
        var roots = new List<string>();
        var file = Path.Combine(configDirectory, "libraries.json");
        if (!File.Exists(file))
        {
            return roots;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(file));
        if (!document.RootElement.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Array)
        {
            return roots;
        }

        foreach (var library in libraries.EnumerateArray())
        {
            var id = library.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            if (id is null || !vaultLibraryIds.Contains(id, StringComparer.OrdinalIgnoreCase)
                || !library.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var source in sources.EnumerateArray())
            {
                if (source.TryGetProperty("path", out var path) && path.GetString() is { Length: > 0 } text)
                {
                    var normalized = text.Trim().Replace('\\', '/').ToLowerInvariant();
                    while (normalized.Contains("//", StringComparison.Ordinal))
                    {
                        normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
                    }

                    normalized = normalized.TrimEnd('/');
                    if (normalized.Length > 0)
                    {
                        roots.Add(normalized);
                    }
                }
            }
        }

        return roots.Distinct(StringComparer.Ordinal).ToList();
    }

    private sealed class BrokenRow
    {
        public string TableName { get; set; } = string.Empty;

        public long? RowId { get; set; }

        public long ForeignKeyId { get; set; }
    }

    private sealed class ReferenceRow
    {
        public string FromColumn { get; set; } = string.Empty;

        public string OnDelete { get; set; } = string.Empty;
    }

    private sealed class MasterRow
    {
        public string Name { get; set; } = string.Empty;

        public string? Sql { get; set; }
    }

    private sealed class ColumnRow
    {
        public string Name { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;
    }

    private sealed class BookmarkRow
    {
        public Guid ProfileId { get; set; }

        public Guid AssetId { get; set; }

        public int IsGlobal { get; set; }

        public int? ChapterIndex { get; set; }

        public string? ChapterTitle { get; set; }

        public double PositionSeconds { get; set; }

        public double? DurationSeconds { get; set; }

        public double? EndSeconds { get; set; }

        public string? Label { get; set; }

        public string? Note { get; set; }

        public string? Description { get; set; }

        public string CreatedAt { get; set; } = string.Empty;
    }
}
