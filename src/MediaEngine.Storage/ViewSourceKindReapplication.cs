using System.Data;
using Dapper;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Storage;

/// <summary>
/// What happens to the items already indexed from a folder when an administrator changes the folder's kind (Library setup
/// D49). It runs inside the same write transaction as the folder's own update, so the folder and its items never disagree.
/// Items the new kind accepts take it; items it no longer accepts are removed from the index only (the files on disk are
/// never touched) and counted in the folder's skipped-file count, as a full scan would count them.
/// </summary>
internal static class ViewSourceKindReapplication
{
    public static void Apply(IDbConnection connection, IDbTransaction transaction, Guid sourceId, string? sourceKey,
        string contentKind, CancellationToken ct)
    {
        var items = connection.Query<ItemRow>(new CommandDefinition("""
            SELECT DISTINCT li.id AS ItemId, li.media_kind AS MediaKind
              FROM local_file_sources lfs
              JOIN local_item_files lif ON lif.file_id = lfs.file_id
              JOIN local_items li ON li.id = lif.item_id AND li.library_id = lfs.library_id
             WHERE lfs.source_id = @sourceId;
            """, new { sourceId }, transaction, cancellationToken: ct)).ToList();

        var removedFiles = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (ViewKindAcceptance.SourceAccepts(contentKind, sourceKey, item.MediaKind))
            {
                connection.Execute(new CommandDefinition(
                    "UPDATE local_items SET content_kind = @contentKind WHERE id = @ItemId AND content_kind IS NOT @contentKind;",
                    new { contentKind, item.ItemId }, transaction, cancellationToken: ct));
                continue;
            }

            // A Shared item that was filed into the Shared library is held there by its own record; it stays as it is.
            if (connection.ExecuteScalar<long>(new CommandDefinition(
                    "SELECT COUNT(*) FROM view_shared_assets WHERE item_id = @ItemId;",
                    new { item.ItemId }, transaction, cancellationToken: ct)) > 0)
            {
                continue;
            }

            removedFiles += RemoveFromIndex(connection, transaction, sourceId, item.ItemId, ct);
        }

        if (removedFiles > 0)
        {
            connection.Execute(new CommandDefinition(
                "UPDATE view_sources SET skipped_file_count = skipped_file_count + @removedFiles WHERE id = @sourceId;",
                new { removedFiles, sourceId }, transaction, cancellationToken: ct));
        }
    }

    /// <summary>
    /// Drops this folder's places for the item's files. When no other place still holds the item it is removed too (its
    /// file links, tags, notes and Gallery memberships go with it), along with file records nothing references. Returns
    /// how many of the folder's files were let go.
    /// </summary>
    private static int RemoveFromIndex(IDbConnection connection, IDbTransaction transaction, Guid sourceId, Guid itemId,
        CancellationToken ct)
    {
        var fileIds = connection.Query<Guid>(new CommandDefinition(
            "SELECT file_id FROM local_item_files WHERE item_id = @itemId;",
            new { itemId }, transaction, cancellationToken: ct)).ToList();
        var removedPlaces = 0;
        foreach (var fileId in fileIds)
        {
            removedPlaces += connection.Execute(new CommandDefinition(
                "DELETE FROM local_file_sources WHERE file_id = @fileId AND source_id = @sourceId;",
                new { fileId, sourceId }, transaction, cancellationToken: ct));
        }

        var stillHeld = connection.ExecuteScalar<long>(new CommandDefinition("""
            SELECT COUNT(*) FROM local_file_sources
             WHERE file_id IN (SELECT file_id FROM local_item_files WHERE item_id = @itemId);
            """, new { itemId }, transaction, cancellationToken: ct)) > 0;
        if (stillHeld)
        {
            return removedPlaces;
        }

        connection.Execute(new CommandDefinition(
            "DELETE FROM local_items WHERE id = @itemId;", new { itemId }, transaction, cancellationToken: ct));
        foreach (var fileId in fileIds)
        {
            connection.Execute(new CommandDefinition("""
                DELETE FROM local_files
                 WHERE id = @fileId
                   AND NOT EXISTS (SELECT 1 FROM local_item_files WHERE file_id = @fileId)
                   AND NOT EXISTS (SELECT 1 FROM local_file_sources WHERE file_id = @fileId);
                """, new { fileId }, transaction, cancellationToken: ct));
        }

        return removedPlaces;
    }

    private sealed class ItemRow
    {
        public Guid ItemId { get; init; }
        public string MediaKind { get; init; } = string.Empty;
    }
}
