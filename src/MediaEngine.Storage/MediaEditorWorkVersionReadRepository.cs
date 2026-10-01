using Dapper;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public sealed class MediaEditorWorkVersionReadRepository(IDatabaseConnection db)
{
    private const int MaximumAssets = 200;

    public Task<MediaEditorWorkVersionSelectorDto?> GetAsync(Guid launchEntityId, CancellationToken ct) =>
        db.ExecuteReadAsync<MediaEditorWorkVersionSelectorDto?>((connection, transaction, token) =>
        {
            var launch = connection.QuerySingleOrDefault<LaunchRow>(new CommandDefinition("""
                SELECT w.id AS WorkId, 'Work' AS EntityType FROM works w WHERE w.id = @id
                UNION ALL
                SELECT e.work_id AS WorkId, 'Edition' AS EntityType FROM editions e WHERE e.id = @id
                UNION ALL
                SELECT e.work_id AS WorkId, 'Asset' AS EntityType
                FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE ma.id = @id
                LIMIT 1;
                """, new { id = GuidSql.ToBlob(launchEntityId) }, transaction, cancellationToken: ct));
            if (launch is null) return null;

            var workTitle = connection.QuerySingleOrDefault<string>(new CommandDefinition("""
                SELECT COALESCE((SELECT value FROM canonical_values WHERE entity_id = w.id AND key = 'title' LIMIT 1), '')
                FROM works w WHERE w.id = @workId;
                """, new { workId = GuidSql.ToBlob(launch.WorkId) }, transaction, cancellationToken: ct)) ?? string.Empty;
            var rows = connection.Query<Row>(new CommandDefinition("""
                SELECT e.id AS EditionId, NULLIF(e.format_label, '') AS EditionLabel,
                       ma.id AS AssetId, ma.file_path_root AS FilePath,
                       COUNT(*) OVER (PARTITION BY e.id) AS EditionAssetCount,
                       (SELECT COUNT(*) FROM editions sibling WHERE sibling.work_id = e.work_id AND EXISTS (
                           SELECT 1 FROM media_assets sibling_asset WHERE sibling_asset.edition_id = sibling.id
                           AND sibling_asset.status = 'Normal' AND sibling_asset.is_orphaned = 0)) AS WorkEditionCount,
                       CASE WHEN e.wikidata_qid IS NOT NULL OR EXISTS (
                           SELECT 1 FROM canonical_values cv WHERE cv.entity_id = e.id
                           AND cv.key IN ('isbn','asin','publisher','edition_release_date','edition_release_year',
                                          'edition_title','edition_label','musicbrainz_release_id','narrator'))
                           OR EXISTS (SELECT 1 FROM entity_artwork_links l WHERE l.entity_id = e.id AND l.entity_type = 'Edition')
                           THEN 1 ELSE 0 END AS HasMeaningfulEvidence
                FROM editions e JOIN media_assets ma ON ma.edition_id = e.id
                WHERE e.work_id = @workId AND ma.status = 'Normal' AND ma.is_orphaned = 0
                ORDER BY lower(COALESCE(e.format_label, '')), e.id, lower(ma.file_path_root), ma.id
                LIMIT @limit;
                """, new { workId = GuidSql.ToBlob(launch.WorkId), limit = MaximumAssets + 1 }, transaction, cancellationToken: ct)).ToList();

            var visible = rows.Take(MaximumAssets).ToList();
            return new MediaEditorWorkVersionSelectorDto
            {
                WorkId = launch.WorkId,
                WorkTitle = workTitle,
                SelectedEntityId = launchEntityId,
                SelectedEntityType = launch.EntityType,
                IsTruncated = rows.Count > MaximumAssets,
                Editions = visible.GroupBy(row => row.EditionId).Select(group =>
                {
                    var first = group.First();
                    return new MediaEditorEditionSelectorDto
                    {
                        EditionId = first.EditionId,
                        Label = first.EditionLabel,
                        AssetCount = first.EditionAssetCount,
                        Collapse = first.WorkEditionCount == 1 && first.EditionAssetCount == 1 && !first.HasMeaningfulEvidence,
                        Assets = group.Select(row => new MediaEditorAssetSelectorDto
                        {
                            AssetId = row.AssetId,
                            EditionId = row.EditionId,
                            FileName = Path.GetFileName(row.FilePath),
                            TechnicalLabel = BuildTechnicalLabel(row.FilePath, row.EditionLabel),
                        }).ToList(),
                    };
                }).ToList(),
            };
        }, ct);

    private static string BuildTechnicalLabel(string path, string? persistedFormatLabel)
    {
        var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(persistedFormatLabel)) return extension;
        if (string.IsNullOrWhiteSpace(extension)
            || string.Equals(persistedFormatLabel, extension, StringComparison.OrdinalIgnoreCase))
            return persistedFormatLabel;
        return $"{persistedFormatLabel} · {extension}";
    }

    private sealed class LaunchRow { public Guid WorkId { get; init; } public string EntityType { get; init; } = string.Empty; }
    private sealed class Row
    {
        public Guid EditionId { get; init; }
        public string? EditionLabel { get; init; }
        public Guid AssetId { get; init; }
        public string FilePath { get; init; } = string.Empty;
        public int EditionAssetCount { get; init; }
        public int WorkEditionCount { get; init; }
        public bool HasMeaningfulEvidence { get; init; }
    }
}
