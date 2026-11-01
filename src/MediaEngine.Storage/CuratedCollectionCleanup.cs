using System.Data;
using Dapper;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Storage;

/// <summary>
/// Removes manually curated collections that have no members left. A curated collection is a
/// <c>Custom</c> collection with manual membership whose members are stored (materialized), so it is never allowed to be
/// empty; a member is a library work in <c>collection_items</c> or a personal-media source (gallery or smart rule)
/// in <c>collection_view_sources</c>; a collection made from an imported list is kept even with no member owned yet, because the list brings titles in as the library gains them; rule-driven Custom collections hold no stored members and playlists, playlist folders and
/// system groupings follow their own rules, so none of those are touched.
///
/// <para>
/// This is deliberately an explicit call made by each code path that deletes works rather than a
/// trigger: a collection can be emptied for a moment while it is being rewritten, and only the
/// code that deletes the works knows the moment has passed.
/// </para>
/// </summary>
internal static class CuratedCollectionCleanup
{
    /// <summary>
    /// Deletes every empty curated collection on the caller's connection (and transaction, when
    /// given) and records one system activity entry for each. Returns what was removed.
    /// </summary>
    internal static IReadOnlyList<RemovedCollection> DeleteEmpty(
        IDbConnection conn,
        IDbTransaction? transaction,
        string reason = "its last item left the library")
    {
        var empty = conn.Query<(Guid Id, string? Name)>("""
            SELECT c.id AS Id, c.display_name AS Name
            FROM collections c
            WHERE c.collection_type = 'Custom'
              AND c.resolution = 'materialized'
              AND c.membership_mode = 'Manual'
              AND NOT EXISTS (SELECT 1 FROM collection_items ci WHERE ci.collection_id = c.id)
              AND NOT EXISTS (SELECT 1 FROM collection_view_sources s WHERE s.collection_id = c.id)
              AND NOT EXISTS (SELECT 1 FROM collection_list_sources l WHERE l.collection_id = c.id);
            """, transaction: transaction).ToList();
        if (empty.Count == 0)
        {
            return [];
        }

        var removed = new List<RemovedCollection>(empty.Count);
        foreach (var (id, storedName) in empty)
        {
            var name = string.IsNullOrWhiteSpace(storedName) ? "Untitled collection" : storedName.Trim();

            // Saved-item and reaction rows point at collections by id without a foreign key.
            conn.Execute(
                "DELETE FROM profile_saved_items WHERE entity_kind = 'Collection' AND entity_id = @id;",
                new { id }, transaction);
            conn.Execute(
                "DELETE FROM profile_reactions WHERE entity_kind = 'Collection' AND entity_id = @id;",
                new { id }, transaction);
            conn.Execute("DELETE FROM collections WHERE id = @id;", new { id }, transaction);

            SystemActivityRepository.Write(conn, transaction, new SystemActivityEntry
            {
                ActionType = SystemActionType.AutoPurge,
                CollectionName = name,
                EntityId = id,
                EntityType = "Collection",
                Detail = $"Removed the collection '{name}' because {reason}.",
            });
            removed.Add(new RemovedCollection(id, name));
        }

        return removed;
    }
}
