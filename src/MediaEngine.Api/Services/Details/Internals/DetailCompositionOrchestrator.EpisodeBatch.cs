using Dapper;
using MediaEngine.Domain;
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
        CancellationToken ct,
        IReadOnlyCollection<string>? onlyKeys = null)
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

            if (onlyKeys is not null)
            {
                // A key-restricted read never needs the edition subtitle.
                continue;
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

            if (onlyKeys is not null)
            {
                // Same rows as the full read below, narrowed to the requested keys (each key resolves independently).
                var keys = onlyKeys.Select(key => key.ToLowerInvariant()).ToArray();
                Collect(scalarsByEntity, await conn.QueryAsync<EntityPairRow>(new CommandDefinition(
                    "SELECT entity_id AS EntityId, key AS Key, value AS Value FROM canonical_values WHERE entity_id IN @entityIds AND lower(key) IN @keys ORDER BY entity_id, key;",
                    new { entityIds = entityBlobs, keys },
                    cancellationToken: ct)));
                Collect(arraysByEntity, await conn.QueryAsync<EntityPairRow>(new CommandDefinition(
                    "SELECT entity_id AS EntityId, key AS Key, value AS Value FROM canonical_value_arrays WHERE entity_id IN @entityIds AND lower(key) IN @keys ORDER BY entity_id, key, ordinal;",
                    new { entityIds = entityBlobs, keys },
                    cancellationToken: ct)));
                continue;
            }

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

    private static readonly string[] EpisodeAirDateKeys = ["air_date", "release_date"];

    /// <summary>
    /// Gives every episode its air date (what the full per-episode read would have set), so show-level air years and
    /// ordering do not depend on how many episodes the response goes on to describe in full.
    /// </summary>
    private async Task<IReadOnlyList<CollectionWorkSummary>> ApplyTvEpisodeAirDatesAsync(
        IReadOnlyList<CollectionWorkSummary> works,
        CancellationToken ct)
    {
        var maps = await LoadWorkAndAssetCanonicalMapsAsync(
            works.Select(work => Guid.Parse(work.Id)).ToList(),
            ct,
            EpisodeAirDateKeys);
        return works.Select(work =>
        {
            var values = maps[Guid.Parse(work.Id)];
            return work with { Year = FirstText(GetValue(values, "air_date"), GetValue(values, "release_date")) };
        }).ToList();
    }

    /// <summary>
    /// Reads description, still and artwork for just <paramref name="episodeIds"/> (skipping any already read) and
    /// lays them over the matching entries of <paramref name="works"/>. Everything else about an entry is kept, so it
    /// can be applied after manifest placeholders were merged in.
    /// </summary>
    private async Task<(IReadOnlyList<CollectionWorkSummary> Works, Dictionary<Guid, Dictionary<string, string>> ValueMaps)> EnrichTvEpisodeSubsetAsync(
        IReadOnlyList<CollectionWorkSummary> works,
        IReadOnlyCollection<Guid> episodeIds,
        HashSet<Guid> alreadyEnriched,
        CancellationToken ct)
    {
        var pending = episodeIds.Where(id => !alreadyEnriched.Contains(id)).Distinct().ToHashSet();
        if (pending.Count == 0)
        {
            return (works, new Dictionary<Guid, Dictionary<string, string>>());
        }

        var subset = works
            .Where(work => Guid.TryParse(work.Id, out var id) && pending.Contains(id))
            .ToList();
        var stills = await LoadTvEpisodeStillsAsync(subset.Select(work => Guid.Parse(work.Id)).ToList(), ct);
        var (enrichedSubset, valueMaps) = await EnrichTvEpisodesAsync(subset, stills, ct);
        var enrichedById = enrichedSubset.ToDictionary(work => work.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var work in subset)
        {
            alreadyEnriched.Add(Guid.Parse(work.Id));
        }

        var merged = works.Select(work => enrichedById.TryGetValue(work.Id, out var enriched)
            ? work with
            {
                Description = enriched.Description,
                BackgroundUrl = enriched.BackgroundUrl,
                EpisodeStillUrl = enriched.EpisodeStillUrl,
                EpisodeStillWidthPx = enriched.EpisodeStillWidthPx,
                EpisodeStillHeightPx = enriched.EpisodeStillHeightPx,
            }
            : work).ToList();
        return (merged, valueMaps);
    }

    private async Task<Dictionary<Guid, SequenceRow>> LoadTvEpisodeStillsAsync(
        IReadOnlyCollection<Guid> episodeIds,
        CancellationToken ct)
    {
        var result = new Dictionary<Guid, SequenceRow>();
        if (episodeIds.Count == 0)
        {
            return result;
        }

        using var conn = _db.CreateConnection();
        foreach (var chunk in episodeIds.Chunk(BatchChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            var ownedIds = chunk.Select(GuidSql.ToBlob).ToArray();
            var rows = await conn.QueryAsync<SequenceRow>(new CommandDefinition("""
                -- readiness: detail page of one item opened by id
                SELECT w.id AS WorkId, art.id AS EpisodeStillAssetId,
                       art.width_px AS EpisodeStillWidthPx, art.height_px AS EpisodeStillHeightPx
                FROM works w LEFT JOIN entity_assets art ON art.id = (
                    SELECT ea.id FROM entity_assets ea
                    WHERE ea.asset_type = 'EpisodeStill'
                      AND (ea.entity_id = w.id OR ea.entity_id IN (SELECT e.id FROM editions e WHERE e.work_id = w.id)
                           OR ea.entity_id IN (SELECT ma.id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = w.id))
                      AND (ea.local_image_path_s IS NOT NULL OR ea.local_image_path IS NOT NULL)
                    ORDER BY ea.is_preferred DESC, ea.is_user_override DESC, ea.updated_at DESC LIMIT 1)
                WHERE w.id IN @ownedIds
                """, new { ownedIds }, cancellationToken: ct));
            foreach (var row in rows)
            {
                result[row.WorkId] = row;
            }
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
                "-- readiness: detail page of one item opened by id\nSELECT id AS WorkId, display_overrides_json AS Json FROM works WHERE id IN @workIds;",
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
