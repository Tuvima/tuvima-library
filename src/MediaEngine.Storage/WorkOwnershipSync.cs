using Dapper;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// Recomputes whether episodes (and their seasons) are owned, from the files that reach them
/// through <c>work_owned_assets</c>: a work's own file or a combined file that covers it.
/// Call inside the transaction that changed files or coverage so ownership never lags behind.
/// </summary>
internal static class WorkOwnershipSync
{
    public static void Recompute(SqliteConnection conn, SqliteTransaction? tx, IEnumerable<Guid> workIds)
    {
        var distinct = workIds.Where(id => id != Guid.Empty).Distinct().ToList();
        foreach (var workId in distinct)
        {
            conn.Execute("""
                UPDATE works SET
                    ownership = CASE WHEN EXISTS (
                        SELECT 1 FROM work_owned_assets woa WHERE woa.work_id = @workId) THEN 'Owned' ELSE 'Unowned' END,
                    is_catalog_only = CASE WHEN EXISTS (
                        SELECT 1 FROM work_owned_assets woa WHERE woa.work_id = @workId) THEN 0 ELSE 1 END,
                    work_kind = CASE WHEN EXISTS (
                        SELECT 1 FROM work_owned_assets woa WHERE woa.work_id = @workId) THEN 'child' ELSE 'catalog' END
                WHERE id = @workId AND work_kind IN ('child', 'catalog');
                """, new { workId }, tx);
        }

        var seasonIds = distinct
            .SelectMany(workId => conn.Query<Guid>(
                "SELECT parent_work_id FROM works WHERE id = @workId AND parent_work_id IS NOT NULL;",
                new { workId }, tx))
            .Distinct()
            .ToList();
        foreach (var seasonId in seasonIds)
        {
            conn.Execute("""
                UPDATE works SET
                    ownership = CASE WHEN EXISTS (
                        SELECT 1 FROM works episode JOIN work_owned_assets woa ON woa.work_id = episode.id
                        WHERE episode.parent_work_id = @seasonId) THEN 'Owned' ELSE 'Unowned' END,
                    is_catalog_only = CASE WHEN EXISTS (
                        SELECT 1 FROM works episode JOIN work_owned_assets woa ON woa.work_id = episode.id
                        WHERE episode.parent_work_id = @seasonId) THEN 0 ELSE 1 END
                WHERE id = @seasonId AND work_kind = 'parent';
                """, new { seasonId }, tx);
        }
    }
}
