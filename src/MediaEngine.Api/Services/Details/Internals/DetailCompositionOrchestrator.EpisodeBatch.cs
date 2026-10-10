using Dapper;
using MediaEngine.Domain.Constants;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.Details.Internals;

internal sealed partial class DetailCompositionOrchestrator
{
    /// <summary>Keeps IN lists well below SQLite's variable ceiling (same bound as the other batch readers).</summary>
    private const int BatchChunkSize = 400;

    private static readonly string[] TechnicalClaimKeys = ["duration_sec", "duration_seconds", "genre"];

    /// <summary>
    /// Batched form of <see cref="LoadWorkAndAssetCanonicalMapAsync"/> (no asset filter): returns exactly the same
    /// map per work, but reads every work, edition and asset in a handful of statements instead of about eight each.
    /// Every requested work has an entry, even when it has no values.
    /// </summary>
    internal async Task<Dictionary<Guid, Dictionary<string, string>>> LoadWorkAndAssetCanonicalMapsAsync(
        IReadOnlyCollection<Guid> workIds,
        CancellationToken ct)
    {
        var distinctWorkIds = workIds.Distinct().ToList();
        var result = new Dictionary<Guid, Dictionary<string, string>>(distinctWorkIds.Count);
        if (distinctWorkIds.Count == 0)
        {
            return result;
        }

        using var conn = _db.CreateConnection();
        var assetsByWork = new Dictionary<Guid, List<Guid>>();
        var editionSubtitleByWork = new Dictionary<Guid, string?>();
        foreach (var chunk in distinctWorkIds.Chunk(BatchChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            var workBlobs = chunk.Select(GuidSql.ToBlob).ToArray();

            // Same assets, in the same order, the single-work reader sees (blob order within each work).
            var assetRows = await conn.QueryAsync<WorkAssetRow>(new CommandDefinition(
                """
                SELECT e.work_id AS WorkId, ma.id AS AssetId
                FROM media_assets ma
                INNER JOIN editions e ON e.id = ma.edition_id
                WHERE e.work_id IN @workIds
                  AND ma.status = 'Normal'
                ORDER BY e.work_id, ma.id;
                """,
                new { workIds = workBlobs },
                cancellationToken: ct));
            foreach (var row in assetRows)
            {
                if (!assetsByWork.TryGetValue(row.WorkId, out var assets))
                {
                    assetsByWork[row.WorkId] = assets = [];
                }

                if (!assets.Contains(row.AssetId))
                {
                    assets.Add(row.AssetId);
                }
            }

            // Only the first edition (by id) of a work can supply the subtitle, even when its value is blank.
            var subtitleRows = await conn.QueryAsync<WorkEditionSubtitleRow>(new CommandDefinition(
                """
                SELECT e.work_id AS WorkId, NULLIF(CAST(cv.value AS TEXT), '') AS Subtitle
                FROM editions e
                INNER JOIN canonical_values cv ON cv.entity_id = e.id
                WHERE e.work_id IN @workIds AND cv.key = @subtitle
                ORDER BY e.work_id, e.id;
                """,
                new { workIds = workBlobs, subtitle = MetadataFieldConstants.Subtitle },
                cancellationToken: ct));
            foreach (var row in subtitleRows)
            {
                editionSubtitleByWork.TryAdd(row.WorkId, row.Subtitle);
            }
        }

        var entityIds = distinctWorkIds
            .Concat(assetsByWork.Values.SelectMany(assets => assets))
            .Distinct()
            .ToList();
        var scalarsByEntity = new Dictionary<Guid, List<CanonicalPair>>();
        var arraysByEntity = new Dictionary<Guid, List<CanonicalPair>>();
        var claimsByEntity = new Dictionary<Guid, List<CanonicalPair>>();
        foreach (var chunk in entityIds.Chunk(BatchChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            var entityBlobs = chunk.Select(GuidSql.ToBlob).ToArray();

            Collect(scalarsByEntity, await conn.QueryAsync<EntityPairRow>(new CommandDefinition(
                "SELECT entity_id AS EntityId, key AS Key, value AS Value FROM canonical_values WHERE entity_id IN @entityIds ORDER BY entity_id, key;",
                new { entityIds = entityBlobs },
                cancellationToken: ct)));
            Collect(arraysByEntity, await conn.QueryAsync<EntityPairRow>(new CommandDefinition(
                "SELECT entity_id AS EntityId, key AS Key, value AS Value FROM canonical_value_arrays WHERE entity_id IN @entityIds ORDER BY entity_id, key, ordinal;",
                new { entityIds = entityBlobs },
                cancellationToken: ct)));
            Collect(claimsByEntity, await conn.QueryAsync<EntityPairRow>(new CommandDefinition(
                """
                SELECT entity_id AS EntityId, claim_key AS Key, claim_value AS Value
                FROM metadata_claims
                WHERE entity_id IN @entityIds
                  AND claim_key IN @claimKeys
                  AND NULLIF(CAST(claim_value AS TEXT), '') IS NOT NULL
                ORDER BY entity_id, confidence DESC, claimed_at DESC, id;
                """,
                new { entityIds = entityBlobs, claimKeys = TechnicalClaimKeys },
                cancellationToken: ct)));
        }

        Dictionary<string, string> CanonicalMapFor(Guid entityId) => BuildCanonicalMap(
            scalarsByEntity.GetValueOrDefault(entityId) ?? [],
            arraysByEntity.GetValueOrDefault(entityId) ?? []);

        foreach (var workId in distinctWorkIds)
        {
            // Mirrors LoadWorkAndAssetCanonicalMapAsync step for step.
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assetId in assetsByWork.GetValueOrDefault(workId) ?? [])
            {
                foreach (var (key, value) in CanonicalMapFor(assetId))
                {
                    values.TryAdd(key, value);
                }

                ApplyTechnicalClaimFallbacks(claimsByEntity.GetValueOrDefault(assetId) ?? [], values);
            }

            if (editionSubtitleByWork.GetValueOrDefault(workId) is { } editionSubtitle
                && !string.IsNullOrWhiteSpace(editionSubtitle))
            {
                values[$"edition_{MetadataFieldConstants.Subtitle}"] = editionSubtitle;
            }

            foreach (var (key, value) in CanonicalMapFor(workId))
            {
                values[key] = value;
            }

            ApplyTechnicalClaimFallbacks(claimsByEntity.GetValueOrDefault(workId) ?? [], values);
            result[workId] = values;
        }

        return result;
    }

    /// <summary>Batched form of <see cref="LoadWorkDisplayOverridesAsync"/>; every requested work has an entry.</summary>
    internal async Task<Dictionary<Guid, Dictionary<string, string>>> LoadWorkDisplayOverridesBatchAsync(
        IReadOnlyCollection<Guid> workIds,
        CancellationToken ct)
    {
        var distinctWorkIds = workIds.Distinct().ToList();
        var result = new Dictionary<Guid, Dictionary<string, string>>(distinctWorkIds.Count);
        foreach (var workId in distinctWorkIds)
        {
            result[workId] = ParseDisplayOverrides(null);
        }

        if (distinctWorkIds.Count == 0)
        {
            return result;
        }

        using var conn = _db.CreateConnection();
        foreach (var chunk in distinctWorkIds.Chunk(BatchChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            var rows = await conn.QueryAsync<WorkOverridesRow>(new CommandDefinition(
                "SELECT id AS WorkId, display_overrides_json AS Json FROM works WHERE id IN @workIds;",
                new { workIds = chunk.Select(GuidSql.ToBlob).ToArray() },
                cancellationToken: ct));
            foreach (var row in rows)
            {
                result[row.WorkId] = ParseDisplayOverrides(row.Json);
            }
        }

        return result;
    }

    private static void Collect(Dictionary<Guid, List<CanonicalPair>> target, IEnumerable<EntityPairRow> rows)
    {
        foreach (var row in rows)
        {
            if (!target.TryGetValue(row.EntityId, out var list))
            {
                target[row.EntityId] = list = [];
            }

            list.Add(new CanonicalPair(row.Key, row.Value));
        }
    }

    private sealed class WorkAssetRow
    {
        public Guid WorkId { get; init; }
        public Guid AssetId { get; init; }
    }

    private sealed class WorkEditionSubtitleRow
    {
        public Guid WorkId { get; init; }
        public string? Subtitle { get; init; }
    }

    private sealed class WorkOverridesRow
    {
        public Guid WorkId { get; init; }
        public string? Json { get; init; }
    }

    private sealed class EntityPairRow
    {
        public Guid EntityId { get; init; }
        public string Key { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
    }
}
