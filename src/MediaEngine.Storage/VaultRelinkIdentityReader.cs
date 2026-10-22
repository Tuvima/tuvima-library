using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using MediaEngine.Domain.Services;

namespace MediaEngine.Storage;

/// <summary>
/// Reads how to recognise Vault videos for the bookmark re-link: canonical IDs (the bridge IDs of the work and edition, the edition's
/// Wikidata ID and the Stash-box record of a matched video), the edition, the running time taken from the chapters, and the exact file
/// fingerprints. It is used on the live store to describe the videos a rescan found, and on a backup copy to describe the videos whose
/// bookmarks are being kept. It only reads.
/// </summary>
public static class VaultRelinkIdentityReader
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static IReadOnlyDictionary<Guid, VaultRelinkCandidate> Read(IDbConnection connection, IReadOnlyCollection<Guid> assetIds)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var result = new Dictionary<Guid, VaultRelinkCandidate>();
        if (assetIds.Count == 0)
        {
            return result;
        }

        var ids = GuidSql.ToJsonHexArray(assetIds);
        var assets = connection.Query<AssetRow>(
            """
            SELECT ma.id AS AssetId, e.work_id AS WorkId, e.id AS EditionId, e.format_label AS Edition,
                   e.wikidata_qid AS EditionQid, ma.content_hash AS ContentHash
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            WHERE ma.id IN (SELECT unhex(value) FROM json_each(@ids));
            """,
            new { ids }).ToList();
        if (assets.Count == 0)
        {
            return result;
        }

        var entityIds = GuidSql.ToJsonHexArray(assets.SelectMany(asset => new[] { asset.WorkId, asset.EditionId }).Distinct());
        var bridge = connection.Query<BridgeRow>(
            """
            SELECT entity_id AS EntityId, id_type AS IdType, id_value AS IdValue
            FROM bridge_ids
            WHERE entity_id IN (SELECT unhex(value) FROM json_each(@entityIds))
            ORDER BY id_type;
            """,
            new { entityIds }).ToList();
        var matches = connection.Query<MatchRow>(
            "SELECT asset_id AS AssetId, server_id AS ServerId, record_id AS RecordId FROM vault_matches WHERE asset_id IN (SELECT unhex(value) FROM json_each(@ids));",
            new { ids }).ToDictionary(row => row.AssetId);
        var prints = connection.Query<PrintRow>(
            "SELECT asset_id AS AssetId, hash AS Hash FROM vault_file_fingerprints WHERE algorithm IN ('OSHASH', 'MD5') AND asset_id IN (SELECT unhex(value) FROM json_each(@ids));",
            new { ids }).ToLookup(row => row.AssetId, row => row.Hash);
        var chapters = connection.Query<ChapterRow>(
            """
            SELECT asset_id AS AssetId, start_ms AS StartMs, end_ms AS EndMs, title AS Title
            FROM media_asset_chapters
            WHERE asset_id IN (SELECT unhex(value) FROM json_each(@ids))
            ORDER BY asset_id, chapter_index;
            """,
            new { ids }).ToLookup(row => row.AssetId);

        foreach (var asset in assets)
        {
            var recognition = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in bridge.Where(row => row.EntityId == asset.WorkId || row.EntityId == asset.EditionId)
                         .OrderBy(row => row.EntityId == asset.WorkId ? 0 : 1))
            {
                if (BookmarkPackMatcher.RecognitionIdKeys.Contains(row.IdType) && !string.IsNullOrWhiteSpace(row.IdValue))
                {
                    recognition.TryAdd(row.IdType.Trim().ToLowerInvariant(), row.IdValue.Trim());
                }
            }

            if (!string.IsNullOrWhiteSpace(asset.EditionQid))
            {
                recognition.TryAdd("wikidata_qid", asset.EditionQid.Trim());
            }

            if (matches.TryGetValue(asset.AssetId, out var match))
            {
                recognition[BookmarkPackMatcher.VaultStashBoxIdKey] = $"{match.ServerId}|{match.RecordId}";
            }

            var assetChapters = chapters[asset.AssetId].ToList();
            double? runningTime = assetChapters
                .Select(chapter => chapter.EndMs ?? chapter.StartMs)
                .DefaultIfEmpty(0)
                .Max() is > 0 and var last ? last / 1000.0 : null;
            var identity = new VaultRelinkIdentity(
                recognition,
                runningTime,
                string.IsNullOrWhiteSpace(asset.Edition) ? null : asset.Edition.Trim(),
                asset.ContentHash,
                prints[asset.AssetId].ToList());
            result[asset.AssetId] = new VaultRelinkCandidate(
                asset.AssetId,
                asset.WorkId,
                identity,
                assetChapters
                    .Where(chapter => !string.IsNullOrWhiteSpace(chapter.Title))
                    .Select(chapter => new BookmarkPackChapter(chapter.Title!.Trim(), chapter.StartMs / 1000.0))
                    .ToList());
        }

        return result;
    }

    public static string Serialize(VaultRelinkIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return JsonSerializer.Serialize(new IdentityJson
        {
            Ids = identity.Ids.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            RunningTimeSeconds = identity.RunningTimeSeconds,
            Edition = identity.Edition,
            ContentHash = identity.ContentHash,
            Fingerprints = identity.Fingerprints.ToList(),
        }, Json);
    }

    public static VaultRelinkIdentity Parse(string json)
    {
        var parsed = JsonSerializer.Deserialize<IdentityJson>(json, Json) ?? new IdentityJson();
        return new VaultRelinkIdentity(
            parsed.Ids ?? new Dictionary<string, string>(),
            parsed.RunningTimeSeconds,
            parsed.Edition,
            parsed.ContentHash,
            parsed.Fingerprints ?? []);
    }

    private sealed class IdentityJson
    {
        [JsonPropertyName("ids")]
        public Dictionary<string, string>? Ids { get; set; }

        [JsonPropertyName("running_time_seconds")]
        public double? RunningTimeSeconds { get; set; }

        [JsonPropertyName("edition")]
        public string? Edition { get; set; }

        [JsonPropertyName("content_hash")]
        public string? ContentHash { get; set; }

        [JsonPropertyName("fingerprints")]
        public List<string>? Fingerprints { get; set; }
    }

    private sealed class AssetRow
    {
        public Guid AssetId { get; set; }

        public Guid WorkId { get; set; }

        public Guid EditionId { get; set; }

        public string? Edition { get; set; }

        public string? EditionQid { get; set; }

        public string? ContentHash { get; set; }
    }

    private sealed class BridgeRow
    {
        public Guid EntityId { get; set; }

        public string IdType { get; set; } = string.Empty;

        public string IdValue { get; set; } = string.Empty;
    }

    private sealed class MatchRow
    {
        public Guid AssetId { get; set; }

        public string ServerId { get; set; } = string.Empty;

        public string RecordId { get; set; } = string.Empty;
    }

    private sealed class PrintRow
    {
        public Guid AssetId { get; set; }

        public string Hash { get; set; } = string.Empty;
    }

    private sealed class ChapterRow
    {
        public Guid AssetId { get; set; }

        public long StartMs { get; set; }

        public long? EndMs { get; set; }

        public string? Title { get; set; }
    }
}
