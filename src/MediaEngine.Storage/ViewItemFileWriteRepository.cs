using Dapper;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// What the View file writer needs to know about an item's file (batched write-back D7 to D9): where its primary file is, which folder
/// and library it sits in, who added it, and the fingerprint Library recorded for it. After Library writes into the file it records
/// the new fingerprint here (D8), so the file is not taken for a new or changed one.
/// </summary>
public sealed class ViewItemFileWriteRepository(IDatabaseConnection database)
{
    /// <summary>
    /// SQL for the number of items in a folder whose file was left as it is because its kind cannot hold the profile name. The column
    /// to compare with is the folder's <c>view_sources.id</c>.
    /// </summary>
    public const string KeptInLibraryCountSql = """
        (SELECT COUNT(DISTINCT w.local_item_id)
           FROM media_file_write_intents w
           JOIN local_items kli ON kli.id = w.local_item_id
           JOIN local_item_files klif ON klif.item_id = kli.id AND klif.role = 'primary'
           JOIN local_file_sources klfs ON klfs.file_id = klif.file_id AND klfs.library_id = kli.library_id
          WHERE w.target_kind = 'view_item' AND w.status = 'unsupported' AND klfs.source_id = view_sources.id)
        """;

    /// <summary>The item's primary file in the item's own library, or null when the item does not exist.</summary>
    public Task<ViewItemWriteTarget?> GetTargetAsync(Guid localItemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return Task.FromResult(connection.QueryFirstOrDefault<ViewItemWriteTarget>(new CommandDefinition("""
            SELECT li.id AS ItemId, li.scope_kind AS ScopeKind, li.library_id AS LibraryId,
                   li.personal_space_id AS PersonalSpaceId, li.added_by_profile_id AS AddedByProfileId,
                   lf.id AS FileId, lf.content_hash AS ContentHash, lf.byte_size AS ByteSize,
                   lfs.file_path AS FilePath, lfs.modified_at AS ModifiedAt,
                   lfs.source_id AS SourceId, vs.storage_mode AS StorageMode,
                   (SELECT COUNT(*) FROM local_file_sources other WHERE other.file_id = lf.id) AS SourceCount
              FROM local_items li
              LEFT JOIN local_item_files lif ON lif.item_id = li.id AND lif.role = 'primary'
              LEFT JOIN local_files lf ON lf.id = lif.file_id
              LEFT JOIN local_file_sources lfs ON lfs.file_id = lf.id AND lfs.library_id = li.library_id
              LEFT JOIN view_sources vs ON vs.id = lfs.source_id
             WHERE li.id = @localItemId
             ORDER BY lfs.indexed_at DESC
             LIMIT 1;
            """, new { localItemId }, cancellationToken: ct)));
    }

    /// <summary>
    /// Records the file as Library left it: its new fingerprint and size, and the new modified time of the path. Returns false when
    /// another file record already holds that fingerprint (the file is now byte for byte a file Library already knows): the record keeps
    /// its old fingerprint, and the next look at the folder joins the path to the other record through the ordinary duplicate handling.
    /// The modified time is always updated, so the file is not read again as a changed one.
    /// </summary>
    public Task<bool> RecordOwnWriteAsync(
        Guid fileId, Guid libraryId, string filePath, string contentHash, long byteSize, DateTimeOffset modifiedAt,
        CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            var hash = contentHash.Trim().ToLowerInvariant();
            connection.Execute("""
                UPDATE local_file_sources SET modified_at = @modifiedAt, indexed_at = @now
                 WHERE library_id = @libraryId AND file_path = @filePath COLLATE NOCASE;
                """, new { libraryId, filePath, modifiedAt, now = DateTimeOffset.UtcNow }, transaction);

            var taken = connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM local_files WHERE content_hash = @hash COLLATE NOCASE AND id <> @fileId;",
                new { hash, fileId }, transaction) > 0;
            if (taken)
            {
                return false;
            }

            connection.Execute(
                "UPDATE local_files SET content_hash = @hash, byte_size = @byteSize WHERE id = @fileId;",
                new { hash, byteSize, fileId }, transaction);
            return true;
        }, ct);
}

/// <summary>An item's primary file as the View writer sees it. The file fields are null when the item has no file in its own library.</summary>
public sealed record ViewItemWriteTarget
{
    public Guid ItemId { get; init; }

    /// <summary><c>personal</c> or <c>shared</c>.</summary>
    public string ScopeKind { get; init; } = "";

    public Guid LibraryId { get; init; }
    public Guid? PersonalSpaceId { get; init; }
    public Guid? AddedByProfileId { get; init; }
    public Guid? FileId { get; init; }

    /// <summary>The fingerprint (SHA-256, lower-case hex) Library recorded for the file.</summary>
    public string? ContentHash { get; init; }

    public long ByteSize { get; init; }

    /// <summary>Where the file is, as indexed (an absolute path).</summary>
    public string? FilePath { get; init; }

    /// <summary>The file's modified time when Library last saw it.</summary>
    public DateTimeOffset? ModifiedAt { get; init; }

    public Guid? SourceId { get; init; }

    /// <summary><c>managed</c> or <c>linked</c>; null when the file is in no folder Library knows.</summary>
    public string? StorageMode { get; init; }

    /// <summary>How many places Library knows this one file record at (a copy in the shared library is a second place).</summary>
    public int SourceCount { get; init; }
}
