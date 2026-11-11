using Dapper;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Playback;

/// <summary>What the data store knows about a file that helps a bookmark set recognise its title (Videos spec D46, Global bookmarks spec D14).</summary>
public sealed record BookmarkPackRecognition(Guid WorkId, string? EditionLabel, IReadOnlyDictionary<string, string> Ids, string? ContentHash = null);

/// <summary>Reads the canonical IDs a bookmark file uses to say which title it is for. Nothing is stored: a bookmark set is applied as ordinary bookmarks.</summary>
public sealed class BookmarkRecognitionRepository
{
    private readonly IDatabaseConnection _db;

    public BookmarkRecognitionRepository(IDatabaseConnection db)
    {
        _db = db;
    }

    /// <summary>
    /// The canonical IDs a bookmark file may use to recognise the title the file belongs to, plus the edition's label. Only IDs on the
    /// recognition list are returned; internal and person IDs never leave the data store.
    /// </summary>
    public Task<BookmarkPackRecognition?> GetRecognitionAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var asset = conn.QueryFirstOrDefault<AssetRow>(
            """
            SELECT e.work_id AS WorkId, e.id AS EditionId, e.format_label AS EditionLabel, e.wikidata_qid AS EditionQid, ma.content_hash AS ContentHash
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            WHERE ma.id = @assetId;
            """,
            new { assetId });
        if (asset is null)
        {
            return Task.FromResult<BookmarkPackRecognition?>(null);
        }

        var rows = conn.Query<BridgeRow>(
            """
            SELECT id_type AS IdType, id_value AS IdValue
            FROM bridge_ids
            WHERE entity_id IN (@workId, @editionId)
            ORDER BY CASE WHEN entity_id = @workId THEN 0 ELSE 1 END, id_type;
            """,
            new { workId = asset.WorkId, editionId = asset.EditionId });
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (BookmarkPackMatcher.RecognitionIdKeys.Contains(row.IdType) && !string.IsNullOrWhiteSpace(row.IdValue))
            {
                ids.TryAdd(row.IdType.Trim().ToLowerInvariant(), row.IdValue.Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(asset.EditionQid))
        {
            ids.TryAdd("wikidata_qid", asset.EditionQid.Trim());
        }

        return Task.FromResult<BookmarkPackRecognition?>(new BookmarkPackRecognition(asset.WorkId, asset.EditionLabel, ids, asset.ContentHash));
    }

    private sealed record AssetRow
    {
        public Guid WorkId { get; init; }
        public Guid EditionId { get; init; }
        public string? EditionLabel { get; init; }
        public string? EditionQid { get; init; }
        public string? ContentHash { get; init; }
    }

    private sealed record BridgeRow
    {
        public string IdType { get; init; } = string.Empty;
        public string IdValue { get; init; } = string.Empty;
    }
}
