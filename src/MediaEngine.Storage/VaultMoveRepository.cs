using System.Globalization;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite implementation of <see cref="IVaultMoveRepository"/>: the Vault's own list of Move to Vault and Move out jobs.</summary>
public sealed class VaultMoveRepository(IDatabaseConnection database) : IVaultMoveRepository
{
    private const string JobSelect = """
        SELECT id AS Id, direction AS Direction, kind_key AS KindKey, source_library_id AS SourceLibraryId,
               destination_library_id AS DestinationLibraryId, source_folder AS SourceFolder, destination_folder AS DestinationFolder,
               remove_read_only_originals AS RemoveReadOnlyOriginals, state AS State, message AS Message,
               created_by_profile_id AS CreatedByProfileId, created_at AS CreatedAt, updated_at AS UpdatedAt
        FROM vault_moves
        """;

    private const string ItemSelect = """
        SELECT id AS Id, move_id AS MoveId, position AS Position, work_id AS WorkId, asset_id AS AssetId, new_asset_id AS NewAssetId,
               source_path AS SourcePath, final_name AS FinalName, size_bytes AS SizeBytes, content_hash AS ContentHash,
               files_json AS FilesJson, snapshot_json AS SnapshotJson, original_left AS OriginalLeft, state AS State,
               reason AS Reason, updated_at AS UpdatedAt
        FROM vault_move_items
        """;

    public Task<IReadOnlyList<VaultMoveCandidate>> FindCandidatesAsync(string libraryId, string folderPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var folder = Normalize(folderPath);
        if (folder.Length == 0 || string.IsNullOrWhiteSpace(libraryId))
        {
            return Task.FromResult<IReadOnlyList<VaultMoveCandidate>>([]);
        }

        using var connection = database.CreateConnection();
        // One row per media file under the folder, with the title it belongs to and how many media files that title has in all (a title
        // with files elsewhere is not moved, so nothing it still needs is taken away from it).
        var rows = connection.Query<CandidateRow>(
            """
            SELECT w.id AS WorkId, ma.id AS AssetId, ma.file_path_root AS FilePath, ma.content_hash AS ContentHash,
                   (SELECT COUNT(*) FROM media_assets other WHERE other.edition_id = ma.edition_id) AS FilesInTitle
            FROM media_assets ma
            LEFT JOIN editions e ON e.id = ma.edition_id
            LEFT JOIN works w ON w.id = e.work_id
            WHERE ma.library_id = @libraryId COLLATE NOCASE
              AND REPLACE(ma.file_path_root, '\', '/') LIKE @under ESCAPE '\'
            ORDER BY ma.file_path_root;
            """,
            new { libraryId, under = EscapeLike(folder) + "/%" });
        return Task.FromResult<IReadOnlyList<VaultMoveCandidate>>(
            rows.Select(row => new VaultMoveCandidate(row.WorkId, row.AssetId, row.FilePath, row.ContentHash, row.FilesInTitle)).ToList());
    }

    public Task<(Guid AssetId, Guid? WorkId)?> FindByHashAsync(string libraryId, string contentHash, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<HashRow>(
            """
            SELECT ma.id AS AssetId, e.work_id AS WorkId
            FROM media_assets ma
            LEFT JOIN editions e ON e.id = ma.edition_id
            WHERE ma.library_id = @libraryId COLLATE NOCASE AND ma.content_hash = @contentHash
            ORDER BY ma.id
            LIMIT 1;
            """,
            new { libraryId, contentHash });
        return Task.FromResult<(Guid AssetId, Guid? WorkId)?>(row is null ? null : (row.AssetId, row.WorkId));
    }

    public Task<bool> HasOpenJobForFolderAsync(string folderPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var folder = Normalize(folderPath);
        using var connection = database.CreateConnection();
        var found = connection.ExecuteScalar<int>(
            """
            SELECT EXISTS(
                SELECT 1 FROM vault_moves
                WHERE state IN ('queued', 'running')
                  AND (REPLACE(source_folder, '\', '/') = @folder COLLATE NOCASE OR REPLACE(destination_folder, '\', '/') = @folder COLLATE NOCASE));
            """,
            new { folder });
        return Task.FromResult(found == 1);
    }

    public Task CreateAsync(VaultMoveJob job, IReadOnlyList<VaultMoveItem> items, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(items);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(
                """
                INSERT INTO vault_moves
                    (id, direction, kind_key, source_library_id, destination_library_id, source_folder, destination_folder,
                     remove_read_only_originals, state, message, created_by_profile_id, created_at, updated_at)
                VALUES (@Id, @Direction, @KindKey, @SourceLibraryId, @DestinationLibraryId, @SourceFolder, @DestinationFolder,
                        @RemoveReadOnlyOriginals, @State, @Message, @CreatedByProfileId, @CreatedAt, @UpdatedAt);
                """,
                new
                {
                    job.Id, job.Direction, job.KindKey, job.SourceLibraryId, job.DestinationLibraryId, job.SourceFolder, job.DestinationFolder,
                    RemoveReadOnlyOriginals = job.RemoveReadOnlyOriginals ? 1 : 0, job.State, job.Message, job.CreatedByProfileId,
                    CreatedAt = Iso(job.CreatedAt), UpdatedAt = Iso(job.UpdatedAt),
                }, transaction);
            foreach (var item in items)
            {
                connection.Execute(
                    """
                    INSERT INTO vault_move_items
                        (id, move_id, position, work_id, asset_id, new_asset_id, source_path, final_name, size_bytes, content_hash,
                         files_json, snapshot_json, original_left, state, reason, updated_at)
                    VALUES (@Id, @MoveId, @Position, @WorkId, @AssetId, @NewAssetId, @SourcePath, @FinalName, @SizeBytes, @ContentHash,
                            @FilesJson, @SnapshotJson, @OriginalLeft, @State, @Reason, @UpdatedAt);
                    """,
                    ItemParameters(item), transaction);
            }
        }, ct);
    }

    public Task<VaultMoveJob?> GetAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var row = connection.QueryFirstOrDefault<JobRow>(JobSelect + " WHERE id = @id;", new { id });
        return Task.FromResult(row is null ? null : Map(row));
    }

    public Task<IReadOnlyList<VaultMoveJob>> ListRecentAsync(int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<JobRow>(JobSelect + " ORDER BY created_at DESC, id LIMIT @limit;", new { limit = Math.Clamp(limit, 1, 200) });
        return Task.FromResult<IReadOnlyList<VaultMoveJob>>(rows.Select(Map).ToList());
    }

    public Task<IReadOnlyList<VaultMoveJob>> ListOpenAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        // A job that needs the administrator is looked at again only while some item is still unfinished (a copy waiting for the folder's scan).
        var rows = connection.Query<JobRow>(JobSelect + """
             WHERE state IN ('queued', 'running')
                OR (state = 'attention' AND EXISTS (
                        SELECT 1 FROM vault_move_items i
                        WHERE i.move_id = vault_moves.id AND i.state IN ('planned', 'copied', 'retired', 'published')))
             ORDER BY created_at, id;
            """);
        return Task.FromResult<IReadOnlyList<VaultMoveJob>>(rows.Select(Map).ToList());
    }

    public Task<IReadOnlyList<VaultMoveItem>> GetItemsAsync(Guid moveId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<ItemRow>(ItemSelect + " WHERE move_id = @moveId ORDER BY position;", new { moveId });
        return Task.FromResult<IReadOnlyList<VaultMoveItem>>(rows.Select(Map).ToList());
    }

    public Task UpdateItemAsync(VaultMoveItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(
                """
                UPDATE vault_move_items
                SET work_id = @WorkId, asset_id = @AssetId, new_asset_id = @NewAssetId, final_name = @FinalName, size_bytes = @SizeBytes,
                    content_hash = @ContentHash, files_json = @FilesJson, snapshot_json = @SnapshotJson, original_left = @OriginalLeft,
                    state = @State, reason = @Reason, updated_at = @UpdatedAt
                WHERE id = @Id;
                """,
                ItemParameters(item), transaction);
        }, ct);
    }

    public Task UpdateJobAsync(Guid id, string state, string? message, DateTimeOffset now, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute(
                "UPDATE vault_moves SET state = @state, message = @message, updated_at = @updated WHERE id = @id;",
                new { id, state, message, updated = Iso(now) }, transaction);
        }, ct);

    private static object ItemParameters(VaultMoveItem item) => new
    {
        item.Id, item.MoveId, item.Position, item.WorkId, item.AssetId, item.NewAssetId, item.SourcePath, item.FinalName, item.SizeBytes,
        item.ContentHash, item.FilesJson, item.SnapshotJson, OriginalLeft = item.OriginalLeft ? 1 : 0, item.State, item.Reason,
        UpdatedAt = Iso(item.UpdatedAt),
    };

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static VaultMoveJob Map(JobRow row) => new(
        row.Id, row.Direction, row.KindKey, row.SourceLibraryId, row.DestinationLibraryId, row.SourceFolder, row.DestinationFolder,
        row.RemoveReadOnlyOriginals != 0, row.State, row.Message, row.CreatedByProfileId, Parse(row.CreatedAt), Parse(row.UpdatedAt));

    private static VaultMoveItem Map(ItemRow row) => new(
        row.Id, row.MoveId, row.Position, row.WorkId, row.AssetId, row.NewAssetId, row.SourcePath, row.FinalName, row.SizeBytes,
        row.ContentHash, row.FilesJson, row.SnapshotJson, row.OriginalLeft != 0, row.State, row.Reason, Parse(row.UpdatedAt));

    private sealed class CandidateRow
    {
        public Guid? WorkId { get; set; }
        public Guid AssetId { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string? ContentHash { get; set; }
        public int FilesInTitle { get; set; }
    }

    private sealed class HashRow
    {
        public Guid AssetId { get; set; }
        public Guid? WorkId { get; set; }
    }

    private sealed class JobRow
    {
        public Guid Id { get; set; }
        public string Direction { get; set; } = string.Empty;
        public string KindKey { get; set; } = string.Empty;
        public string SourceLibraryId { get; set; } = string.Empty;
        public string DestinationLibraryId { get; set; } = string.Empty;
        public string SourceFolder { get; set; } = string.Empty;
        public string DestinationFolder { get; set; } = string.Empty;
        public long RemoveReadOnlyOriginals { get; set; }
        public string State { get; set; } = string.Empty;
        public string? Message { get; set; }
        public Guid? CreatedByProfileId { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
    }

    private sealed class ItemRow
    {
        public Guid Id { get; set; }
        public Guid MoveId { get; set; }
        public int Position { get; set; }
        public Guid? WorkId { get; set; }
        public Guid? AssetId { get; set; }
        public Guid? NewAssetId { get; set; }
        public string SourcePath { get; set; } = string.Empty;
        public string FinalName { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string? ContentHash { get; set; }
        public string? FilesJson { get; set; }
        public string? SnapshotJson { get; set; }
        public long OriginalLeft { get; set; }
        public string State { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public string UpdatedAt { get; set; } = string.Empty;
    }
}
