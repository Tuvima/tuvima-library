using Dapper;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Storage.Services;

public sealed class WorkHierarchyMaintenanceService
{
    private readonly IDatabaseConnection _db;
    private readonly ILogger<WorkHierarchyMaintenanceService>? _logger;

    public WorkHierarchyMaintenanceService(
        IDatabaseConnection db,
        ILogger<WorkHierarchyMaintenanceService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _logger = logger;
    }

    public Task<int> CleanupEmptyParentsAsync(Guid? startingParentId, CancellationToken ct = default)
        => CleanupEmptyParentsAsync(ct);

    public async Task<int> CleanupEmptyParentsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var deleted = await _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            var count = 0;
            while (true)
            {
                var parentIds = conn.Query<Guid>(
                    EmptyParentSql,
                    transaction: tx).ToList();
                if (parentIds.Count == 0)
                {
                    break;
                }

                foreach (var parentId in parentIds)
                {
                    DeleteParentDerivedState(conn, tx, parentId);
                    count += conn.Execute("DELETE FROM works WHERE id = @parentId;", new { parentId }, tx);
                }
            }

            return count;
        }, ct).ConfigureAwait(false);

        if (deleted > 0)
        {
            _logger?.LogInformation("Removed {Count} empty parent work rows", deleted);
        }

        return deleted;
    }

    private const string EmptyParentSql = """
        SELECT p.id
        FROM works p
        WHERE p.work_kind = 'parent'
          AND COALESCE(p.is_catalog_only, 0) = 0
          AND NOT EXISTS (SELECT 1 FROM editions e WHERE e.work_id = p.id)
          AND NOT EXISTS (SELECT 1 FROM works child WHERE child.parent_work_id = p.id)
          AND NOT EXISTS (SELECT 1 FROM collection_items ci WHERE ci.work_id = p.id)
          AND NOT EXISTS (SELECT 1 FROM profile_work_preferences preference WHERE preference.work_id = p.id)
          AND NOT EXISTS (SELECT 1 FROM profile_saved_items saved WHERE saved.entity_id = p.id)
          AND NOT EXISTS (SELECT 1 FROM profile_reactions reaction WHERE reaction.entity_id = p.id)
          AND NOT EXISTS (SELECT 1 FROM profile_sequence_preferences preference
                          WHERE preference.media_type = p.media_type
                            AND (preference.container_key = p.parent_key OR preference.container_key = lower(hex(p.id))))
          AND NOT EXISTS (SELECT 1 FROM entity_assets ea WHERE ea.entity_id = p.id AND COALESCE(ea.is_user_override, 0) = 1)
          AND NOT EXISTS (SELECT 1 FROM entity_artwork_links link WHERE link.entity_id = p.id AND COALESCE(link.is_user_override, 0) = 1)
          AND COALESCE(NULLIF(p.display_overrides_json, ''), '') = '';
        """;

    private static void DeleteParentDerivedState(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        Guid parentId)
    {
        conn.Execute("DELETE FROM entity_assets WHERE entity_id = @parentId AND COALESCE(is_user_override, 0) = 0;", new { parentId }, tx);
        conn.Execute("DELETE FROM canonical_values WHERE entity_id = @parentId;", new { parentId }, tx);
        conn.Execute("DELETE FROM metadata_claims WHERE entity_id = @parentId;", new { parentId }, tx);
        conn.Execute("DELETE FROM review_queue WHERE entity_id = @parentId;", new { parentId }, tx);
        conn.Execute("UPDATE series_manifest_items SET linked_work_id = NULL WHERE linked_work_id = @parentId;", new { parentId }, tx);
    }
}
