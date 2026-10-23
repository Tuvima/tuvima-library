using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// Files per library in one grouped read (simpler access D13). A folder is matched with the first check's path rule
/// (<see cref="FolderPathSql"/>, <see cref="FolderFirstCheckPaths"/>), never a second copy of it. Short-lived connection per call.
/// </summary>
public sealed class MediaAssetLibraryCountRepository(IDatabaseConnection db) : IMediaAssetLibraryCountRepository
{
    public Task<IReadOnlyDictionary<Guid, int>> CountByLibraryInFolderAsync(string folderPath, Guid? libraryId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var prefix = FolderFirstCheckPaths.Prefix(folderPath);
        var folded = FolderFirstCheckPaths.Folded(prefix);
        using var connection = db.CreateConnection();
        var rows = connection.Query<(string LibraryId, int Files)>($"""
            SELECT a.library_id AS LibraryId, COUNT(*) AS Files
            FROM media_assets a
            WHERE a.is_orphaned = 0 AND a.library_id IS NOT NULL
              AND {FolderPathSql.InFolderOfBounds("a.file_path_root")}
              AND (@libraryId IS NULL OR lower(a.library_id) = @libraryId)
            GROUP BY a.library_id;
            """, new
        {
            folderLow = folded,
            folderHigh = FolderFirstCheckPaths.RangeEnd(folded),
            folderPrefix = prefix,
            libraryId = libraryId?.ToString("D"),
        });
        return Task.FromResult(Collect(rows));
    }

    public Task<IReadOnlyDictionary<Guid, int>> CountByLibraryForAssetsAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (assetIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<Guid, int>>(new Dictionary<Guid, int>());
        }

        using var connection = db.CreateConnection();
        // The ids go in as one JSON array (one parameter, not one per id), turned back into id blobs in SQL.
        var rows = connection.Query<(string LibraryId, int Files)>("""
            SELECT a.library_id AS LibraryId, COUNT(*) AS Files
            FROM media_assets a
            WHERE a.is_orphaned = 0 AND a.library_id IS NOT NULL
              AND a.id IN (SELECT unhex(value) FROM json_each(@assetIds))
            GROUP BY a.library_id;
            """, new { assetIds = GuidSql.ToJsonHexArray(assetIds.Distinct()) });
        return Task.FromResult(Collect(rows));
    }

    // A library id is stored as text; two spellings of one id (case) add up, and text that is not an id is left out.
    private static IReadOnlyDictionary<Guid, int> Collect(IEnumerable<(string LibraryId, int Files)> rows)
    {
        var counts = new Dictionary<Guid, int>();
        foreach (var (libraryId, files) in rows)
        {
            if (Guid.TryParse(libraryId, out var id) && id != Guid.Empty)
            {
                counts[id] = counts.GetValueOrDefault(id) + files;
            }
        }

        return counts;
    }
}
