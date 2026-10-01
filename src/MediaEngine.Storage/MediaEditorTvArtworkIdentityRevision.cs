using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Domain;

namespace MediaEngine.Storage;

/// <summary>
/// Opaque identity snapshot for every file affected by a reviewed TV artwork
/// choice. File paths, codecs, and writeback state deliberately do not enter it.
/// The same reader runs before and inside the pairing write transaction.
/// </summary>
internal static class MediaEditorTvArtworkIdentityRevision
{
    internal static IReadOnlyDictionary<Guid, string> Read(IDbConnection connection,
        IDbTransaction? transaction, IReadOnlyList<Guid> assetIds)
    {
        if (assetIds.Count is < 1 or > 1000 || assetIds.Contains(Guid.Empty)
            || assetIds.Distinct().Count() != assetIds.Count)
            return new Dictionary<Guid, string>();

        var rows = connection.Query<IdentityRow>("""
            SELECT a.id AS AssetId, a.edition_id AS EditionId, a.library_id AS LibraryId,
                   a.status AS Status, a.is_orphaned AS IsOrphaned,
                   e.work_id AS WorkId, w.parent_work_id AS SeasonId,
                   season.parent_work_id AS ShowId,
                   w.media_type AS MediaType, w.work_kind AS WorkKind,
                   w.ordinal AS WorkOrdinal, w.ordinal_sort AS WorkOrdinalSort,
                   COALESCE((SELECT value FROM canonical_values WHERE entity_id=a.id
                       AND key=@revisionKey), '') AS AssetIdentityRevision,
                   COALESCE((SELECT value FROM canonical_values WHERE entity_id=e.id
                       AND key=@revisionKey), '') AS EditionIdentityRevision,
                   COALESCE((SELECT value FROM canonical_values WHERE entity_id=w.id
                       AND key=@revisionKey), '') AS WorkIdentityRevision,
                   COALESCE((SELECT value FROM canonical_values WHERE entity_id=season.id
                       AND key=@revisionKey), '') AS SeasonIdentityRevision,
                   COALESCE((SELECT value FROM canonical_values WHERE entity_id=show.id
                       AND key=@revisionKey), '') AS ShowIdentityRevision
            FROM media_assets a
            JOIN editions e ON e.id=a.edition_id
            JOIN works w ON w.id=e.work_id AND w.media_type='TV' AND w.work_kind='child'
            JOIN works season ON season.id=w.parent_work_id
                AND season.media_type='TV' AND season.work_kind='parent'
            JOIN works show ON show.id=season.parent_work_id
                AND show.media_type='TV' AND show.work_kind='parent'
            WHERE a.id IN @ids AND a.status='Normal' AND a.is_orphaned=0;
            """, new { ids = assetIds.Select(GuidSql.ToBlob).ToArray(),
                revisionKey = MetadataFieldConstants.IdentityRevision }, transaction).ToArray();
        if (rows.Length != assetIds.Count || rows.Any(row =>
                !Guid.TryParse(row.LibraryId, out var libraryId) || libraryId == Guid.Empty))
            return new Dictionary<Guid, string>();

        var related = rows.SelectMany(row => new[]
            { row.AssetId, row.EditionId, row.WorkId, row.SeasonId, row.ShowId })
            .Distinct().ToArray();
        var bridges = new Dictionary<Guid, List<BridgeRow>>();
        foreach (var batch in related.Chunk(400))
            foreach (var bridge in connection.Query<BridgeRow>("""
                SELECT entity_id AS EntityId, id_type AS IdType, id_value AS IdValue
                FROM bridge_ids WHERE entity_id IN @ids;
                """, new { ids = batch.Select(GuidSql.ToBlob).ToArray() }, transaction))
            {
                if (!bridges.TryGetValue(bridge.EntityId, out var list))
                    bridges[bridge.EntityId] = list = [];
                list.Add(bridge);
            }

        return rows.ToDictionary(row => row.AssetId, row =>
        {
            var identityBridges = new[] { row.AssetId, row.EditionId, row.WorkId,
                    row.SeasonId, row.ShowId }
                .Distinct().SelectMany(id => bridges.GetValueOrDefault(id) ?? [])
                .OrderBy(item => item.EntityId).ThenBy(item => item.IdType, StringComparer.Ordinal)
                .ThenBy(item => item.IdValue, StringComparer.Ordinal).ToArray();
            var state = JsonSerializer.Serialize(new { Version = 1, row.AssetId,
                row.EditionId, row.WorkId, row.SeasonId, row.ShowId, row.LibraryId,
                row.MediaType, row.WorkKind, row.WorkOrdinal, row.WorkOrdinalSort,
                row.AssetIdentityRevision, row.EditionIdentityRevision,
                row.WorkIdentityRevision, row.SeasonIdentityRevision,
                row.ShowIdentityRevision, Bridges = identityBridges });
            return "v1:" + Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(state)));
        });
    }

    private sealed class IdentityRow
    {
        public Guid AssetId { get; set; }
        public Guid EditionId { get; set; }
        public Guid WorkId { get; set; }
        public Guid SeasonId { get; set; }
        public Guid ShowId { get; set; }
        public string? LibraryId { get; set; }
        public string MediaType { get; set; } = "";
        public string WorkKind { get; set; } = "";
        public double? WorkOrdinal { get; set; }
        public string? WorkOrdinalSort { get; set; }
        public string AssetIdentityRevision { get; set; } = "";
        public string EditionIdentityRevision { get; set; } = "";
        public string WorkIdentityRevision { get; set; } = "";
        public string SeasonIdentityRevision { get; set; } = "";
        public string ShowIdentityRevision { get; set; } = "";
    }

    private sealed class BridgeRow
    {
        public Guid EntityId { get; set; }
        public string IdType { get; set; } = "";
        public string IdValue { get; set; } = "";
    }
}
