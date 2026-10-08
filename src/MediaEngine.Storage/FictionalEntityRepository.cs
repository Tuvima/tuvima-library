using System.Security.Cryptography;
using System.Text;
using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IFictionalEntityRepository"/>.
///
/// Fictional entities (Characters, Locations, Organizations, Events, and Objects) are discovered
/// during work hydration and enriched asynchronously via Wikidata SPARQL.
/// Work-link junction records live in <c>fictional_entity_work_links</c>.
/// </summary>
public sealed class FictionalEntityRepository : IFictionalEntityRepository
{
    private readonly IDatabaseConnection _db;

    public FictionalEntityRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc/>
    public Task<FictionalEntity?> FindByQidAsync(string qid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(qid);

        using var conn = _db.CreateConnection();
        var result = conn.QueryFirstOrDefault<FictionalEntity>("""
            SELECT fe.id                   AS Id,
                   fe.wikidata_qid          AS WikidataQid,
                   COALESCE(overrides.label, fe.label) AS Label,
                   COALESCE(overrides.description, fe.description) AS Description,
                   fe.entity_sub_type       AS EntitySubType,
                   fe.fictional_universe_qid AS FictionalUniverseQid,
                   fe.fictional_universe_label AS FictionalUniverseLabel,
                   fe.image_url             AS ImageUrl,
                   fe.local_image_path      AS LocalImagePath,
                   fe.created_at            AS CreatedAt,
                   fe.enriched_at           AS EnrichedAt,
                   fe.wikidata_revision_id  AS WikidataRevisionId
            FROM   fictional_entities fe
            LEFT JOIN fictional_entity_user_overrides overrides ON overrides.entity_id = fe.id
            WHERE  fe.wikidata_qid = @qid COLLATE NOCASE
            LIMIT  1;
            """, new { qid });
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FictionalEntity>> FindByQidsAsync(
        IEnumerable<string> qids,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(qids);

        var distinctQids = qids
            .Where(qid => !string.IsNullOrWhiteSpace(qid))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinctQids.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<FictionalEntity>>([]);
        }

        using var conn = _db.CreateConnection();
        var results = new List<FictionalEntity>(distinctQids.Length);
        foreach (var batch in distinctQids.Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            results.AddRange(conn.Query<FictionalEntity>("""
                SELECT fe.id                    AS Id,
                       fe.wikidata_qid          AS WikidataQid,
                       COALESCE(overrides.label, fe.label) AS Label,
                       COALESCE(overrides.description, fe.description) AS Description,
                       fe.entity_sub_type       AS EntitySubType,
                       fe.fictional_universe_qid AS FictionalUniverseQid,
                       fe.fictional_universe_label AS FictionalUniverseLabel,
                       fe.image_url             AS ImageUrl,
                       fe.local_image_path      AS LocalImagePath,
                       fe.created_at            AS CreatedAt,
                       fe.enriched_at           AS EnrichedAt,
                       fe.wikidata_revision_id  AS WikidataRevisionId
                FROM fictional_entities fe
                LEFT JOIN fictional_entity_user_overrides overrides ON overrides.entity_id = fe.id
                WHERE fe.wikidata_qid COLLATE NOCASE IN @qids;
                """, new { qids = batch }));
        }

        return Task.FromResult<IReadOnlyList<FictionalEntity>>(results);
    }

    /// <inheritdoc/>
    public Task<FictionalEntity?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var result = conn.QueryFirstOrDefault<FictionalEntity>("""
            SELECT fe.id                   AS Id,
                   fe.wikidata_qid          AS WikidataQid,
                   COALESCE(overrides.label, fe.label) AS Label,
                   COALESCE(overrides.description, fe.description) AS Description,
                   fe.entity_sub_type       AS EntitySubType,
                   fe.fictional_universe_qid AS FictionalUniverseQid,
                   fe.fictional_universe_label AS FictionalUniverseLabel,
                   fe.image_url             AS ImageUrl,
                   fe.local_image_path      AS LocalImagePath,
                   fe.created_at            AS CreatedAt,
                   fe.enriched_at           AS EnrichedAt,
                   fe.wikidata_revision_id  AS WikidataRevisionId
            FROM   fictional_entities fe
            LEFT JOIN fictional_entity_user_overrides overrides ON overrides.entity_id = fe.id
            WHERE  fe.id = @id
            LIMIT  1;
            """, new { id });
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FictionalEntity>> GetByUniverseAsync(
        string universeQid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(universeQid);

        using var conn = _db.CreateConnection();
        var results = conn.Query<FictionalEntity>("""
            SELECT fe.id                   AS Id,
                   fe.wikidata_qid          AS WikidataQid,
                   COALESCE(overrides.label, fe.label) AS Label,
                   COALESCE(overrides.description, fe.description) AS Description,
                   fe.entity_sub_type       AS EntitySubType,
                   fe.fictional_universe_qid AS FictionalUniverseQid,
                   fe.fictional_universe_label AS FictionalUniverseLabel,
                   fe.image_url             AS ImageUrl,
                   fe.local_image_path      AS LocalImagePath,
                   fe.created_at            AS CreatedAt,
                   fe.enriched_at           AS EnrichedAt,
                   fe.wikidata_revision_id  AS WikidataRevisionId
            FROM fictional_entities fe
            LEFT JOIN fictional_entity_user_overrides overrides ON overrides.entity_id = fe.id
            WHERE fe.fictional_universe_qid = @universeQid
            ORDER BY fe.entity_sub_type, COALESCE(overrides.label, fe.label);
            """, new { universeQid }).AsList();

        return Task.FromResult<IReadOnlyList<FictionalEntity>>(results);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FictionalEntity>> GetByUniverseAndTypeAsync(
        string universeQid, string entitySubType, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var results = conn.Query<FictionalEntity>("""
            SELECT fe.id                   AS Id,
                   fe.wikidata_qid          AS WikidataQid,
                   COALESCE(overrides.label, fe.label) AS Label,
                   COALESCE(overrides.description, fe.description) AS Description,
                   fe.entity_sub_type       AS EntitySubType,
                   fe.fictional_universe_qid AS FictionalUniverseQid,
                   fe.fictional_universe_label AS FictionalUniverseLabel,
                   fe.image_url             AS ImageUrl,
                   fe.local_image_path      AS LocalImagePath,
                   fe.created_at            AS CreatedAt,
                   fe.enriched_at           AS EnrichedAt,
                   fe.wikidata_revision_id  AS WikidataRevisionId
            FROM fictional_entities fe
            LEFT JOIN fictional_entity_user_overrides overrides ON overrides.entity_id = fe.id
            WHERE fe.fictional_universe_qid = @universeQid
              AND fe.entity_sub_type = @entitySubType
            ORDER BY COALESCE(overrides.label, fe.label);
            """, new { universeQid, entitySubType }).AsList();

        return Task.FromResult<IReadOnlyList<FictionalEntity>>(results);
    }

    /// <inheritdoc/>
    public Task<FictionalEntitySearchPage> SearchVisibleByUniverseAsync(
        string universeQid,
        IReadOnlyCollection<string> visibleWorkQids,
        string? entitySubType,
        string? search,
        int offset,
        int limit,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(universeQid);
        ArgumentNullException.ThrowIfNull(visibleWorkQids);
        if (visibleWorkQids.Count == 0)
        {
            return Task.FromResult(new FictionalEntitySearchPage([], 0));
        }

        using var conn = _db.CreateConnection();
        conn.Execute("CREATE TEMP TABLE IF NOT EXISTS editor_visible_work_qids (qid TEXT PRIMARY KEY COLLATE NOCASE);");
        conn.Execute("DELETE FROM editor_visible_work_qids;");
        foreach (var batch in visibleWorkQids.Where(qid => !string.IsNullOrWhiteSpace(qid)).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            conn.Execute("INSERT OR IGNORE INTO editor_visible_work_qids (qid) VALUES (@qid);", batch.Select(qid => new { qid }));
        }

        const string visibleWhere = """
            fe.fictional_universe_qid = @universeQid
            AND (@entitySubType IS NULL OR fe.entity_sub_type = @entitySubType COLLATE NOCASE)
            AND (@search IS NULL OR fe.wikidata_qid LIKE '%' || @search || '%' COLLATE NOCASE
                 OR fe.label LIKE '%' || @search || '%' COLLATE NOCASE
                 OR COALESCE(overrides.label, '') LIKE '%' || @search || '%' COLLATE NOCASE)
            AND EXISTS (
                SELECT 1 FROM fictional_entity_work_links fewl
                INNER JOIN editor_visible_work_qids visible ON visible.qid = fewl.work_qid
                WHERE fewl.entity_id = fe.id)
            """;
        var args = new { universeQid, entitySubType = string.IsNullOrWhiteSpace(entitySubType) ? null : entitySubType, search = string.IsNullOrWhiteSpace(search) ? null : search.Trim(), offset = Math.Max(0, offset), limit = Math.Clamp(limit, 1, 100) };
        var total = conn.ExecuteScalar<int>($"""
            SELECT COUNT(*)
            FROM fictional_entities fe
            LEFT JOIN fictional_entity_user_overrides overrides ON overrides.entity_id = fe.id
            WHERE {visibleWhere};
            """, args);
        var items = conn.Query<FictionalEntity>($"""
            SELECT fe.id AS Id,
                   fe.wikidata_qid AS WikidataQid,
                   COALESCE(overrides.label, fe.label) AS Label,
                   COALESCE(overrides.description, fe.description) AS Description,
                   fe.entity_sub_type AS EntitySubType,
                   fe.fictional_universe_qid AS FictionalUniverseQid,
                   fe.fictional_universe_label AS FictionalUniverseLabel,
                   fe.image_url AS ImageUrl,
                   fe.local_image_path AS LocalImagePath,
                   fe.created_at AS CreatedAt,
                   fe.enriched_at AS EnrichedAt,
                   fe.wikidata_revision_id AS WikidataRevisionId
            FROM fictional_entities fe
            LEFT JOIN fictional_entity_user_overrides overrides ON overrides.entity_id = fe.id
            WHERE {visibleWhere}
            ORDER BY COALESCE(overrides.label, fe.label) COLLATE NOCASE, fe.wikidata_qid
            LIMIT @limit OFFSET @offset;
            """, args).AsList();
        return Task.FromResult(new FictionalEntitySearchPage(items, total));
    }

    /// <inheritdoc/>
    public Task UpdateUserDetailsAsync(Guid entityId, string label, string? description, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            conn.Execute("""
                INSERT INTO fictional_entity_user_overrides (entity_id, label, description, updated_at)
                VALUES (@entityId, @label, @description, datetime('now'))
                ON CONFLICT(entity_id) DO UPDATE SET
                    label = excluded.label,
                    description = excluded.description,
                    updated_at = datetime('now');
                """, new { entityId, label = label.Trim(), description }, tx);
        }, ct);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FictionalEntity>> GetByWorkQidAsync(
        string workQid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(workQid);

        using var conn = _db.CreateConnection();
        var results = conn.Query<FictionalEntity>("""
            SELECT DISTINCT fe.id             AS Id,
                   fe.wikidata_qid            AS WikidataQid,
                   COALESCE(overrides.label, fe.label) AS Label,
                   COALESCE(overrides.description, fe.description) AS Description,
                   fe.entity_sub_type         AS EntitySubType,
                   fe.fictional_universe_qid  AS FictionalUniverseQid,
                   fe.fictional_universe_label AS FictionalUniverseLabel,
                   fe.image_url               AS ImageUrl,
                   fe.local_image_path        AS LocalImagePath,
                   fe.created_at              AS CreatedAt,
                   fe.enriched_at             AS EnrichedAt,
                   fe.wikidata_revision_id    AS WikidataRevisionId
            FROM   fictional_entities fe
            INNER JOIN fictional_entity_work_links fewl
                ON fe.id = fewl.entity_id
            LEFT JOIN fictional_entity_user_overrides overrides ON overrides.entity_id = fe.id
            WHERE  fewl.work_qid = @workQid COLLATE NOCASE;
            """, new { workQid }).AsList();

        return Task.FromResult<IReadOnlyList<FictionalEntity>>(results);
    }

    /// <inheritdoc/>
    public Task CreateAsync(FictionalEntity entity, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entity);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO fictional_entities
                (id, wikidata_qid, label, description, entity_sub_type,
                 fictional_universe_qid, fictional_universe_label,
                 image_url, local_image_path, created_at, enriched_at)
            VALUES
                (@Id, @WikidataQid, @Label, @Description, @EntitySubType,
                 @FictionalUniverseQid, @FictionalUniverseLabel,
                 @ImageUrl, @LocalImagePath, @CreatedAt, @EnrichedAt);
            """,
            new
            {
                Id = entity.Id,
                entity.WikidataQid,
                entity.Label,
                entity.Description,
                entity.EntitySubType,
                entity.FictionalUniverseQid,
                entity.FictionalUniverseLabel,
                entity.ImageUrl,
                entity.LocalImagePath,
                CreatedAt = entity.CreatedAt,
                EnrichedAt = entity.EnrichedAt,
            });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpdateEnrichmentAsync(
        Guid entityId,
        string? label,
        string? description,
        string? imageUrl,
        DateTimeOffset enrichedAt,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE fictional_entities
            SET    label       = COALESCE(NULLIF(TRIM(@label), ''), label),
                   description = COALESCE(NULLIF(TRIM(@description), ''), description),
                   image_url   = COALESCE(NULLIF(TRIM(@imageUrl), ''), image_url),
                   enriched_at = @enrichedAt
            WHERE  id = @entityId;
            """,
            new
            {
                entityId,
                label,
                description,
                imageUrl,
                enrichedAt,
            });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task UpdateUniverseAsync(
        Guid entityId,
        string universeQid,
        string? universeLabel,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(universeQid);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE fictional_entities
            SET fictional_universe_qid = @universeQid,
                fictional_universe_label = COALESCE(NULLIF(TRIM(@universeLabel), ''), fictional_universe_label)
            WHERE id = @entityId;
            """, new { entityId, universeQid, universeLabel });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task LinkToWorkAsync(FictionalEntityWorkLink appearance, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentException.ThrowIfNullOrWhiteSpace(appearance.WorkQid);
        ArgumentException.ThrowIfNullOrWhiteSpace(appearance.LinkType);

        var appearanceKey = string.IsNullOrWhiteSpace(appearance.AppearanceKey)
            ? BuildAppearanceKey(appearance)
            : appearance.AppearanceKey.Trim();

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO fictional_entity_work_links
                (id, appearance_key, entity_id, work_qid, work_label, link_type,
                 appearance_role, work_context, anchor_kind, anchor_value,
                 narrative_time_index, start_time, end_time, spoiler_for_work_qid,
                 source_provider, provenance, is_supplemental, confidence)
            VALUES
                (@Id, @AppearanceKey, @FictionalEntityId, @WorkQid, @WorkLabel, @LinkType,
                 @AppearanceRole, @WorkContext, @AnchorKind, @AnchorValue,
                 @NarrativeTimeIndex, @StartTime, @EndTime, @SpoilerForWorkQid,
                 @SourceProvider, @Provenance, @IsSupplemental, @Confidence)
            ON CONFLICT(appearance_key) DO UPDATE SET
                work_label = excluded.work_label,
                appearance_role = excluded.appearance_role,
                work_context = excluded.work_context,
                anchor_kind = excluded.anchor_kind,
                anchor_value = excluded.anchor_value,
                narrative_time_index = excluded.narrative_time_index,
                start_time = excluded.start_time,
                end_time = excluded.end_time,
                spoiler_for_work_qid = excluded.spoiler_for_work_qid,
                source_provider = excluded.source_provider,
                provenance = excluded.provenance,
                is_supplemental = excluded.is_supplemental,
                confidence = excluded.confidence;
            """, new
        {
            Id = Guid.NewGuid(),
            AppearanceKey = appearanceKey,
            appearance.FictionalEntityId,
            appearance.WorkQid,
            appearance.WorkLabel,
            appearance.LinkType,
            appearance.AppearanceRole,
            appearance.WorkContext,
            appearance.AnchorKind,
            appearance.AnchorValue,
            appearance.NarrativeTimeIndex,
            appearance.StartTime,
            appearance.EndTime,
            appearance.SpoilerForWorkQid,
            appearance.SourceProvider,
            Provenance = string.IsNullOrWhiteSpace(appearance.Provenance) ? "Wikidata" : appearance.Provenance,
            appearance.IsSupplemental,
            appearance.Confidence,
        });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FictionalEntityWorkLink>>
        GetWorkLinksAsync(Guid entityId, CancellationToken ct = default)
    {
        return GetSingleEntityWorkLinksAsync(entityId, ct);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FictionalEntityWorkLink>> GetWorkLinksAsync(
        IEnumerable<Guid> entityIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ct.ThrowIfCancellationRequested();

        var ids = entityIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<FictionalEntityWorkLink>>([]);
        }

        using var conn = _db.CreateConnection();
        var rows = new List<WorkLinkRow>();
        foreach (var batch in ids.Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            var parameters = new DynamicParameters();
            var idClause = AddGuidBlobList(parameters, "entityId", batch);
            rows.AddRange(conn.Query<WorkLinkRow>($"""
                SELECT entity_id  AS EntityId,
                       work_qid   AS WorkQid,
                       work_label AS WorkLabel,
                       link_type  AS LinkType,
                       appearance_role AS AppearanceRole,
                       work_context AS WorkContext,
                       anchor_kind AS AnchorKind,
                       anchor_value AS AnchorValue,
                       narrative_time_index AS NarrativeTimeIndex,
                       start_time AS StartTime,
                       end_time AS EndTime,
                       spoiler_for_work_qid AS SpoilerForWorkQid,
                       source_provider AS SourceProvider,
                       provenance AS Provenance,
                       is_supplemental AS IsSupplemental,
                       confidence AS Confidence,
                       appearance_key AS AppearanceKey
                FROM   fictional_entity_work_links
                WHERE  entity_id IN ({idClause})
                ORDER BY entity_id, work_qid;
                """, parameters));
        }

        IReadOnlyList<FictionalEntityWorkLink> result = rows
            .Select(row => new FictionalEntityWorkLink(
                row.EntityId,
                row.WorkQid,
                row.WorkLabel,
                row.LinkType,
                row.AppearanceRole,
                row.WorkContext,
                row.AnchorKind,
                row.AnchorValue,
                row.NarrativeTimeIndex,
                row.StartTime,
                row.EndTime,
                row.SpoilerForWorkQid,
                row.SourceProvider,
                row.Provenance,
                row.IsSupplemental,
                row.Confidence,
                row.AppearanceKey))
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<int> CountAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        var count = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM fictional_entities;");
        return Task.FromResult(count);
    }

    /// <inheritdoc/>
    public Task UpdateRevisionAsync(Guid entityId, long revisionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE fictional_entities
            SET    wikidata_revision_id = @revisionId
            WHERE  id = @entityId;
            """,
            new { entityId, revisionId });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FictionalEntity>> GetStaleEntitiesAsync(
        int staleAfterDays, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var cutoff = DateTimeOffset.UtcNow.AddDays(-staleAfterDays);

        using var conn = _db.CreateConnection();
        var results = conn.Query<FictionalEntity>("""
            SELECT fe.id                    AS Id,
                   fe.wikidata_qid          AS WikidataQid,
                   COALESCE(overrides.label, fe.label) AS Label,
                   COALESCE(overrides.description, fe.description) AS Description,
                   fe.entity_sub_type       AS EntitySubType,
                   fe.fictional_universe_qid AS FictionalUniverseQid,
                   fe.fictional_universe_label AS FictionalUniverseLabel,
                   fe.image_url             AS ImageUrl,
                   fe.local_image_path      AS LocalImagePath,
                   fe.created_at            AS CreatedAt,
                   fe.enriched_at           AS EnrichedAt,
                   fe.wikidata_revision_id  AS WikidataRevisionId
            FROM fictional_entities fe
            LEFT JOIN fictional_entity_user_overrides overrides ON overrides.entity_id = fe.id
            WHERE fe.enriched_at IS NOT NULL
              AND fe.enriched_at < @cutoff
            ORDER BY fe.enriched_at ASC
            LIMIT  @limit;
            """,
            new { cutoff, limit }).AsList();

        return Task.FromResult<IReadOnlyList<FictionalEntity>>(results);
    }

    // ── Private row types ────────────────────────────────────────────────────

    private async Task<IReadOnlyList<FictionalEntityWorkLink>>
        GetSingleEntityWorkLinksAsync(Guid entityId, CancellationToken ct)
    {
        var links = await GetWorkLinksAsync([entityId], ct).ConfigureAwait(false);
        return links;
    }

    private static string AddGuidBlobList(
        DynamicParameters parameters,
        string prefix,
        IReadOnlyList<Guid> ids)
    {
        var names = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            var name = $"{prefix}{i}";
            names[i] = $"@{name}";
            parameters.Add(name, GuidSql.ToBlob(ids[i]));
        }

        return string.Join(", ", names);
    }

    /// <summary>Intermediate row type for the work-link queries.</summary>
    private sealed class WorkLinkRow
    {
        public Guid EntityId { get; set; }
        public string WorkQid { get; set; } = string.Empty;
        public string? WorkLabel { get; set; }
        public string LinkType { get; set; } = string.Empty;
        public string? AppearanceRole { get; set; }
        public string? WorkContext { get; set; }
        public string? AnchorKind { get; set; }
        public string? AnchorValue { get; set; }
        public string? NarrativeTimeIndex { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public string? SpoilerForWorkQid { get; set; }
        public string? SourceProvider { get; set; }
        public string Provenance { get; set; } = "Wikidata";
        public bool IsSupplemental { get; set; }
        public double? Confidence { get; set; }
        public string? AppearanceKey { get; set; }
    }

    private static string BuildAppearanceKey(FictionalEntityWorkLink appearance)
    {
        var canonical = string.Join('|',
            appearance.FictionalEntityId.ToString("N"), appearance.WorkQid.Trim(), (appearance.LinkType ?? string.Empty).Trim(),
            appearance.AppearanceRole?.Trim() ?? string.Empty, appearance.WorkContext?.Trim() ?? string.Empty,
            appearance.AnchorKind?.Trim() ?? string.Empty, appearance.AnchorValue?.Trim() ?? string.Empty,
            appearance.NarrativeTimeIndex?.Trim() ?? string.Empty, appearance.StartTime?.Trim() ?? string.Empty,
            appearance.EndTime?.Trim() ?? string.Empty, appearance.SpoilerForWorkQid?.Trim() ?? string.Empty,
            appearance.SourceProvider?.Trim() ?? string.Empty, (appearance.Provenance ?? "Wikidata").Trim(),
            appearance.IsSupplemental ? "1" : "0");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
