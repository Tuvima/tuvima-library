using System.Data;
using Dapper;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Collections;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

public sealed class WorkDetailReadService(IDatabaseConnection db) : IWorkDetailReadService
{
    public async Task<WorkDetailDto?> GetAsync(Guid workId, CancellationToken ct = default)
    {
        using var connection = db.CreateConnection();
        workId = WorkRedirects.Resolve(connection, workId);

        var row = await connection.QuerySingleOrDefaultAsync<WorkDetailRow>(new CommandDefinition("""
            SELECT id AS Id,
                   collection_id AS CollectionId,
                   parent_work_id AS ParentWorkId,
                   media_type AS MediaType,
                   work_kind AS WorkKind,
                   ordinal AS Ordinal,
                   is_catalog_only AS IsCatalogOnly,
                   wikidata_qid AS WikidataQid
            FROM works
            WHERE id = @workId
            LIMIT 1;
            """, new { workId }, cancellationToken: ct));

        if (row is null)
        {
            return null;
        }

        var work = new WorkDetailDto
        {
            Id = row.Id,
            CollectionId = row.CollectionId,
            ParentWorkId = row.ParentWorkId,
            MediaType = row.MediaType,
            WorkKind = row.WorkKind,
            Ordinal = row.Ordinal,
            IsCatalogOnly = row.IsCatalogOnly,
            WikidataQid = row.WikidataQid,
            CanonicalValues = await LoadCanonicalValuesAsync(connection, [workId], ct),
        };

        var editionRows = (await connection.QueryAsync<EditionRow>(new CommandDefinition("""
            SELECT id AS Id,
                   work_id AS WorkId,
                   format_label AS FormatLabel,
                   wikidata_qid AS WikidataQid
            FROM editions
            WHERE work_id = @workId
            ORDER BY COALESCE(format_label, ''), id;
            """, new { workId }, cancellationToken: ct))).ToList();

        List<EditionAssetRow> assetRows = editionRows.Count == 0
            ? []
            : (await connection.QueryAsync<EditionAssetRow>(new CommandDefinition("""
                SELECT id AS Id,
                       edition_id AS EditionId,
                       file_path_root AS FilePathRoot,
                       status AS Status
                FROM media_assets
                WHERE edition_id IN @editionIds
                  AND status = 'Normal'
                  AND is_orphaned = 0
                ORDER BY file_path_root;
                """, new { editionIds = editionRows.Select(edition => GuidSql.ToBlob(edition.Id)).ToArray() }, cancellationToken: ct))).ToList();

        var canonicalEntityIds = editionRows.Select(edition => edition.Id)
            .Concat(assetRows.Select(asset => asset.Id))
            .Distinct()
            .ToArray();
        var canonicalByEntity = await LoadCanonicalValuesByEntityAsync(connection, canonicalEntityIds, ct);
        var assetsByEdition = assetRows
            .GroupBy(asset => asset.EditionId)
            .ToDictionary(group => group.Key, group => group.Select(asset => new EditionAssetDto
            {
                Id = asset.Id,
                EditionId = asset.EditionId,
                FilePathRoot = asset.FilePathRoot,
                Status = asset.Status,
                CanonicalValues = canonicalByEntity.GetValueOrDefault(asset.Id) ?? [],
            }).ToList());

        work.Editions.AddRange(editionRows.Select(edition => new EditionDto
        {
            Id = edition.Id,
            WorkId = edition.WorkId,
            FormatLabel = edition.FormatLabel,
            WikidataQid = edition.WikidataQid,
            CanonicalValues = canonicalByEntity.GetValueOrDefault(edition.Id) ?? [],
            Assets = assetsByEdition.GetValueOrDefault(edition.Id) ?? [],
        }));

        return work;
    }

    private static async Task<List<CanonicalValueDto>> LoadCanonicalValuesAsync(
        IDbConnection connection,
        IReadOnlyCollection<Guid> entityIds,
        CancellationToken ct)
    {
        var values = await LoadCanonicalValuesByEntityAsync(connection, entityIds, ct);
        return values.SelectMany(pair => pair.Value).ToList();
    }

    private static async Task<Dictionary<Guid, List<CanonicalValueDto>>> LoadCanonicalValuesByEntityAsync(
        IDbConnection connection,
        IReadOnlyCollection<Guid> entityIds,
        CancellationToken ct)
    {
        if (entityIds.Count == 0)
        {
            return [];
        }

        var rows = await connection.QueryAsync<CanonicalValueRow>(new CommandDefinition("""
            SELECT entity_id AS EntityId,
                   key AS Key,
                   value AS Value,
                   winning_provider_id AS WinningProviderId,
                   last_scored_at AS LastScoredAt
            FROM canonical_values
            WHERE entity_id IN @entityIds
            ORDER BY key;
            """, new { entityIds = entityIds.Select(GuidSql.ToBlob).ToArray() }, cancellationToken: ct));

        return rows
            .GroupBy(row => row.EntityId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => new CanonicalValueDto
                {
                    Key = row.Key,
                    Value = row.Value,
                    WinningProviderId = row.WinningProviderId?.ToString("D"),
                    LastScoredAt = row.LastScoredAt,
                }).ToList());
    }

    private sealed class WorkDetailRow
    {
        public Guid Id { get; init; }
        public Guid? CollectionId { get; init; }
        public Guid? ParentWorkId { get; init; }
        public string MediaType { get; init; } = string.Empty;
        public string WorkKind { get; init; } = string.Empty;
        public int? Ordinal { get; init; }
        public bool IsCatalogOnly { get; init; }
        public string? WikidataQid { get; init; }
    }

    private sealed class EditionRow
    {
        public Guid Id { get; init; }
        public Guid WorkId { get; init; }
        public string? FormatLabel { get; init; }
        public string? WikidataQid { get; init; }
    }

    private sealed class EditionAssetRow
    {
        public Guid Id { get; init; }
        public Guid EditionId { get; init; }
        public string FilePathRoot { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
    }

    private sealed class CanonicalValueRow
    {
        public Guid EntityId { get; init; }
        public string Key { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
        public Guid? WinningProviderId { get; init; }
        public DateTimeOffset LastScoredAt { get; init; }
    }
}
