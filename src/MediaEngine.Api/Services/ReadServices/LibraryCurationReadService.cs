using Dapper;
using MediaEngine.Api.Models;
using MediaEngine.Application.ReadModels;
using MediaEngine.Application.Services;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.ReadServices;

/// <summary>
/// Typed read boundary for library curation. It owns GUID-BLOB SQL shape and
/// batches selected items so endpoint handlers do not perform N+1 lookups.
/// </summary>
public sealed class LibraryCurationReadService(IDatabaseConnection db) : ILibraryCurationReadService
{
    private const int MaxParametersPerQuery = 400;

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, Guid>>> ResolveBatchEditTargetsAsync(
        IReadOnlyCollection<Guid> entityIds,
        IReadOnlyCollection<string> fieldKeys,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentNullException.ThrowIfNull(fieldKeys);
        ct.ThrowIfCancellationRequested();

        var ids = entityIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        var keys = fieldKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ids.Length == 0 || keys.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyDictionary<string, Guid>>();
        }

        using var conn = db.CreateConnection();
        var rows = new List<BatchEditLineageRow>();
        foreach (var batch in ids.Chunk(MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            var parameters = new DynamicParameters();
            var values = new string[batch.Length];
            for (var i = 0; i < batch.Length; i++)
            {
                var name = $"entityId{i}";
                values[i] = $"(@{name})";
                parameters.Add(name, GuidSql.ToBlob(batch[i]));
            }

            rows.AddRange(await conn.QueryAsync<BatchEditLineageRow>(new CommandDefinition(
                $"""
                WITH RECURSIVE
                requested(input_id) AS (
                    VALUES {string.Join(", ", values)}
                ),
                requested_work AS (
                    SELECT r.input_id AS RequestedEntityId, w.id AS WorkId,
                           w.media_type AS MediaType
                    FROM requested r
                    JOIN works w ON w.id = r.input_id
                    UNION
                    SELECT r.input_id AS RequestedEntityId, w.id AS WorkId,
                           w.media_type AS MediaType
                    FROM requested r
                    JOIN media_assets ma ON ma.id = r.input_id
                    JOIN editions e ON e.id = ma.edition_id
                    JOIN works w ON w.id = e.work_id
                ),
                descendants(RequestedEntityId, WorkId, CandidateWorkId, Depth) AS (
                    SELECT RequestedEntityId, WorkId, WorkId, 0
                    FROM requested_work
                    UNION ALL
                    SELECT d.RequestedEntityId, d.WorkId, child.id, d.Depth + 1
                    FROM descendants d
                    JOIN works child ON child.parent_work_id = d.CandidateWorkId
                ),
                ranked AS (
                    SELECT rw.RequestedEntityId,
                           rw.WorkId,
                           ma.id AS AssetId,
                           rw.MediaType,
                           COALESCE(gp.id, p.id, rw.WorkId) AS RootParentWorkId,
                           ROW_NUMBER() OVER (
                               PARTITION BY rw.RequestedEntityId
                               ORDER BY d.Depth, ma.id
                           ) AS AssetRank
                    FROM requested_work rw
                    LEFT JOIN works target ON target.id = rw.WorkId
                    LEFT JOIN works p ON p.id = target.parent_work_id
                    LEFT JOIN works gp ON gp.id = p.parent_work_id
                    JOIN descendants d
                      ON d.RequestedEntityId = rw.RequestedEntityId
                     AND d.WorkId = rw.WorkId
                    JOIN editions e ON e.work_id = d.CandidateWorkId
                    JOIN media_assets ma ON ma.edition_id = e.id
                )
                SELECT RequestedEntityId, WorkId, AssetId, MediaType, RootParentWorkId
                FROM ranked
                WHERE AssetRank = 1;
                """,
                parameters,
                cancellationToken: ct)).ConfigureAwait(false));
        }

        var result = new Dictionary<Guid, IReadOnlyDictionary<string, Guid>>();
        foreach (var row in rows)
        {
            var mediaType = Enum.TryParse<MediaType>(row.MediaType, ignoreCase: true, out var parsed)
                ? parsed
                : MediaType.Unknown;
            result[row.RequestedEntityId] = keys.ToDictionary(
                key => key,
                key => ClaimScopeCatalog.IsParentScoped(key, mediaType)
                    ? row.RootParentWorkId
                    : row.AssetId,
                StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    private sealed class BatchEditLineageRow
    {
        public Guid RequestedEntityId { get; init; }
        public Guid WorkId { get; init; }
        public Guid AssetId { get; init; }
        public string MediaType { get; init; } = string.Empty;
        public Guid RootParentWorkId { get; init; }
    }
}
