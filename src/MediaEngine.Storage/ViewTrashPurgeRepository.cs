using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Removes personal items' records for Empty Trash. The checks and the deletes share one write transaction, and every check runs
/// before the first delete, so an item that was restored, changed or handed to Shared Library work a moment ago leaves the whole
/// group exactly as it was.
/// </summary>
public sealed class ViewTrashPurgeRepository(IDatabaseConnection database) : IViewTrashPurgeRepository
{
    public Task<ViewTrashPurgeResult> PurgeItemsAsync(IReadOnlyList<ViewTrashPurgeRequest> requests, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var groupIds = requests.Select(request => request.ItemId).ToHashSet();
        return database.ExecuteWriteAsync((connection, transaction, token) =>
        {
            token.ThrowIfCancellationRequested();

            // 1. Check every item first. Nothing is changed until all of them pass.
            var checkedItems = new List<(ViewTrashPurgeRequest Request, List<Guid> FileIds, List<Guid> Places)>(requests.Count);
            foreach (var request in requests)
            {
                var inTrash = connection.ExecuteScalar<long>(new CommandDefinition("""
                    SELECT COUNT(*) FROM local_items
                     WHERE id = @ItemId AND personal_space_id = @PersonalSpaceId AND owner_profile_id = @OwnerProfileId
                       AND scope_kind = 'personal' AND trashed_at IS NOT NULL;
                    """, new { request.ItemId, request.PersonalSpaceId, request.OwnerProfileId }, transaction, cancellationToken: token));
                if (inTrash == 0)
                {
                    return new ViewTrashPurgeResult(ViewTrashPurgeOutcome.NotInTrash, request.ItemId);
                }

                // Same conditions as the plan: only work in flight protects an item.
                var inFlight = connection.ExecuteScalar<long>(new CommandDefinition("""
                    SELECT (SELECT COUNT(*)
                              FROM view_shared_contribution_items ci
                              JOIN view_shared_contributions c ON c.id = ci.contribution_id
                             WHERE ci.item_id = @ItemId
                               AND (c.status = 'pending'
                                    OR (c.status = 'accepted'
                                        AND ci.execution_state IN ('waiting', 'transferring', 'cleanup_pending'))))
                         + (SELECT COUNT(*) FROM view_shared_transfers tr
                             WHERE tr.item_id = @ItemId AND tr.state IN ('planned', 'transferring', 'cleanup_pending'));
                    """, new { request.ItemId }, transaction, cancellationToken: token));
                if (inFlight > 0)
                {
                    return new ViewTrashPurgeResult(ViewTrashPurgeOutcome.SharedWorkInFlight, request.ItemId);
                }

                // The files the item uses now, and every place they are indexed at inside the item's own library. A place the
                // plan did not see (a scan added it since) means the plan is out of date.
                var fileIds = connection.Query<Guid>(new CommandDefinition(
                    "SELECT DISTINCT file_id FROM local_item_files WHERE item_id = @ItemId;",
                    new { request.ItemId }, transaction, cancellationToken: token)).ToList();
                var places = connection.Query<Guid>(new CommandDefinition("""
                    SELECT DISTINCT lfs.id
                      FROM local_items li
                      JOIN local_item_files lif ON lif.item_id = li.id
                      JOIN local_file_sources lfs ON lfs.file_id = lif.file_id AND lfs.library_id = li.library_id
                     WHERE li.id = @ItemId;
                    """, new { request.ItemId }, transaction, cancellationToken: token)).ToList();
                if (places.Any(place => !request.PlannedSourceRowIds.Contains(place)))
                {
                    return new ViewTrashPurgeResult(ViewTrashPurgeOutcome.ChangedSincePlanned, request.ItemId);
                }

                // A file about to leave the disk must not have been picked up by an item outside this group since the plan was made
                // (the same file record in the same library is the same file on disk). The planner decided "shared" with the same rule.
                foreach (var fileId in request.DeleteFileIds)
                {
                    var users = connection.Query<Guid>(new CommandDefinition("""
                        SELECT DISTINCT other.id
                          FROM local_items me
                          JOIN local_item_files oif ON oif.file_id = @fileId
                          JOIN local_items other ON other.id = oif.item_id AND other.library_id = me.library_id
                         WHERE me.id = @ItemId;
                        """, new { request.ItemId, fileId }, transaction, cancellationToken: token));
                    if (users.Any(user => !groupIds.Contains(user)))
                    {
                        return new ViewTrashPurgeResult(ViewTrashPurgeOutcome.ChangedSincePlanned, request.ItemId);
                    }
                }

                checkedItems.Add((request, fileIds, places));
            }

            // 2. Remove. The item row first: its cascades clear details, file links, tags, document rows, Gallery memberships,
            // notes, search keys (the delete trigger clears the search text) and offline download entries. A Gallery that used
            // the item as its cover falls back through its own SET NULL link; Galleries themselves are never removed.
            foreach (var (request, fileIds, places) in checkedItems)
            {
                connection.Execute(new CommandDefinition(
                    "DELETE FROM local_items WHERE id = @ItemId;",
                    new { request.ItemId }, transaction, cancellationToken: token));

                // Only places of a file this item used are removed; a place id from anywhere else removes nothing.
                foreach (var rowId in request.RemovedSourceRowIds.Where(places.Contains))
                {
                    connection.Execute(new CommandDefinition(
                        "DELETE FROM local_file_sources WHERE id = @rowId;",
                        new { rowId }, transaction, cancellationToken: token));
                }

                // A file record goes only when no item links it and no place (in any library) still indexes it.
                foreach (var fileId in fileIds)
                {
                    connection.Execute(new CommandDefinition("""
                        DELETE FROM local_files
                         WHERE id = @fileId
                           AND NOT EXISTS (SELECT 1 FROM local_item_files WHERE file_id = @fileId)
                           AND NOT EXISTS (SELECT 1 FROM local_file_sources WHERE file_id = @fileId);
                        """, new { fileId }, transaction, cancellationToken: token));
                }
            }

            return new ViewTrashPurgeResult(ViewTrashPurgeOutcome.Removed);
        }, ct);
    }

    public Task<bool> ItemExistsAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        return Task.FromResult(connection.ExecuteScalar<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM local_items WHERE id = @itemId;", new { itemId }, cancellationToken: ct)) > 0);
    }
}
