using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IVaultImageRepository"/>: the records that make a View-style item a Vault image (Videos spec D44).
/// The images themselves are read and written through <see cref="ILocalAssetRepository"/>; nothing here touches View galleries, the shared
/// tag list, People or sources.
/// </summary>
public sealed class VaultImageRepository(IDatabaseConnection database) : IVaultImageRepository
{
    public const int MaximumTagLength = 80;
    public const int MaximumTagsPerImage = 50;

    public Task<IReadOnlyList<VaultImageLibrary>> GetLibrariesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<LibraryRow>(
            "SELECT library_id AS LibraryId, vault_library_id AS VaultLibraryId FROM vault_image_libraries ORDER BY created_at, library_id;")
            .Select(row => new VaultImageLibrary(row.LibraryId, row.VaultLibraryId)).ToList();
        return Task.FromResult<IReadOnlyList<VaultImageLibrary>>(rows);
    }

    public Task<VaultImageLibrary?> EnsureLibraryAsync(string vaultLibraryId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(vaultLibraryId, out var libraryId) || libraryId == Guid.Empty)
        {
            return Task.FromResult<VaultImageLibrary?>(null);
        }

        return database.ExecuteWriteAsync<VaultImageLibrary?>((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var vaultId = connection.QuerySingleOrDefault<string>(
                "SELECT library_id FROM vault_libraries WHERE library_id = @vaultLibraryId;", new { vaultLibraryId }, transaction);
            if (vaultId is null)
            {
                return null;
            }

            // Looked up first: the guard that keeps View's libraries out fires on every insert attempt, even one that would be ignored.
            var exists = connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM vault_image_libraries WHERE library_id = @blob;", new { blob = GuidSql.ToBlob(libraryId) }, transaction) > 0;
            if (!exists)
            {
                connection.Execute("""
                    INSERT INTO vault_image_libraries (library_id, vault_library_id, created_at)
                    VALUES (@libraryId, @vaultId, @now);
                    """, new { libraryId, vaultId, now = DateTimeOffset.UtcNow }, transaction);
            }

            return new VaultImageLibrary(libraryId, vaultId);
        }, ct);
    }

    public Task<Guid> EnsureFolderAsync(Guid libraryId, string folderKey, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderKey);
        var folderName = string.IsNullOrWhiteSpace(name) ? "Images" : name.Trim();
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            var existing = connection.QuerySingleOrDefault<Guid?>(
                "SELECT id FROM vault_image_folders WHERE library_id = @libraryId AND folder_key = @folderKey;",
                new { libraryId, folderKey }, transaction);
            if (existing is { } id)
            {
                connection.Execute(
                    "UPDATE vault_image_folders SET name = @folderName, updated_at = @now WHERE id = @id AND name <> @folderName;",
                    new { id, folderName, now }, transaction);
                return id;
            }

            var created = Guid.NewGuid();
            connection.Execute("""
                INSERT INTO vault_image_folders (id, library_id, name, folder_key, created_at, updated_at)
                VALUES (@created, @libraryId, @folderName, @folderKey, @now, @now);
                """, new { created, libraryId, folderName, folderKey, now }, transaction);
            return created;
        }, ct);
    }

    public Task AssignFolderAsync(IReadOnlyCollection<Guid> itemIds, Guid folderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Count == 0)
        {
            return Task.CompletedTask;
        }

        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            foreach (var itemId in itemIds.Distinct())
            {
                token.ThrowIfCancellationRequested();
                connection.Execute(
                    "UPDATE vault_image_items SET folder_id = @folderId WHERE item_id = @itemId AND folder_id IS NOT @folderId;",
                    new { itemId, folderId }, transaction);
            }
        }, ct);
    }

    public Task<IReadOnlyList<VaultImageFolder>> ListFoldersAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<FolderRow>("""
            SELECT f.id AS Id, f.library_id AS LibraryId, f.name AS Name, COUNT(li.id) AS ImageCount,
                   (SELECT c.id FROM vault_image_items cv JOIN local_items c ON c.id = cv.item_id
                     WHERE cv.folder_id = f.id AND c.trashed_at IS NULL AND c.archived_at IS NULL
                     ORDER BY COALESCE(c.captured_at, c.created_at) DESC, c.id DESC LIMIT 1) AS CoverItemId
              FROM vault_image_folders f
              JOIN vault_image_items vi ON vi.folder_id = f.id
              JOIN local_items li ON li.id = vi.item_id AND li.trashed_at IS NULL AND li.archived_at IS NULL
             GROUP BY f.id
            HAVING COUNT(li.id) > 0
             ORDER BY f.name COLLATE NOCASE, f.id;
            """).Select(Map).ToList();
        return Task.FromResult<IReadOnlyList<VaultImageFolder>>(rows);
    }

    public Task<VaultImageFolder?> GetFolderAsync(Guid folderId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<FolderRow>("""
            SELECT f.id AS Id, f.library_id AS LibraryId, f.name AS Name,
                   (SELECT COUNT(*) FROM vault_image_items vi JOIN local_items li ON li.id = vi.item_id
                     WHERE vi.folder_id = f.id AND li.trashed_at IS NULL AND li.archived_at IS NULL) AS ImageCount,
                   NULL AS CoverItemId
              FROM vault_image_folders f WHERE f.id = @folderId;
            """, new { folderId });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<IReadOnlyDictionary<Guid, VaultImageFacts>> GetFactsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var facts = new Dictionary<Guid, VaultImageFacts>();
        using var connection = database.CreateConnection();
        foreach (var chunk in itemIds.Where(id => id != Guid.Empty).Distinct().Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var row in connection.Query<FactsRow>(new CommandDefinition("""
                SELECT item_id AS ItemId, library_id AS LibraryId, folder_id AS FolderId, recycle_entries AS RecycleEntries
                  FROM vault_image_items WHERE item_id IN @ids;
                """, new { ids = chunk.Select(GuidSql.ToBlob).ToArray() }, cancellationToken: ct)))
            {
                facts[row.ItemId] = ToFacts(row);
            }
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, VaultImageFacts>>(facts);
    }

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<VaultImageTag>>> GetTagsAsync(
        IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var tags = new Dictionary<Guid, List<VaultImageTag>>();
        using var connection = database.CreateConnection();
        foreach (var chunk in itemIds.Where(id => id != Guid.Empty).Distinct().Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var row in connection.Query<ItemTagRow>(new CommandDefinition("""
                SELECT vit.item_id AS ItemId, vt.id AS TagId, vt.name AS Name
                  FROM vault_image_tags vit JOIN vault_tags vt ON vt.id = vit.tag_id
                 WHERE vit.item_id IN @ids
                 ORDER BY vt.name COLLATE NOCASE;
                """, new { ids = chunk.Select(GuidSql.ToBlob).ToArray() }, cancellationToken: ct)))
            {
                if (!tags.TryGetValue(row.ItemId, out var list))
                {
                    tags[row.ItemId] = list = [];
                }

                list.Add(new VaultImageTag(row.TagId, row.Name, 0));
            }
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<VaultImageTag>>>(
            tags.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<VaultImageTag>)pair.Value));
    }

    public Task<IReadOnlyList<VaultImageTag>> ListTagsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<TagRow>("""
            SELECT vt.id AS Id, vt.name AS Name, COUNT(li.id) AS ImageCount
              FROM vault_tags vt
              JOIN vault_image_tags vit ON vit.tag_id = vt.id
              JOIN local_items li ON li.id = vit.item_id AND li.trashed_at IS NULL AND li.archived_at IS NULL
             GROUP BY vt.id
             ORDER BY COUNT(li.id) DESC, vt.name COLLATE NOCASE, vt.id;
            """).Select(row => new VaultImageTag(row.Id, row.Name, checked((int)row.ImageCount))).ToList();
        return Task.FromResult<IReadOnlyList<VaultImageTag>>(rows);
    }

    public Task<bool> ReplaceTagsAsync(Guid itemId, IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        var clean = names
            .Select(name => name?.Trim() ?? string.Empty)
            .Where(name => name.Length > 0)
            .Select(name => name.Length > MaximumTagLength ? name[..MaximumTagLength] : name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaximumTagsPerImage)
            .ToList();
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var vaultLibraryId = connection.QuerySingleOrDefault<string>("""
                SELECT vil.vault_library_id FROM vault_image_items vii
                  JOIN vault_image_libraries vil ON vil.library_id = vii.library_id
                 WHERE vii.item_id = @itemId;
                """, new { itemId }, transaction);
            if (vaultLibraryId is null)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            connection.Execute("DELETE FROM vault_image_tags WHERE item_id = @itemId;", new { itemId }, transaction);
            foreach (var name in clean)
            {
                var tagId = connection.QuerySingleOrDefault<Guid?>(
                    "SELECT id FROM vault_tags WHERE library_id = @vaultLibraryId AND name = @name COLLATE NOCASE;",
                    new { vaultLibraryId, name }, transaction);
                if (tagId is null)
                {
                    tagId = Guid.NewGuid();
                    connection.Execute("""
                        INSERT INTO vault_tags (id, library_id, name, created_at, updated_at)
                        VALUES (@tagId, @vaultLibraryId, @name, @now, @now);
                        """, new { tagId, vaultLibraryId, name, now }, transaction);
                }

                connection.Execute(
                    "INSERT OR IGNORE INTO vault_image_tags (item_id, tag_id, added_at) VALUES (@itemId, @tagId, @now);",
                    new { itemId, tagId, now }, transaction);
            }

            // A tag nobody uses any more leaves the list, unless a video of the Vault still uses it.
            connection.Execute("""
                DELETE FROM vault_tags
                 WHERE library_id = @vaultLibraryId
                   AND NOT EXISTS (SELECT 1 FROM vault_image_tags WHERE tag_id = vault_tags.id)
                   AND NOT EXISTS (SELECT 1 FROM vault_asset_tags WHERE tag_id = vault_tags.id);
                """, new { vaultLibraryId }, transaction);
            LocalAssetRepository.RebuildSearchDocument(connection, transaction, itemId);
            return true;
        }, ct);
    }

    public Task<bool> SetRecycleEntriesAsync(Guid itemId, IReadOnlyList<Guid>? entryIds, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var entries = entryIds is { Count: > 0 } ? System.Text.Json.JsonSerializer.Serialize(entryIds) : null;
            return connection.Execute(
                "UPDATE vault_image_items SET recycle_entries = @entries WHERE item_id = @itemId;",
                new { itemId, entries }, transaction) > 0;
        }, ct);

    public Task<IReadOnlyList<VaultImageFacts>> ListDeletedAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<FactsRow>("""
            SELECT vi.item_id AS ItemId, vi.library_id AS LibraryId, vi.folder_id AS FolderId, vi.recycle_entries AS RecycleEntries
              FROM vault_image_items vi JOIN local_items li ON li.id = vi.item_id
             WHERE li.trashed_at IS NOT NULL
             ORDER BY li.trashed_at DESC, vi.item_id;
            """).Select(row => ToFacts(row)).ToList();
        return Task.FromResult<IReadOnlyList<VaultImageFacts>>(rows);
    }

    public Task<int> RemoveItemsAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            var removed = 0;
            foreach (var itemId in itemIds.Distinct())
            {
                token.ThrowIfCancellationRequested();
                // Only a deleted Vault image can be removed for good. Its search words go with the item row (a trigger clears them),
                // then the places it was indexed at, then any file record nothing else uses.
                var isDeleted = connection.ExecuteScalar<long>("""
                    SELECT COUNT(*) FROM local_items li JOIN vault_image_items vi ON vi.item_id = li.id
                     WHERE li.id = @itemId AND li.trashed_at IS NOT NULL;
                    """, new { itemId }, transaction) > 0;
                if (!isDeleted)
                {
                    continue;
                }

                var fileIds = connection.Query<Guid>(
                    "SELECT DISTINCT file_id FROM local_item_files WHERE item_id = @itemId;", new { itemId }, transaction).ToList();
                var libraryId = connection.ExecuteScalar<Guid>("SELECT library_id FROM local_items WHERE id = @itemId;", new { itemId }, transaction);
                connection.Execute("DELETE FROM local_items WHERE id = @itemId;", new { itemId }, transaction);
                foreach (var fileId in fileIds)
                {
                    connection.Execute(
                        "DELETE FROM local_file_sources WHERE file_id = @fileId AND library_id = @libraryId;",
                        new { fileId, libraryId }, transaction);
                    connection.Execute("""
                        DELETE FROM local_files
                         WHERE id = @fileId
                           AND NOT EXISTS (SELECT 1 FROM local_item_files WHERE file_id = @fileId)
                           AND NOT EXISTS (SELECT 1 FROM local_file_sources WHERE file_id = @fileId);
                        """, new { fileId }, transaction);
                }

                removed++;
            }

            return removed;
        }, ct);
    }

    private static VaultImageFacts ToFacts(FactsRow row)
    {
        IReadOnlyList<Guid> entries = [];
        if (!string.IsNullOrWhiteSpace(row.RecycleEntries))
        {
            try
            {
                entries = System.Text.Json.JsonSerializer.Deserialize<List<Guid>>(row.RecycleEntries) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                // A note that cannot be read counts as no recycle bin entries; the image then reads as not in the bin.
                entries = [];
            }
        }

        return new VaultImageFacts(row.ItemId, row.LibraryId, row.FolderId, entries);
    }

    private static VaultImageFolder Map(FolderRow row) =>
        new(row.Id, row.LibraryId, row.Name, checked((int)row.ImageCount), row.CoverItemId);

    private sealed class LibraryRow
    {
        public Guid LibraryId { get; init; }
        public string VaultLibraryId { get; init; } = string.Empty;
    }

    private sealed class FolderRow
    {
        public Guid Id { get; init; }
        public Guid LibraryId { get; init; }
        public string Name { get; init; } = string.Empty;
        public long ImageCount { get; init; }
        public Guid? CoverItemId { get; init; }
    }

    private sealed class FactsRow
    {
        public Guid ItemId { get; init; }
        public Guid LibraryId { get; init; }
        public Guid? FolderId { get; init; }
        public string? RecycleEntries { get; init; }
    }

    private sealed class ItemTagRow
    {
        public Guid ItemId { get; init; }
        public Guid TagId { get; init; }
        public string Name { get; init; } = string.Empty;
    }

    private sealed class TagRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public long ImageCount { get; init; }
    }
}
