using Dapper;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

public sealed record MediaEditorSelectionHistoryEntry(
    string Id, Guid EntityId, DateTimeOffset OccurredAt, string EventType,
    string Label, string? Detail, string Category, string ActorLabel,
    string Scope, IReadOnlyList<Guid> SelectedAssetIds);

public sealed record MediaEditorSelectionHistoryResult(
    Guid ParentEntityId, IReadOnlyList<Guid> SelectedAssetIds,
    IReadOnlyList<MediaEditorSelectionHistoryEntry> Items);

/// <summary>
/// Reads events for a caller-authorized, bounded set of actual owned files.
/// This service performs membership checks; the HTTP boundary must authorize
/// each selected asset before and after this read.
/// </summary>
public sealed class MediaEditorSelectionHistoryReadService(IDatabaseConnection database)
{
    public Task<MediaEditorSelectionHistoryResult?> ReadAsync(Guid parentEntityId,
        IReadOnlyList<Guid> selectedAssetIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(selectedAssetIds);
        if (selectedAssetIds.Count is < 1 or > 1000
            || selectedAssetIds.Any(id => id == Guid.Empty)
            || selectedAssetIds.Distinct().Count() != selectedAssetIds.Count)
        {
            throw new ArgumentException("History requires 1 to 1,000 distinct owned files.", nameof(selectedAssetIds));
        }

        return database.ExecuteReadAsync<MediaEditorSelectionHistoryResult?>(
            (connection, transaction, token) =>
        {
            var routeWorkId = connection.QueryFirstOrDefault<Guid?>("""
            SELECT id FROM works WHERE id=@parentEntityId
            UNION ALL
            SELECT e.work_id FROM media_assets a JOIN editions e ON e.id=a.edition_id
            WHERE a.id=@parentEntityId LIMIT 1;
            """, new { parentEntityId }, transaction);
            var routeCollection = connection.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM collections WHERE id=@parentEntityId;
            """, new { parentEntityId }, transaction) > 0;
            if (routeWorkId is null && !routeCollection)
            {
                return null;
            }

            var lineage = new List<LineageRow>(selectedAssetIds.Count * 3);
            foreach (var batch in selectedAssetIds.Chunk(350))
            {
                ct.ThrowIfCancellationRequested();
                lineage.AddRange(connection.Query<LineageRow>(new CommandDefinition("""
                WITH RECURSIVE lineage AS (
                    SELECT a.id AS AssetId, e.id AS EditionId,
                           w.id AS WorkId, w.parent_work_id AS ParentWorkId,
                           w.collection_id AS CollectionId, 0 AS Depth
                    FROM media_assets a JOIN editions e ON e.id=a.edition_id
                    JOIN works w ON w.id=e.work_id
                    WHERE a.id IN @assetIds AND a.status='Normal'
                      AND a.is_orphaned=0 AND w.ownership='Owned'
                    UNION ALL
                    SELECT lineage.AssetId, lineage.EditionId,
                           parent.id, parent.parent_work_id,
                           parent.collection_id, lineage.Depth+1
                    FROM lineage JOIN works parent ON parent.id=lineage.ParentWorkId
                    WHERE lineage.Depth < 8
                ) SELECT * FROM lineage;
                """, new { assetIds = batch.Select(GuidSql.ToBlob).ToArray() },
                    transaction, cancellationToken: ct)));
            }
            var byAsset = lineage.GroupBy(row => row.AssetId).ToDictionary(group => group.Key);
            if (byAsset.Count != selectedAssetIds.Count
                || selectedAssetIds.Any(id => !byAsset.TryGetValue(id, out var rows)
                    || !rows.Any(row => routeWorkId.HasValue
                        ? row.WorkId == routeWorkId.Value
                        : row.CollectionId == parentEntityId)))
            {
                return null;
            }

            var related = new Dictionary<Guid, RelatedEntity>();
            foreach (var assetId in selectedAssetIds)
            {
                var rows = byAsset[assetId];
                var first = rows.First(row => row.Depth == 0);
                AddRelated(related, assetId, "file", assetId);
                AddRelated(related, first.EditionId, "edition", assetId);
                foreach (var row in rows)
                {
                    AddRelated(related, row.WorkId, row.Depth == 0 ? "work" : "parent", assetId);
                }
            }

            var activity = new List<ActivityRow>();
            foreach (var batch in related.Keys.Chunk(350))
            {
                ct.ThrowIfCancellationRequested();
                activity.AddRange(connection.Query<ActivityRow>(new CommandDefinition("""
                SELECT id AS Id, occurred_at AS OccurredAt,
                       action_type AS EventType, entity_id AS EntityId,
                       profile_id AS ProfileId, detail AS Detail
                FROM system_activity WHERE entity_id IN @entityIds
                ORDER BY occurred_at DESC, id DESC LIMIT 200;
                """, new { entityIds = batch.Select(GuidSql.ToBlob).ToArray() },
                    transaction, cancellationToken: ct)));
            }
            var events = activity.DistinctBy(row => row.Id)
                .Where(row => row.EntityId is { } entityId && related.ContainsKey(entityId))
                .Select(row =>
                {
                    var entity = related[row.EntityId!.Value];
                    return new MediaEditorSelectionHistoryEntry(
                        row.Id.ToString(), row.EntityId.Value, row.OccurredAt,
                        row.EventType, Humanize(row.EventType),
                        // A shared Work/Edition event can mention an unselected
                        // sibling in its free-form detail; expose its occurrence
                        // without copying that text into a file-scoped review.
                        entity.Scope == "file" ? row.Detail : null,
                        Category(row.EventType), row.ProfileId.HasValue ? "Profile" : "System",
                        entity.Scope, entity.AssetIds.Order().ToArray());
                }).ToList();

            // TV pairing is a durable editor action even when no system_activity
            // event has yet been emitted for the file.
            foreach (var batch in selectedAssetIds.Chunk(350))
            {
                ct.ThrowIfCancellationRequested();
                var receipts = connection.Query<PairingReceiptRow>(new CommandDefinition("""
                SELECT item.asset_id AS AssetId, item.source_work_id AS SourceWorkId,
                       item.target_work_id AS TargetWorkId,
                       receipt.operation_token AS OperationToken,
                       receipt.committed_at AS CommittedAt
                FROM media_editor_commit_items item
                JOIN media_editor_commits receipt ON receipt.operation_token=item.operation_token
                WHERE item.asset_id IN @assetIds
                ORDER BY receipt.committed_at DESC LIMIT 200;
                """, new { assetIds = batch.Select(GuidSql.ToBlob).ToArray() },
                    transaction, cancellationToken: ct));
                events.AddRange(receipts.Select(row => new MediaEditorSelectionHistoryEntry(
                    $"pairing:{row.OperationToken}:{row.AssetId:D}", row.AssetId, row.CommittedAt,
                    "MediaEditorPairingCommitted", "Episode match saved",
                    "A reviewed episode match was saved. File synchronization may still be pending.",
                    "match", "Editor", "file", [row.AssetId])));
            }

            return new(parentEntityId, selectedAssetIds.Order().ToArray(),
                events.OrderByDescending(row => row.OccurredAt).ThenBy(row => row.Id)
                    .Take(200).ToArray());
        }, ct);
    }

    private static void AddRelated(Dictionary<Guid, RelatedEntity> related,
        Guid entityId, string scope, Guid assetId)
    {
        if (!related.TryGetValue(entityId, out var entry))
        {
            related[entityId] = entry = new(scope, []);
        }
        entry.AssetIds.Add(assetId);
    }

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Activity";
        }
        var text = value.Replace('_', ' ').Replace('-', ' ');
        return System.Text.RegularExpressions.Regex.Replace(text, "(?<=[a-z])(?=[A-Z])", " ").Trim();
    }

    private static string Category(string value) => value.ToLowerInvariant() switch
    {
        var text when text.Contains("artwork") || text.Contains("cover") => "artwork",
        var text when text.Contains("match") || text.Contains("identif") => "match",
        var text when text.Contains("ingest") || text.Contains("scan") => "ingestion",
        _ => "metadata",
    };

    private sealed record RelatedEntity(string Scope, HashSet<Guid> AssetIds);
    private sealed class LineageRow
    {
        public Guid AssetId { get; set; }
        public Guid EditionId { get; set; }
        public Guid WorkId { get; set; }
        public Guid? ParentWorkId { get; set; }
        public Guid? CollectionId { get; set; }
        public int Depth { get; set; }
    }
    private sealed class ActivityRow
    {
        public long Id { get; set; }
        public DateTimeOffset OccurredAt { get; set; }
        public string EventType { get; set; } = "";
        public Guid? EntityId { get; set; }
        public Guid? ProfileId { get; set; }
        public string? Detail { get; set; }
    }
    private sealed class PairingReceiptRow
    {
        public Guid AssetId { get; set; }
        public Guid SourceWorkId { get; set; }
        public Guid TargetWorkId { get; set; }
        public string OperationToken { get; set; } = "";
        public DateTimeOffset CommittedAt { get; set; }
    }
}
