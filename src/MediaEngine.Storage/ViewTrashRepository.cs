using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Read-only facts about one person's Trash in View. Every query is limited to the named Personal Space, owner and
/// <c>scope_kind = 'personal'</c>, so Shared Library items and other people's items can never be returned. Reads use a
/// short-lived connection; nothing here writes.
/// </summary>
public sealed class ViewTrashRepository(IDatabaseConnection database) : IViewTrashRepository
{
    // SQLite limits how many values one IN list may hold; paths are looked up in slices well under that.
    private const int PathSliceSize = 400;

    public Task<ViewTrashTotals> GetTotalsAsync(
        Guid personalSpaceId, Guid ownerProfileId, bool includeDocuments, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var counts = connection.QuerySingle<TotalsRow>(new CommandDefinition("""
            SELECT COUNT(*) AS ItemCount,
                   COALESCE(SUM(CASE WHEN media_kind = 'image' THEN 1 ELSE 0 END), 0) AS ImageCount,
                   COALESCE(SUM(CASE WHEN media_kind = 'video' THEN 1 ELSE 0 END), 0) AS VideoCount,
                   COALESCE(SUM(CASE WHEN media_kind = 'document' THEN 1 ELSE 0 END), 0) AS DocumentCount,
                   COALESCE(SUM(CASE WHEN media_kind NOT IN ('image', 'video', 'document') THEN 1 ELSE 0 END), 0) AS OtherCount
              FROM local_items
             WHERE personal_space_id = @personalSpaceId AND owner_profile_id = @ownerProfileId
               AND scope_kind = 'personal' AND trashed_at IS NOT NULL
               AND (@includeDocuments = 1 OR media_kind <> 'document');
            """, new { personalSpaceId, ownerProfileId, includeDocuments = includeDocuments ? 1 : 0 }, cancellationToken: ct));

        // A file record used by several trashed items is counted once.
        var bytes = connection.ExecuteScalar<long>(new CommandDefinition("""
            SELECT COALESCE(SUM(lf.byte_size), 0)
              FROM local_files lf
             WHERE lf.id IN (SELECT lif.file_id
                               FROM local_item_files lif
                               JOIN local_items li ON li.id = lif.item_id
                              WHERE li.personal_space_id = @personalSpaceId AND li.owner_profile_id = @ownerProfileId
                                AND li.scope_kind = 'personal' AND li.trashed_at IS NOT NULL
                                AND (@includeDocuments = 1 OR li.media_kind <> 'document'));
            """, new { personalSpaceId, ownerProfileId, includeDocuments = includeDocuments ? 1 : 0 }, cancellationToken: ct));

        return Task.FromResult(new ViewTrashTotals(
            (int)counts.ItemCount, (int)counts.ImageCount, (int)counts.VideoCount,
            (int)counts.DocumentCount, (int)counts.OtherCount, bytes));
    }

    public Task<IReadOnlyList<ViewTrashItem>> GetTrashedItemsAsync(
        Guid personalSpaceId, Guid ownerProfileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();

        // LEFT joins: an item with no file link, or a file with no indexed place, must still come back (with nothing under it).
        var rows = connection.Query<ItemFileRow>(new CommandDefinition("""
            SELECT li.id AS ItemId, li.media_kind AS MediaKind, li.library_id AS LibraryId,
                   lif.file_id AS FileId, lif.role AS Role, lf.byte_size AS ByteSize,
                   lfs.id AS SourceRowId, lfs.source_id AS SourceId, lfs.file_path AS FilePath
              FROM local_items li
              LEFT JOIN local_item_files lif ON lif.item_id = li.id
              LEFT JOIN local_files lf ON lf.id = lif.file_id
              LEFT JOIN local_file_sources lfs ON lfs.file_id = lif.file_id AND lfs.library_id = li.library_id
             WHERE li.personal_space_id = @personalSpaceId AND li.owner_profile_id = @ownerProfileId
               AND li.scope_kind = 'personal' AND li.trashed_at IS NOT NULL
             ORDER BY li.id, lif.position, lif.file_id, lif.role, lfs.file_path;
            """, new { personalSpaceId, ownerProfileId }, cancellationToken: ct)).ToList();

        var items = new List<ViewTrashItem>();
        foreach (var itemGroup in rows.GroupBy(row => row.ItemId))
        {
            var first = itemGroup.First();
            var files = new List<ViewTrashFile>();
            foreach (var fileGroup in itemGroup.Where(row => row.FileId.HasValue)
                         .GroupBy(row => (FileId: row.FileId!.Value, row.Role)))
            {
                var sources = fileGroup
                    .Where(row => row.SourceRowId.HasValue && !string.IsNullOrEmpty(row.FilePath))
                    .Select(row => new ViewTrashFileSource(row.SourceRowId!.Value, row.SourceId, row.FilePath!))
                    .ToList();
                files.Add(new ViewTrashFile(
                    fileGroup.Key.FileId, fileGroup.Key.Role ?? string.Empty, fileGroup.First().ByteSize ?? 0, sources));
            }

            items.Add(new ViewTrashItem(first.ItemId, first.MediaKind, first.LibraryId, files));
        }

        return Task.FromResult<IReadOnlyList<ViewTrashItem>>(items);
    }

    public Task<IReadOnlyList<ViewTrashPathUse>> GetPathUsesByOtherItemsAsync(
        Guid personalSpaceId, Guid ownerProfileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();

        // The same file record, used by another item of the same library (a Live Photo's video that is also its own item).
        var uses = connection.Query<PathUseRow>(new CommandDefinition("""
            SELECT DISTINCT t.id AS ItemId, lfs.file_path AS FilePath, other.id AS OtherItemId
              FROM local_items t
              JOIN local_item_files tif ON tif.item_id = t.id
              JOIN local_file_sources lfs ON lfs.file_id = tif.file_id AND lfs.library_id = t.library_id
              JOIN local_item_files oif ON oif.file_id = tif.file_id
              JOIN local_items other ON other.id = oif.item_id AND other.id <> t.id AND other.library_id = t.library_id
             WHERE t.personal_space_id = @personalSpaceId AND t.owner_profile_id = @ownerProfileId
               AND t.scope_kind = 'personal' AND t.trashed_at IS NOT NULL;
            """, new { personalSpaceId, ownerProfileId }, cancellationToken: ct))
            .Select(row => new ViewTrashPathUse(row.ItemId, row.FilePath, row.OtherItemId))
            .ToList();

        // The same path indexed in another library and used by an item there. Matching content on a different path is not this.
        var paths = connection.Query<PathUseRow>(new CommandDefinition("""
            SELECT DISTINCT t.id AS ItemId, lfs.file_path AS FilePath
              FROM local_items t
              JOIN local_item_files tif ON tif.item_id = t.id
              JOIN local_file_sources lfs ON lfs.file_id = tif.file_id AND lfs.library_id = t.library_id
             WHERE t.personal_space_id = @personalSpaceId AND t.owner_profile_id = @ownerProfileId
               AND t.scope_kind = 'personal' AND t.trashed_at IS NOT NULL;
            """, new { personalSpaceId, ownerProfileId }, cancellationToken: ct)).ToList();
        if (paths.Count > 0)
        {
            var libraryId = connection.Query<Guid>(new CommandDefinition(
                "SELECT library_id FROM view_personal_spaces WHERE id = @personalSpaceId;",
                new { personalSpaceId }, cancellationToken: ct)).Single();
            var byPath = paths.ToLookup(row => row.FilePath, StringComparer.OrdinalIgnoreCase);
            foreach (var slice in paths.Select(row => row.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(PathSliceSize))
            {
                ct.ThrowIfCancellationRequested();
                var elsewhere = connection.Query<PathUseRow>(new CommandDefinition("""
                    SELECT DISTINCT lfs2.file_path AS FilePath, other.id AS OtherItemId
                      FROM local_file_sources lfs2
                      JOIN local_item_files oif ON oif.file_id = lfs2.file_id
                      JOIN local_items other ON other.id = oif.item_id AND other.library_id = lfs2.library_id
                     WHERE lfs2.library_id <> @libraryId
                       AND lfs2.file_path COLLATE NOCASE IN @slice;
                    """, new { libraryId, slice }, cancellationToken: ct));
                foreach (var use in elsewhere)
                {
                    foreach (var trashed in byPath[use.FilePath])
                    {
                        uses.Add(new ViewTrashPathUse(trashed.ItemId, trashed.FilePath, use.OtherItemId));
                    }
                }
            }
        }

        return Task.FromResult<IReadOnlyList<ViewTrashPathUse>>(uses);
    }

    public Task<IReadOnlySet<Guid>> GetItemsWithPendingContributionAsync(
        Guid personalSpaceId, Guid ownerProfileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();

        // Only work that is in flight protects an item: waiting for a household administrator, accepted and waiting or copying,
        // or a transfer that is planned, copying or cleaning up. Failed, needs-attention, declined, cancelled and completed
        // states do not block; the person's own copy may go and the history row is left unlinked.
        var ids = connection.Query<Guid>(new CommandDefinition("""
            SELECT ci.item_id
              FROM view_shared_contribution_items ci
              JOIN view_shared_contributions c ON c.id = ci.contribution_id
              JOIN local_items t ON t.id = ci.item_id
             WHERE t.personal_space_id = @personalSpaceId AND t.owner_profile_id = @ownerProfileId
               AND t.scope_kind = 'personal' AND t.trashed_at IS NOT NULL
               AND (c.status = 'pending'
                    OR (c.status = 'accepted'
                        AND ci.execution_state IN ('waiting', 'transferring', 'cleanup_pending')))
            UNION
            SELECT tr.item_id
              FROM view_shared_transfers tr
              JOIN local_items t ON t.id = tr.item_id
             WHERE t.personal_space_id = @personalSpaceId AND t.owner_profile_id = @ownerProfileId
               AND t.scope_kind = 'personal' AND t.trashed_at IS NOT NULL
               AND tr.state IN ('planned', 'transferring', 'cleanup_pending');
            """, new { personalSpaceId, ownerProfileId }, cancellationToken: ct)).ToHashSet();
        return Task.FromResult<IReadOnlySet<Guid>>(ids);
    }

    public Task<IReadOnlyList<ViewTrashGalleryMembership>> GetManualGalleryMembershipsAsync(
        Guid personalSpaceId, Guid ownerProfileId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = database.CreateConnection();
        var rows = connection.Query<GalleryRow>(new CommandDefinition("""
            SELECT g.id AS GalleryId, g.name AS Name, gi.item_id AS ItemId
              FROM view_gallery_items gi
              JOIN view_galleries g ON g.id = gi.gallery_id
              JOIN local_items t ON t.id = gi.item_id
             WHERE g.gallery_kind = 'manual'
               AND t.personal_space_id = @personalSpaceId AND t.owner_profile_id = @ownerProfileId
               AND t.scope_kind = 'personal' AND t.trashed_at IS NOT NULL;
            """, new { personalSpaceId, ownerProfileId }, cancellationToken: ct))
            .Select(row => new ViewTrashGalleryMembership(row.GalleryId, row.Name, row.ItemId))
            .ToList();
        return Task.FromResult<IReadOnlyList<ViewTrashGalleryMembership>>(rows);
    }

    private sealed class TotalsRow
    {
        public long ItemCount { get; init; }
        public long ImageCount { get; init; }
        public long VideoCount { get; init; }
        public long DocumentCount { get; init; }
        public long OtherCount { get; init; }
    }

    private sealed class ItemFileRow
    {
        public Guid ItemId { get; init; }
        public string MediaKind { get; init; } = string.Empty;
        public Guid LibraryId { get; init; }
        public Guid? FileId { get; init; }
        public string? Role { get; init; }
        public long? ByteSize { get; init; }
        public Guid? SourceRowId { get; init; }
        public Guid? SourceId { get; init; }
        public string? FilePath { get; init; }
    }

    private sealed class PathUseRow
    {
        public Guid ItemId { get; init; }
        public string FilePath { get; init; } = string.Empty;
        public Guid OtherItemId { get; init; }
    }

    private sealed class GalleryRow
    {
        public Guid GalleryId { get; init; }
        public string Name { get; init; } = string.Empty;
        public Guid ItemId { get; init; }
    }
}
