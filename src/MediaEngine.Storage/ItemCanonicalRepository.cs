using System.Text.Json;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Storage;

/// <summary>
/// Typed persistence boundary for item canonical editing. Endpoints own HTTP and
/// orchestration concerns; this service owns SQLite shape, GUID BLOB parameters,
/// transactions, and cancellation-aware commands.
/// </summary>
public sealed class ItemCanonicalRepository(
    IDatabaseConnection db,
    ILogger<ItemCanonicalRepository> logger) : IItemCanonicalRepository
{
    private sealed record DisplayOverrideRow(string? Json);
    private sealed record WikidataStateRow(
        string? Qid,
        string? Status,
        string? Source,
        long Locked,
        string? RejectedQidsJson);

    public Task<ItemCanonicalWorkAssetContext?> ResolveWorkAssetContextAsync(
        Guid entityId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH target AS (
                SELECT
                    ma.id AS asset_id,
                    w.id AS work_id,
                    COALESCE(NULLIF(w.media_type, ''), '') AS work_media_type
                FROM media_assets ma
                JOIN editions e ON e.id = ma.edition_id
                JOIN works w ON w.id = e.work_id
                WHERE ma.id = @entityId

                UNION ALL

                SELECT
                    COALESCE(ma.id, child_ma.id, grandchild_ma.id) AS asset_id,
                    w.id AS work_id,
                    COALESCE(NULLIF(w.media_type, ''), '') AS work_media_type
                FROM works w
                LEFT JOIN editions e ON e.work_id = w.id
                LEFT JOIN media_assets ma ON ma.edition_id = e.id
                LEFT JOIN works child ON child.parent_work_id = w.id
                LEFT JOIN editions child_e ON child_e.work_id = child.id
                LEFT JOIN media_assets child_ma ON child_ma.edition_id = child_e.id
                LEFT JOIN works grandchild ON grandchild.parent_work_id = child.id
                LEFT JOIN editions grandchild_e ON grandchild_e.work_id = grandchild.id
                LEFT JOIN media_assets grandchild_ma ON grandchild_ma.edition_id = grandchild_e.id
                WHERE w.id = @entityId
            )
            SELECT
                t.asset_id,
                COALESCE(NULLIF(t.work_media_type, ''), MAX(CASE WHEN cv.key = 'media_type' THEN cv.value END), ''),
                COALESCE(MAX(CASE WHEN cv.key = 'title' THEN cv.value END), ''),
                COALESCE(
                    (SELECT value
                     FROM canonical_value_arrays
                     WHERE entity_id IN (t.work_id, t.asset_id)
                       AND key IN ('author', 'album_artist', 'artist', 'director')
                     ORDER BY CASE key
                         WHEN 'author' THEN 0
                         WHEN 'album_artist' THEN 1
                         WHEN 'artist' THEN 2
                         ELSE 3
                     END, ordinal
                     LIMIT 1),
                    ''),
                COALESCE(MAX(CASE WHEN cv.key = 'year' THEN cv.value END), '')
            FROM target t
            LEFT JOIN canonical_values cv ON cv.entity_id = t.asset_id
            WHERE t.asset_id IS NOT NULL
            GROUP BY t.asset_id, t.work_media_type
            ORDER BY CASE WHEN t.asset_id = @entityId THEN 0 ELSE 1 END, t.asset_id
            LIMIT 1;
            """;
        cmd.Parameters.Add("@entityId", SqliteType.Blob).Value = GuidSql.ToBlob(entityId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return Task.FromResult<ItemCanonicalWorkAssetContext?>(null);
        }

        ItemCanonicalWorkAssetContext result = new(
            GuidSql.FromDb(reader.GetValue(0)),
            reader.IsDBNull(1) ? Domain.Enums.MediaType.Unknown.ToString() : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
        return Task.FromResult<ItemCanonicalWorkAssetContext?>(result);
    }

    public Task<ItemCanonicalDisplayOverrideState> LoadDisplayOverridesAsync(
        Guid workId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var row = conn.QuerySingleOrDefault<DisplayOverrideRow>(new CommandDefinition(
            "SELECT display_overrides_json AS Json FROM works WHERE id = @workId LIMIT 1;",
            new { workId = GuidSql.ToBlob(workId) },
            cancellationToken: ct));

        if (row is null)
        {
            return Task.FromResult(new ItemCanonicalDisplayOverrideState(false, new(StringComparer.OrdinalIgnoreCase)));
        }

        if (string.IsNullOrWhiteSpace(row.Json))
        {
            return Task.FromResult(new ItemCanonicalDisplayOverrideState(true, new(StringComparer.OrdinalIgnoreCase)));
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(row.Json);
            return Task.FromResult(new ItemCanonicalDisplayOverrideState(
                true,
                parsed is null
                    ? new(StringComparer.OrdinalIgnoreCase)
                    : new(parsed, StringComparer.OrdinalIgnoreCase)));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Ignoring malformed display override JSON for work {WorkId}.", workId);
            return Task.FromResult(new ItemCanonicalDisplayOverrideState(true, new(StringComparer.OrdinalIgnoreCase)));
        }
    }

    public async Task<bool> SaveDisplayOverridesAsync(
        Guid workId,
        IReadOnlyDictionary<string, string> overrides,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        ct.ThrowIfCancellationRequested();

        var affected = await db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            return conn.Execute(new CommandDefinition(
                "UPDATE works SET display_overrides_json = @json WHERE id = @workId;",
                new
                {
                    json = overrides.Count == 0 ? null : JsonSerializer.Serialize(overrides),
                    workId = GuidSql.ToBlob(workId),
                },
                tx,
                cancellationToken: innerCt));
        }, ct).ConfigureAwait(false);
        return affected > 0;
    }

    public Task<string?> GetWorkMediaTypeAsync(Guid workId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return Task.FromResult(conn.QuerySingleOrDefault<string>(new CommandDefinition(
            "SELECT media_type FROM works WHERE id = @workId LIMIT 1;",
            new { workId = GuidSql.ToBlob(workId) },
            cancellationToken: ct)));
    }

    public Task<Guid?> ResolveWorkIdForAssetAsync(Guid assetId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var value = conn.QuerySingleOrDefault<byte[]>(new CommandDefinition(
            """
            SELECT e.work_id
            FROM media_assets ma
            JOIN editions e ON e.id = ma.edition_id
            WHERE ma.id = @assetId
            LIMIT 1;
            """,
            new { assetId = GuidSql.ToBlob(assetId) },
            cancellationToken: ct));
        return Task.FromResult(value is null ? (Guid?)null : GuidSql.FromDb(value));
    }

    public Task<ItemCanonicalWorkWikidataState?> LoadWorkWikidataStateAsync(
        Guid workId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        return Task.FromResult(LoadWorkWikidataState(conn, workId, ct));
    }

    private static ItemCanonicalWorkWikidataState? LoadWorkWikidataState(
        SqliteConnection conn,
        Guid workId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var row = conn.QuerySingleOrDefault<WikidataStateRow>(new CommandDefinition(
            """
            SELECT wikidata_qid                AS Qid,
                   wikidata_status             AS Status,
                   wikidata_match_source       AS Source,
                   COALESCE(wikidata_match_locked, 0) AS Locked,
                   wikidata_rejected_qids_json AS RejectedQidsJson
            FROM works
            WHERE id = @workId
            LIMIT 1;
            """,
            new { workId = GuidSql.ToBlob(workId) },
            cancellationToken: ct));

        return row is null
            ? null
            : new ItemCanonicalWorkWikidataState(
                row.Qid,
                row.Status,
                row.Source,
                row.Locked == 1,
                row.RejectedQidsJson);
    }

    public Task UpdateWorkIdentityAsync(
        Guid workId,
        string wikidataQid,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wikidataQid);
        ct.ThrowIfCancellationRequested();

        return db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            conn.Execute(new CommandDefinition(
                """
                UPDATE works
                SET wikidata_qid = @wikidataQid,
                    curator_state = 'registered',
                    rejected_at = NULL
                WHERE id = @workId;
                """,
                new { wikidataQid, workId = GuidSql.ToBlob(workId) },
                tx,
                cancellationToken: innerCt));
        }, ct);
    }

    public Task DeleteIdentityArtifactsAsync(
        IReadOnlyCollection<ItemCanonicalIdentityArtifact> artifacts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ct.ThrowIfCancellationRequested();

        var distinct = artifacts
            .Where(artifact => artifact.EntityId != Guid.Empty && !string.IsNullOrWhiteSpace(artifact.Key))
            .Distinct()
            .ToList();
        if (distinct.Count == 0)
        {
            return Task.CompletedTask;
        }

        return db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            foreach (var artifact in distinct)
            {
                innerCt.ThrowIfCancellationRequested();
                var parameters = new
                {
                    entityId = GuidSql.ToBlob(artifact.EntityId),
                    key = artifact.Key,
                };
                conn.Execute(new CommandDefinition(
                    "DELETE FROM canonical_values WHERE entity_id = @entityId AND key = @key;",
                    parameters,
                    tx,
                    cancellationToken: innerCt));
                conn.Execute(new CommandDefinition(
                    "DELETE FROM metadata_claims WHERE entity_id = @entityId AND claim_key = @key;",
                    parameters,
                    tx,
                    cancellationToken: innerCt));
                conn.Execute(new CommandDefinition(
                    "DELETE FROM bridge_ids WHERE entity_id = @entityId AND id_type = @key;",
                    parameters,
                    tx,
                    cancellationToken: innerCt));
            }
        }, ct);
    }

    public Task ReplaceExternalIdentifiersAsync(
        Guid workId,
        IReadOnlyCollection<string> keysToRemove,
        IReadOnlyDictionary<string, string> replacements,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keysToRemove);
        ArgumentNullException.ThrowIfNull(replacements);
        ct.ThrowIfCancellationRequested();

        return db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            var json = conn.QueryFirstOrDefault<string?>(new CommandDefinition(
                "SELECT external_identifiers FROM works WHERE id = @workId LIMIT 1;",
                new { workId = GuidSql.ToBlob(workId) },
                tx,
                cancellationToken: innerCt));

            Dictionary<string, string> identifiers;
            try
            {
                var parsed = string.IsNullOrWhiteSpace(json)
                    ? null
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                identifiers = parsed is null
                    ? new(StringComparer.OrdinalIgnoreCase)
                    : new(parsed, StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Replacing malformed external identifiers for work {WorkId}.", workId);
                identifiers = new(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var key in keysToRemove.Where(key => !string.IsNullOrWhiteSpace(key)))
            {
                identifiers.Remove(key);
            }

            foreach (var (key, value) in replacements)
            {
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    identifiers[key] = value;
                }
            }

            conn.Execute(new CommandDefinition(
                "UPDATE works SET external_identifiers = @json WHERE id = @workId;",
                new
                {
                    json = identifiers.Count == 0 ? null : JsonSerializer.Serialize(identifiers),
                    workId = GuidSql.ToBlob(workId),
                },
                tx,
                cancellationToken: innerCt));
        }, ct);
    }

    public Task<string> AppendRejectedQidAsync(
        Guid workId,
        string? rejectedQid,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = db.CreateConnection();
        var state = LoadWorkWikidataState(conn, workId, ct);
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(state?.RejectedQidsJson))
        {
            try
            {
                foreach (var qid in JsonSerializer.Deserialize<List<string>>(state.RejectedQidsJson) ?? [])
                {
                    rejected.Add(qid);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Replacing malformed rejected-QID JSON for work {WorkId}.", workId);
            }
        }

        if (!string.IsNullOrWhiteSpace(rejectedQid))
        {
            rejected.Add(rejectedQid.Trim());
        }

        return Task.FromResult(
            JsonSerializer.Serialize(rejected.OrderBy(qid => qid, StringComparer.OrdinalIgnoreCase)));
    }
}
