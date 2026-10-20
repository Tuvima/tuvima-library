using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public sealed class PluginLoreRepository : IPluginLoreRepository
{
    private readonly IDatabaseConnection _db;

    public PluginLoreRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<IReadOnlyList<PluginLoreSourceRecord>> GetSourcesAsync(
        string universeQid,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<PluginLoreSourceRecord>("""
            SELECT id AS Id, universe_qid AS UniverseQid, plugin_id AS PluginId,
                   source_key AS SourceKey, source_name AS SourceName, base_url AS BaseUrl,
                   api_url AS ApiUrl, status AS Status, confidence AS Confidence,
                   evidence_json AS EvidenceJson, license AS License,
                   approved_at AS ApprovedAt, approved_by AS ApprovedBy, rejected_at AS RejectedAt,
                   last_discovered_at AS LastDiscoveredAt, last_enriched_at AS LastEnrichedAt,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM plugin_lore_sources
            WHERE universe_qid = @universeQid COLLATE NOCASE
            ORDER BY status, confidence DESC, source_name;
            """, new { universeQid });
        return Task.FromResult<IReadOnlyList<PluginLoreSourceRecord>>(rows.AsList());
    }

    public Task<IReadOnlyList<PluginLoreSourceRecord>> GetApprovedSourcesAsync(
        string universeQid,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<PluginLoreSourceRecord>("""
            SELECT id AS Id, universe_qid AS UniverseQid, plugin_id AS PluginId,
                   source_key AS SourceKey, source_name AS SourceName, base_url AS BaseUrl,
                   api_url AS ApiUrl, status AS Status, confidence AS Confidence,
                   evidence_json AS EvidenceJson, license AS License,
                   approved_at AS ApprovedAt, approved_by AS ApprovedBy, rejected_at AS RejectedAt,
                   last_discovered_at AS LastDiscoveredAt, last_enriched_at AS LastEnrichedAt,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM plugin_lore_sources
            WHERE universe_qid = @universeQid COLLATE NOCASE
              AND status = @status
            ORDER BY confidence DESC, source_name;
            """, new { universeQid, status = PluginLoreSourceStatus.Approved });
        return Task.FromResult<IReadOnlyList<PluginLoreSourceRecord>>(rows.AsList());
    }

    public Task<PluginLoreSourceRecord?> FindSourceAsync(
        Guid sourceId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var source = conn.QueryFirstOrDefault<PluginLoreSourceRecord>("""
            SELECT id AS Id, universe_qid AS UniverseQid, plugin_id AS PluginId,
                   source_key AS SourceKey, source_name AS SourceName, base_url AS BaseUrl,
                   api_url AS ApiUrl, status AS Status, confidence AS Confidence,
                   evidence_json AS EvidenceJson, license AS License,
                   approved_at AS ApprovedAt, approved_by AS ApprovedBy, rejected_at AS RejectedAt,
                   last_discovered_at AS LastDiscoveredAt, last_enriched_at AS LastEnrichedAt,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM plugin_lore_sources
            WHERE id = @sourceId
            LIMIT 1;
            """, new { sourceId });
        return Task.FromResult(source);
    }

    public Task UpsertSourceCandidateAsync(
        string universeQid,
        string pluginId,
        PluginLoreSourceCandidateRecord candidate,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO plugin_lore_sources
                (id, universe_qid, plugin_id, source_key, source_name, base_url, api_url,
                 status, confidence, evidence_json, license, last_discovered_at, created_at, updated_at)
            VALUES
                (@Id, @UniverseQid, @PluginId, @SourceKey, @SourceName, @BaseUrl, @ApiUrl,
                 @Status, @Confidence, @EvidenceJson, @License, @Now, @Now, @Now)
            ON CONFLICT(universe_qid, plugin_id, source_key) DO UPDATE SET
                source_name = excluded.source_name,
                base_url = excluded.base_url,
                api_url = excluded.api_url,
                confidence = excluded.confidence,
                evidence_json = excluded.evidence_json,
                license = excluded.license,
                last_discovered_at = excluded.last_discovered_at,
                updated_at = excluded.updated_at;
            """,
            new
            {
                Id = Guid.NewGuid(),
                UniverseQid = universeQid,
                PluginId = pluginId,
                candidate.SourceKey,
                candidate.SourceName,
                candidate.BaseUrl,
                candidate.ApiUrl,
                Status = PluginLoreSourceStatus.Pending,
                candidate.Confidence,
                candidate.EvidenceJson,
                candidate.License,
                Now = now,
            });
        return Task.CompletedTask;
    }

    public Task<PluginLoreSourceRecord> AddManualSourceAsync(
        string universeQid,
        string pluginId,
        string sourceName,
        string baseUrl,
        string apiUrl,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var sourceKey = NormalizeSourceKey(baseUrl);

        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO plugin_lore_sources
                (id, universe_qid, plugin_id, source_key, source_name, base_url, api_url,
                 status, confidence, evidence_json, last_discovered_at, created_at, updated_at)
            VALUES
                (@Id, @UniverseQid, @PluginId, @SourceKey, @SourceName, @BaseUrl, @ApiUrl,
                 @Status, @Confidence, @EvidenceJson, @Now, @Now, @Now)
            ON CONFLICT(universe_qid, plugin_id, source_key) DO UPDATE SET
                source_name = excluded.source_name,
                base_url = excluded.base_url,
                api_url = excluded.api_url,
                updated_at = excluded.updated_at;
            """,
            new
            {
                Id = Guid.NewGuid(),
                UniverseQid = universeQid,
                PluginId = pluginId,
                SourceKey = sourceKey,
                SourceName = sourceName,
                BaseUrl = baseUrl,
                ApiUrl = apiUrl,
                Status = PluginLoreSourceStatus.Pending,
                Confidence = 0.75,
                EvidenceJson = """{"method":"manual"}""",
                Now = now,
            });

        var source = conn.QueryFirst<PluginLoreSourceRecord>("""
            SELECT id AS Id, universe_qid AS UniverseQid, plugin_id AS PluginId,
                   source_key AS SourceKey, source_name AS SourceName, base_url AS BaseUrl,
                   api_url AS ApiUrl, status AS Status, confidence AS Confidence,
                   evidence_json AS EvidenceJson, license AS License,
                   approved_at AS ApprovedAt, approved_by AS ApprovedBy, rejected_at AS RejectedAt,
                   last_discovered_at AS LastDiscoveredAt, last_enriched_at AS LastEnrichedAt,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
            FROM plugin_lore_sources
            WHERE universe_qid = @universeQid COLLATE NOCASE
              AND plugin_id = @pluginId COLLATE NOCASE
              AND source_key = @sourceKey COLLATE NOCASE
            LIMIT 1;
            """, new { universeQid, pluginId, sourceKey });
        return Task.FromResult(source);
    }

    public Task SetSourceStatusAsync(
        Guid sourceId,
        string status,
        string? actor,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (status is not (PluginLoreSourceStatus.Approved or PluginLoreSourceStatus.Rejected or PluginLoreSourceStatus.Pending))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported plugin lore source status.");
        }

        var now = DateTimeOffset.UtcNow;
        using var conn = _db.CreateConnection();
        conn.Execute("""
            UPDATE plugin_lore_sources
            SET status = @status,
                approved_at = CASE WHEN @status = @approved THEN @now ELSE approved_at END,
                approved_by = CASE WHEN @status = @approved THEN @actor ELSE approved_by END,
                rejected_at = CASE WHEN @status = @rejected THEN @now ELSE rejected_at END,
                updated_at = @now
            WHERE id = @sourceId;
            """,
            new
            {
                sourceId,
                status,
                approved = PluginLoreSourceStatus.Approved,
                rejected = PluginLoreSourceStatus.Rejected,
                actor,
                now,
            });
        return Task.CompletedTask;
    }

    public Task UpsertExtractionResultAsync(
        PluginLoreSourceRecord source,
        IReadOnlyList<PluginLoreEntityRecord> entities,
        IReadOnlyList<PluginLoreRelationshipRecord> relationships,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            foreach (var entity in entities.Where(e => !string.IsNullOrWhiteSpace(e.ExternalKey) && !string.IsNullOrWhiteSpace(e.Label)))
            {
                innerCt.ThrowIfCancellationRequested();
                conn.Execute("""
                    INSERT INTO plugin_lore_entities
                        (id, source_id, universe_qid, plugin_id, external_key, wikidata_qid,
                         label, description, entity_type, aliases_json, source_url, confidence,
                         evidence_json, created_at, updated_at)
                    VALUES
                        (@Id, @SourceId, @UniverseQid, @PluginId, @ExternalKey, @WikidataQid,
                         @Label, @Description, @EntityType, @AliasesJson, @SourceUrl, @Confidence,
                         @EvidenceJson, @Now, @Now)
                    ON CONFLICT(source_id, external_key) DO UPDATE SET
                        wikidata_qid = excluded.wikidata_qid,
                        label = excluded.label,
                        description = excluded.description,
                        entity_type = excluded.entity_type,
                        aliases_json = excluded.aliases_json,
                        source_url = excluded.source_url,
                        confidence = excluded.confidence,
                        evidence_json = excluded.evidence_json,
                        updated_at = excluded.updated_at;
                    """,
                    new
                    {
                        Id = entity.Id == Guid.Empty ? Guid.NewGuid() : entity.Id,
                        SourceId = source.Id,
                        UniverseQid = source.UniverseQid,
                        PluginId = source.PluginId,
                        entity.ExternalKey,
                        entity.WikidataQid,
                        entity.Label,
                        entity.Description,
                        EntityType = NormalizeEntityType(entity.EntityType),
                        AliasesJson = string.IsNullOrWhiteSpace(entity.AliasesJson) ? "[]" : entity.AliasesJson,
                        entity.SourceUrl,
                        entity.Confidence,
                        EvidenceJson = string.IsNullOrWhiteSpace(entity.EvidenceJson) ? "{}" : entity.EvidenceJson,
                        Now = now,
                    },
                    tx);
            }

            foreach (var relationship in relationships.Where(r => !string.IsNullOrWhiteSpace(r.SubjectExternalKey)
                                                                  && !string.IsNullOrWhiteSpace(r.ObjectExternalKey)
                                                                  && !string.IsNullOrWhiteSpace(r.RelationshipType)))
            {
                innerCt.ThrowIfCancellationRequested();
                conn.Execute("""
                    INSERT INTO plugin_lore_relationships
                        (id, source_id, universe_qid, plugin_id, subject_external_key, subject_qid,
                         object_external_key, object_qid, relationship_type, source_url, confidence,
                         evidence_json, created_at, updated_at)
                    VALUES
                        (@Id, @SourceId, @UniverseQid, @PluginId, @SubjectExternalKey, @SubjectQid,
                         @ObjectExternalKey, @ObjectQid, @RelationshipType, @SourceUrl, @Confidence,
                         @EvidenceJson, @Now, @Now)
                    ON CONFLICT(source_id, subject_external_key, relationship_type, object_external_key) DO UPDATE SET
                        subject_qid = excluded.subject_qid,
                        object_qid = excluded.object_qid,
                        source_url = excluded.source_url,
                        confidence = excluded.confidence,
                        evidence_json = excluded.evidence_json,
                        updated_at = excluded.updated_at;
                    """,
                    new
                    {
                        Id = relationship.Id == Guid.Empty ? Guid.NewGuid() : relationship.Id,
                        SourceId = source.Id,
                        UniverseQid = source.UniverseQid,
                        PluginId = source.PluginId,
                        relationship.SubjectExternalKey,
                        relationship.SubjectQid,
                        relationship.ObjectExternalKey,
                        relationship.ObjectQid,
                        relationship.RelationshipType,
                        relationship.SourceUrl,
                        relationship.Confidence,
                        EvidenceJson = string.IsNullOrWhiteSpace(relationship.EvidenceJson) ? "{}" : relationship.EvidenceJson,
                        Now = now,
                    },
                    tx);
            }

            innerCt.ThrowIfCancellationRequested();
            conn.Execute("""
                UPDATE plugin_lore_sources
                SET last_enriched_at = @now,
                    updated_at = @now
                WHERE id = @sourceId;
                """, new { sourceId = source.Id, now }, tx);
        }, ct);
    }

    public Task<IReadOnlyList<PluginLoreEntityRecord>> GetEntitiesAsync(
        string universeQid,
        bool approvedOnly = true,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<PluginLoreEntityRecord>("""
            SELECT e.id AS Id, e.source_id AS SourceId, e.universe_qid AS UniverseQid,
                   e.plugin_id AS PluginId, e.external_key AS ExternalKey, e.wikidata_qid AS WikidataQid,
                   e.label AS Label, e.description AS Description, e.entity_type AS EntityType,
                   e.aliases_json AS AliasesJson, e.source_url AS SourceUrl, e.confidence AS Confidence,
                   e.evidence_json AS EvidenceJson, e.created_at AS CreatedAt, e.updated_at AS UpdatedAt
            FROM plugin_lore_entities e
            INNER JOIN plugin_lore_sources s ON s.id = e.source_id
            WHERE e.universe_qid = @universeQid COLLATE NOCASE
              AND (@approvedOnly = 0 OR s.status = @approved)
            ORDER BY e.entity_type, e.label;
            """, new { universeQid, approvedOnly = approvedOnly ? 1 : 0, approved = PluginLoreSourceStatus.Approved });
        return Task.FromResult<IReadOnlyList<PluginLoreEntityRecord>>(rows.AsList());
    }

    public Task<IReadOnlyList<PluginLoreItemSource>> GetApprovedSourceNamesForItemsAsync(
        string universeQid,
        IReadOnlyCollection<string> wikidataQids,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(wikidataQids);
        var qids = wikidataQids
            .Where(qid => !string.IsNullOrWhiteSpace(qid))
            .Select(qid => qid.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rows = new List<PluginLoreItemSource>();
        if (qids.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<PluginLoreItemSource>>(rows);
        }

        using var conn = _db.CreateConnection();
        foreach (var batch in qids.Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            ct.ThrowIfCancellationRequested();
            rows.AddRange(conn.Query<PluginLoreItemSource>("""
                -- Plain columns (not expressions) keep their declared TEXT type, even when nothing matches.
                SELECT DISTINCT e.wikidata_qid AS WikidataQid, s.source_name AS SourceName
                FROM plugin_lore_entities e
                INNER JOIN plugin_lore_sources s ON s.id = e.source_id
                WHERE e.universe_qid = @universeQid COLLATE NOCASE
                  AND s.status = @approved
                  AND e.wikidata_qid COLLATE NOCASE IN @batch
                ORDER BY 1, 2;
                """, new { universeQid, approved = PluginLoreSourceStatus.Approved, batch }));
        }

        return Task.FromResult<IReadOnlyList<PluginLoreItemSource>>(rows);
    }

    public Task<IReadOnlyList<PluginLoreRelationshipRecord>> GetRelationshipsAsync(
        string universeQid,
        bool approvedOnly = true,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<PluginLoreRelationshipRecord>("""
            SELECT r.id AS Id, r.source_id AS SourceId, r.universe_qid AS UniverseQid,
                   r.plugin_id AS PluginId, r.subject_external_key AS SubjectExternalKey,
                   r.subject_qid AS SubjectQid, r.object_external_key AS ObjectExternalKey,
                   r.object_qid AS ObjectQid, r.relationship_type AS RelationshipType,
                   r.source_url AS SourceUrl, r.confidence AS Confidence,
                   r.evidence_json AS EvidenceJson, r.created_at AS CreatedAt, r.updated_at AS UpdatedAt
            FROM plugin_lore_relationships r
            INNER JOIN plugin_lore_sources s ON s.id = r.source_id
            WHERE r.universe_qid = @universeQid COLLATE NOCASE
              AND (@approvedOnly = 0 OR s.status = @approved)
            ORDER BY r.relationship_type;
            """, new { universeQid, approvedOnly = approvedOnly ? 1 : 0, approved = PluginLoreSourceStatus.Approved });
        return Task.FromResult<IReadOnlyList<PluginLoreRelationshipRecord>>(rows.AsList());
    }

    private static string NormalizeSourceKey(string baseUrl)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return uri.Host.ToLowerInvariant();
        }

        return baseUrl.Trim().TrimEnd('/').ToLowerInvariant();
    }

    private static string NormalizeEntityType(string value) =>
        value switch
        {
            "Character" or "Location" or "Organization" or "Event" or "Object" => value,
            _ => "Unknown",
        };
}
