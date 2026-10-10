using System.Data;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>SQLite access for removing a person's personal photos and videos.</summary>
public sealed class ProfilePersonalMediaRepository(IDatabaseConnection database) : IProfilePersonalMediaRepository
{
    private const string PersonalItems =
        "SELECT id FROM local_items WHERE scope_kind='personal' AND owner_profile_id=@profileId";

    private sealed class FileRow
    {
        public Guid FileId { get; init; }
        public string FilePath { get; init; } = string.Empty;
        public long IsManaged { get; init; }
    }

    public Task<IReadOnlyList<Guid>> GetPersonalItemIdsAsync(Guid profileId, CancellationToken ct = default) =>
        database.ExecuteReadAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return (IReadOnlyList<Guid>)connection.Query<Guid>(
                PersonalItems + " AND trashed_at IS NULL ORDER BY created_at, id;",
                new { profileId }, transaction).ToList();
        }, ct);

    public Task AddTagAsync(Guid profileId, string tag, DateTimeOffset addedAt, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            connection.Execute($"""
                INSERT OR IGNORE INTO local_item_tags (item_id, tag, added_at)
                SELECT id, @tag, @addedAt FROM ({PersonalItems} AND trashed_at IS NULL);
                """, new { profileId, tag, addedAt }, transaction);
        }, ct);

    public Task<IReadOnlyList<ProfilePersonalFile>> GetPersonalFilesAsync(Guid profileId, CancellationToken ct = default) =>
        database.ExecuteReadAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();
            return (IReadOnlyList<ProfilePersonalFile>)connection.Query<FileRow>("""
                SELECT DISTINCT lf.id AS FileId, lfs.file_path AS FilePath,
                       CASE WHEN COALESCE(vs.storage_mode, 'linked') = 'managed' THEN 1 ELSE 0 END AS IsManaged
                  FROM local_items li
                  JOIN local_item_files lif ON lif.item_id = li.id
                  JOIN local_files lf ON lf.id = lif.file_id
                  JOIN local_file_sources lfs ON lfs.file_id = lf.id AND lfs.library_id = li.library_id
                  LEFT JOIN view_sources vs ON vs.id = lfs.source_id
                 WHERE li.scope_kind = 'personal' AND li.owner_profile_id = @profileId;
                """, new { profileId }, transaction)
                .Select(row => new ProfilePersonalFile(row.FileId, row.FilePath, row.IsManaged != 0))
                .ToList();
        }, ct);

    /// <summary>
    /// Lets go of everything that points at the person's personal photos, so their profile can be deleted. Runs inside
    /// the profile delete's own transaction, so it is all undone if the delete does not go through.
    /// </summary>
    internal static void ReleaseForRemoval(IDbConnection connection, IDbTransaction transaction, Guid profileId) =>
        connection.Execute($"""
            DELETE FROM view_shared_transfers WHERE item_id IN ({PersonalItems});
            DELETE FROM view_shared_contributions
             WHERE submitted_by_profile_id = @profileId AND status = 'pending';
            DELETE FROM view_shared_contribution_items WHERE item_id IN ({PersonalItems});
            DELETE FROM local_file_sources
             WHERE library_id IN (SELECT library_id FROM view_personal_spaces WHERE owner_profile_id = @profileId);
            """, new { profileId }, transaction);

    public Task DeleteUnusedFilesAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken ct = default) =>
        database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            foreach (var fileId in fileIds)
            {
                token.ThrowIfCancellationRequested();
                connection.Execute("""
                    DELETE FROM local_files
                     WHERE id = @fileId
                       AND NOT EXISTS (SELECT 1 FROM local_item_files WHERE file_id = @fileId);
                    """, new { fileId }, transaction);
            }
        }, ct);
}
