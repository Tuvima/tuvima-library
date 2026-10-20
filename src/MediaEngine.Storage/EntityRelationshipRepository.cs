using System.Security.Cryptography;
using System.Text;
using Dapper;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite storage for statement-level universe graph facts. A fact's identity includes
/// its context and qualifiers, preventing one adaptation or spoiler boundary from
/// overwriting another fact with the same subject, predicate, and object.
/// </summary>
public sealed class EntityRelationshipRepository : IEntityRelationshipRepository
{
    private readonly IDatabaseConnection _db;

    public EntityRelationshipRepository(IDatabaseConnection db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task CreateAsync(EntityRelationship edge, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(edge);
        ValidateFact(edge);

        var qualifiers = NormalizeQualifiers(edge);
        edge.StatementKey = string.IsNullOrWhiteSpace(edge.StatementKey)
            ? BuildStatementKey(edge, qualifiers)
            : edge.StatementKey.Trim();

        return _db.ExecuteWriteAsync((conn, tx, token) =>
        {
            token.ThrowIfCancellationRequested();
            conn.Execute("""
                INSERT INTO entity_relationships
                    (id, statement_key, subject_qid, relationship_type, object_qid,
                     confidence, context_work_qid, source_provider, provenance,
                     is_supplemental, discovered_at, start_time, end_time)
                VALUES
                    (@Id, @StatementKey, @SubjectQid, @RelationshipTypeValue, @ObjectQid,
                     @Confidence, @ContextWorkQid, @SourceProvider, @Provenance,
                     @IsSupplemental, @DiscoveredAt, @StartTime, @EndTime)
                ON CONFLICT(statement_key) DO UPDATE SET
                    confidence = excluded.confidence,
                    context_work_qid = excluded.context_work_qid,
                    source_provider = excluded.source_provider,
                    provenance = excluded.provenance,
                    is_supplemental = excluded.is_supplemental,
                    start_time = excluded.start_time,
                    end_time = excluded.end_time;
                """, edge, tx);

            var relationshipId = conn.ExecuteScalar<Guid>(
                "SELECT id FROM entity_relationships WHERE statement_key = @statementKey LIMIT 1;",
                new { statementKey = edge.StatementKey }, tx);
            edge.Id = relationshipId;

            conn.Execute("DELETE FROM entity_relationship_qualifiers WHERE relationship_id = @relationshipId;",
                new { relationshipId }, tx);
            foreach (var qualifier in qualifiers)
            {
                token.ThrowIfCancellationRequested();
                qualifier.RelationshipId = relationshipId;
                conn.Execute("""
                    INSERT INTO entity_relationship_qualifiers
                        (id, relationship_id, qualifier_type, value, value_kind,
                         source_provider, provenance, is_supplemental, confidence)
                    VALUES
                        (@Id, @RelationshipId, @QualifierType, @Value, @ValueKind,
                         @SourceProvider, @Provenance, @IsSupplemental, @Confidence);
                    """, qualifier, tx);
            }

            edge.Qualifiers = qualifiers;
        }, ct);
    }

    public Task<IReadOnlyList<EntityRelationship>> GetBySubjectAsync(string subjectQid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(LoadFacts("subject_qid = @qid", new { qid = subjectQid }));
    }

    public Task<IReadOnlyList<EntityRelationship>> GetByObjectAsync(string objectQid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(LoadFacts("object_qid = @qid", new { qid = objectQid }));
    }

    public Task<IReadOnlyList<EntityRelationship>> GetByObjectsAsync(IEnumerable<string> objectQids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(objectQids);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(LoadFactsByQids("object_qid", objectQids));
    }

    public Task<IReadOnlyList<EntityRelationship>> GetByEntityAsync(string qid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(LoadFacts("subject_qid = @qid OR object_qid = @qid", new { qid }));
    }

    public Task<IReadOnlyList<EntityRelationship>> GetByUniverseAsync(IReadOnlyCollection<string> entityQids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entityQids);
        ct.ThrowIfCancellationRequested();
        var qids = NormalizeQids(entityQids);
        if (qids.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<EntityRelationship>>([]);
        }

        var facts = LoadFactsByQids("subject_qid", qids);
        var qidSet = qids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyList<EntityRelationship>>(
            facts.Where(fact => qidSet.Contains(fact.ObjectQid)).ToList());
    }

    public Task<IReadOnlyList<EntityRelationship>> GetByUniverseAsync(
        IReadOnlyCollection<string> entityQids,
        IReadOnlyCollection<string> relationshipTypes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entityQids);
        ArgumentNullException.ThrowIfNull(relationshipTypes);
        ct.ThrowIfCancellationRequested();
        var qids = NormalizeQids(entityQids);
        var types = relationshipTypes
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => type.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (qids.Count == 0 || types.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<EntityRelationship>>([]);
        }

        var facts = LoadFactsByQids("subject_qid", qids, types);
        var qidSet = qids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult<IReadOnlyList<EntityRelationship>>(
            facts.Where(fact => qidSet.Contains(fact.ObjectQid)).ToList());
    }

    public Task<IReadOnlyList<EntityRelationship>> GetByQualifierAsync(
        string qualifierType,
        string value,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifierType);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Task.FromResult(LoadFacts("""
            id IN (
                SELECT relationship_id
                FROM entity_relationship_qualifiers
                WHERE qualifier_type = @qualifierType AND value = @value)
            """, new { qualifierType, value }));
    }

    public Task<IReadOnlyList<EntityRelationshipQualifier>> GetQualifiersAsync(Guid relationshipId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        return Task.FromResult<IReadOnlyList<EntityRelationshipQualifier>>(LoadQualifiers(conn, [relationshipId])
            .GetValueOrDefault(relationshipId, []));
    }

    public Task<int> CountAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        return Task.FromResult(conn.ExecuteScalar<int>("SELECT COUNT(*) FROM entity_relationships;"));
    }

    private IReadOnlyList<EntityRelationship> LoadFacts(string whereClause, object parameters)
    {
        using var conn = _db.CreateConnection();
        var rows = conn.Query<EntityRelationshipRow>($"""
            {FactSelect}
            WHERE {whereClause}
            ORDER BY relationship_type, object_qid, statement_key;
            """, parameters).AsList();
        return MaterializeFacts(conn, rows);
    }

    private IReadOnlyList<EntityRelationship> LoadFactsByQids(
        string column,
        IEnumerable<string> rawQids,
        IReadOnlyCollection<string>? relationshipTypes = null)
    {
        var qids = NormalizeQids(rawQids);
        if (qids.Count == 0)
        {
            return [];
        }

        var typeClause = relationshipTypes is null ? string.Empty : "AND relationship_type COLLATE NOCASE IN @types";
        using var conn = _db.CreateConnection();
        var rows = new List<EntityRelationshipRow>();
        foreach (var batch in qids.Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            rows.AddRange(conn.Query<EntityRelationshipRow>($"""
                {FactSelect}
                WHERE {column} COLLATE NOCASE IN @qids
                  {typeClause}
                ORDER BY relationship_type, object_qid, statement_key;
                """, new { qids = batch, types = relationshipTypes?.ToArray() }));
        }

        return MaterializeFacts(conn, rows);
    }

    private static IReadOnlyList<EntityRelationship> MaterializeFacts(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        IReadOnlyList<EntityRelationshipRow> rows)
    {
        var qualifiers = LoadQualifiers(conn, rows.Select(row => row.Id));
        return rows.Select(row => new EntityRelationship
        {
            Id = row.Id,
            StatementKey = row.StatementKey,
            SubjectQid = row.SubjectQid,
            RelationshipTypeValue = row.RelationshipTypeValue,
            ObjectQid = row.ObjectQid,
            Confidence = row.Confidence,
            ContextWorkQid = row.ContextWorkQid,
            SourceProvider = row.SourceProvider,
            Provenance = row.Provenance,
            IsSupplemental = row.IsSupplemental,
            DiscoveredAt = row.DiscoveredAt,
            StartTime = row.StartTime,
            EndTime = row.EndTime,
            Qualifiers = qualifiers.GetValueOrDefault(row.Id, []),
        }).ToList();
    }

    private static Dictionary<Guid, List<EntityRelationshipQualifier>> LoadQualifiers(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        IEnumerable<Guid> rawRelationshipIds)
    {
        var ids = rawRelationshipIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        var result = ids.ToDictionary(id => id, _ => new List<EntityRelationshipQualifier>());
        foreach (var batch in ids.Chunk(SqliteBatching.MaxParametersPerQuery))
        {
            var rows = conn.Query<EntityRelationshipQualifier>("""
                SELECT id AS Id, relationship_id AS RelationshipId,
                       qualifier_type AS QualifierType, value AS Value,
                       value_kind AS ValueKind, source_provider AS SourceProvider,
                       provenance AS Provenance, is_supplemental AS IsSupplemental,
                       confidence AS Confidence
                FROM entity_relationship_qualifiers
                WHERE relationship_id IN @relationshipIds
                ORDER BY qualifier_type, value, id;
                """, new { relationshipIds = batch.Select(GuidSql.ToBlob).ToArray() });
            foreach (var row in rows)
            {
                if (result.TryGetValue(row.RelationshipId, out var list))
                {
                    list.Add(row);
                }
            }
        }

        return result;
    }

    private static List<EntityRelationshipQualifier> NormalizeQualifiers(EntityRelationship edge)
    {
        var qualifiers = (edge.Qualifiers ?? [])
            .Where(qualifier => !string.IsNullOrWhiteSpace(qualifier.QualifierType) && !string.IsNullOrWhiteSpace(qualifier.Value))
            .Select(qualifier => new EntityRelationshipQualifier
            {
                Id = qualifier.Id == Guid.Empty ? Guid.NewGuid() : qualifier.Id,
                QualifierType = qualifier.QualifierType.Trim(),
                Value = qualifier.Value.Trim(),
                ValueKind = string.IsNullOrWhiteSpace(qualifier.ValueKind) ? "Text" : qualifier.ValueKind.Trim(),
                SourceProvider = qualifier.SourceProvider,
                Provenance = string.IsNullOrWhiteSpace(qualifier.Provenance) ? edge.Provenance : qualifier.Provenance,
                IsSupplemental = qualifier.IsSupplemental || edge.IsSupplemental,
                Confidence = qualifier.Confidence,
            })
            .GroupBy(qualifier => string.Join('|',
                qualifier.QualifierType,
                qualifier.Value,
                qualifier.ValueKind,
                qualifier.SourceProvider ?? string.Empty,
                qualifier.Provenance,
                qualifier.IsSupplemental ? "1" : "0"), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        AddConvenienceQualifier(qualifiers, GraphQualifierType.AppliesToWork, edge.ContextWorkQid, "Qid", edge);
        AddConvenienceQualifier(qualifiers, GraphQualifierType.StartTime, edge.StartTime, "Time", edge);
        AddConvenienceQualifier(qualifiers, GraphQualifierType.EndTime, edge.EndTime, "Time", edge);
        return qualifiers;
    }

    private static void AddConvenienceQualifier(List<EntityRelationshipQualifier> qualifiers, string qualifierType, string? value, string valueKind, EntityRelationship edge)
    {
        if (string.IsNullOrWhiteSpace(value) || qualifiers.Any(qualifier =>
                string.Equals(qualifier.QualifierType, qualifierType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(qualifier.Value, value, StringComparison.Ordinal)))
        {
            return;
        }

        qualifiers.Add(new EntityRelationshipQualifier
        {
            QualifierType = qualifierType,
            Value = value,
            ValueKind = valueKind,
            SourceProvider = edge.SourceProvider,
            Provenance = edge.Provenance,
            IsSupplemental = edge.IsSupplemental,
        });
    }

    private static string BuildStatementKey(EntityRelationship edge, IReadOnlyList<EntityRelationshipQualifier> qualifiers)
    {
        var canonical = string.Join('|',
            edge.SubjectQid.Trim(), edge.RelationshipTypeValue.Trim(), edge.ObjectQid.Trim(),
            edge.ContextWorkQid?.Trim() ?? string.Empty, edge.StartTime?.Trim() ?? string.Empty,
            edge.EndTime?.Trim() ?? string.Empty, edge.SourceProvider?.Trim() ?? string.Empty,
            edge.Provenance.Trim(), edge.IsSupplemental ? "1" : "0",
            string.Join(';', qualifiers
                .OrderBy(qualifier => qualifier.QualifierType, StringComparer.Ordinal)
                .ThenBy(qualifier => qualifier.Value, StringComparer.Ordinal)
                .ThenBy(qualifier => qualifier.ValueKind, StringComparer.Ordinal)
                .ThenBy(qualifier => qualifier.SourceProvider, StringComparer.Ordinal)
                .ThenBy(qualifier => qualifier.Provenance, StringComparer.Ordinal)
                .ThenBy(qualifier => qualifier.IsSupplemental)
                .Select(qualifier => $"{qualifier.QualifierType}={qualifier.Value}:{qualifier.ValueKind}:{qualifier.SourceProvider}:{qualifier.Provenance}:{qualifier.IsSupplemental}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void ValidateFact(EntityRelationship edge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(edge.SubjectQid);
        ArgumentException.ThrowIfNullOrWhiteSpace(edge.RelationshipTypeValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(edge.ObjectQid);
        edge.Provenance = string.IsNullOrWhiteSpace(edge.Provenance) ? "Wikidata" : edge.Provenance.Trim();
    }

    private static List<string> NormalizeQids(IEnumerable<string> qids) => qids
        .Where(qid => !string.IsNullOrWhiteSpace(qid))
        .Select(qid => qid.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private const string FactSelect = """
        SELECT id AS Id, statement_key AS StatementKey, subject_qid AS SubjectQid,
               relationship_type AS RelationshipTypeValue, object_qid AS ObjectQid,
               confidence AS Confidence, context_work_qid AS ContextWorkQid,
               source_provider AS SourceProvider, provenance AS Provenance,
               is_supplemental AS IsSupplemental, discovered_at AS DiscoveredAt,
               start_time AS StartTime, end_time AS EndTime
        FROM entity_relationships
        """;

    private sealed class EntityRelationshipRow
    {
        public Guid Id { get; set; }
        public string StatementKey { get; set; } = string.Empty;
        public string SubjectQid { get; set; } = string.Empty;
        public string RelationshipTypeValue { get; set; } = string.Empty;
        public string ObjectQid { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string? ContextWorkQid { get; set; }
        public string? SourceProvider { get; set; }
        public string Provenance { get; set; } = "Wikidata";
        public bool IsSupplemental { get; set; }
        public DateTimeOffset DiscoveredAt { get; set; }
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
    }
}
