using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// SQLite implementation of <see cref="IEntityTimelineRepository"/>.
/// Manages the <c>entity_events</c> and <c>entity_field_changes</c> tables.
/// </summary>
public sealed class EntityTimelineRepository : IEntityTimelineRepository
{
    private readonly IDatabaseConnection _db;

    public EntityTimelineRepository(IDatabaseConnection db) => _db = db;

    public Task InsertEventAsync(EntityEvent evt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync(
            (conn, tx, innerCt) => InsertEventCore(conn, tx, evt, innerCt),
            ct);
    }

    public Task InsertEventsAsync(IReadOnlyList<EntityEvent> events, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (events.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            foreach (var evt in events)
            {
                InsertEventCore(conn, tx, evt, innerCt);
            }
        }, ct);
    }

    private static void InsertEventCore(
        SqliteConnection conn,
        SqliteTransaction? tx,
        EntityEvent evt,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        const string sql = """
            INSERT INTO entity_events (
                id, entity_id, entity_type, event_type, stage, trigger,
                provider_id, provider_name, bridge_id_type, bridge_id_value,
                resolved_qid, confidence,
                score_title, score_author, score_year, score_format,
                score_cross_field, score_cover_art, score_composite,
                occurred_at, detail
            ) VALUES (
                @id, @entity_id, @entity_type, @event_type, @stage, @trigger,
                @provider_id, @provider_name, @bridge_id_type, @bridge_id_value,
                @resolved_qid, @confidence,
                @score_title, @score_author, @score_year, @score_format,
                @score_cross_field, @score_cover_art, @score_composite,
                @occurred_at, @detail
            )
            """;

        using var cmd = new SqliteCommand(sql, conn, tx);
        cmd.Parameters.Add("@id", SqliteType.Blob).Value = GuidSql.ToBlob(evt.Id);
        cmd.Parameters.Add("@entity_id", SqliteType.Blob).Value = GuidSql.ToBlob(evt.EntityId);
        cmd.Parameters.AddWithValue("@entity_type", evt.EntityType);
        cmd.Parameters.AddWithValue("@event_type", evt.EventType);
        cmd.Parameters.AddWithValue("@stage", evt.Stage.HasValue ? evt.Stage.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@trigger", evt.Trigger);
        cmd.Parameters.AddWithValue("@provider_id", (object?)evt.ProviderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@provider_name", (object?)evt.ProviderName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bridge_id_type", (object?)evt.BridgeIdType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bridge_id_value", (object?)evt.BridgeIdValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@resolved_qid", (object?)evt.ResolvedQid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@confidence", evt.Confidence.HasValue ? evt.Confidence.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@score_title", evt.ScoreTitle.HasValue ? evt.ScoreTitle.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@score_author", evt.ScoreAuthor.HasValue ? evt.ScoreAuthor.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@score_year", evt.ScoreYear.HasValue ? evt.ScoreYear.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@score_format", evt.ScoreFormat.HasValue ? evt.ScoreFormat.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@score_cross_field", evt.ScoreCrossField.HasValue ? evt.ScoreCrossField.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@score_cover_art", evt.ScoreCoverArt.HasValue ? evt.ScoreCoverArt.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@score_composite", evt.ScoreComposite.HasValue ? evt.ScoreComposite.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@occurred_at", evt.OccurredAt.ToString("o"));
        cmd.Parameters.AddWithValue("@detail", (object?)evt.Detail ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public Task<IReadOnlyList<EntityEvent>> GetEventsByEntityAsync(
        Guid entityId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        const string sql = """
            SELECT id, entity_id, entity_type, event_type, stage, trigger,
                   provider_id, provider_name, bridge_id_type, bridge_id_value,
                   resolved_qid, confidence,
                   score_title, score_author, score_year, score_format,
                   score_cross_field, score_cover_art, score_composite,
                   occurred_at, detail
            FROM entity_events
            WHERE entity_id = @entity_id
            ORDER BY occurred_at DESC
            """;

        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.Add("@entity_id", SqliteType.Blob).Value = GuidSql.ToBlob(entityId);
        return Task.FromResult<IReadOnlyList<EntityEvent>>(ReadEvents(cmd, ct));
    }

    public Task<EntityEvent?> GetLatestEventAsync(
        Guid entityId,
        int stage,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        const string sql = """
            SELECT id, entity_id, entity_type, event_type, stage, trigger,
                   provider_id, provider_name, bridge_id_type, bridge_id_value,
                   resolved_qid, confidence,
                   score_title, score_author, score_year, score_format,
                   score_cross_field, score_cover_art, score_composite,
                   occurred_at, detail
            FROM entity_events
            WHERE entity_id = @entity_id AND stage = @stage
            ORDER BY occurred_at DESC
            LIMIT 1
            """;

        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.Add("@entity_id", SqliteType.Blob).Value = GuidSql.ToBlob(entityId);
        cmd.Parameters.AddWithValue("@stage", stage);
        var results = ReadEvents(cmd, ct);
        return Task.FromResult<EntityEvent?>(results.Count > 0 ? results[0] : null);
    }

    public Task<IReadOnlyList<EntityEvent>> GetCurrentPipelineStateAsync(
        Guid entityId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        const string sql = """
            SELECT id, entity_id, entity_type, event_type, stage, trigger,
                   provider_id, provider_name, bridge_id_type, bridge_id_value,
                   resolved_qid, confidence,
                   score_title, score_author, score_year, score_format,
                   score_cross_field, score_cover_art, score_composite,
                   occurred_at, detail
            FROM entity_events e1
            WHERE entity_id = @entity_id
              AND stage IS NOT NULL
              AND occurred_at = (
                  SELECT MAX(e2.occurred_at) FROM entity_events e2
                  WHERE e2.entity_id = e1.entity_id AND e2.stage = e1.stage
              )
            ORDER BY stage ASC
            """;

        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.Add("@entity_id", SqliteType.Blob).Value = GuidSql.ToBlob(entityId);
        return Task.FromResult<IReadOnlyList<EntityEvent>>(ReadEvents(cmd, ct));
    }

    public Task<EntityEvent?> GetEventByIdAsync(Guid eventId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        const string sql = """
            SELECT id, entity_id, entity_type, event_type, stage, trigger,
                   provider_id, provider_name, bridge_id_type, bridge_id_value,
                   resolved_qid, confidence,
                   score_title, score_author, score_year, score_format,
                   score_cross_field, score_cover_art, score_composite,
                   occurred_at, detail
            FROM entity_events
            WHERE id = @id
            """;

        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.Add("@id", SqliteType.Blob).Value = GuidSql.ToBlob(eventId);
        var results = ReadEvents(cmd, ct);
        return Task.FromResult<EntityEvent?>(results.Count > 0 ? results[0] : null);
    }

    public Task InsertFieldChangesAsync(
        IReadOnlyList<EntityFieldChange> changes,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (changes.Count == 0)
        {
            return Task.CompletedTask;
        }

        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            const string sql = """
                INSERT INTO entity_field_changes (
                    id, event_id, entity_id, field,
                    old_value, new_value, old_provider_id, new_provider_id,
                    confidence, is_file_original
                ) VALUES (
                    @id, @event_id, @entity_id, @field,
                    @old_value, @new_value, @old_provider_id, @new_provider_id,
                    @confidence, @is_file_original
                )
                """;

            foreach (var change in changes)
            {
                innerCt.ThrowIfCancellationRequested();
                using var cmd = new SqliteCommand(sql, conn, tx);
                cmd.Parameters.Add("@id", SqliteType.Blob).Value = GuidSql.ToBlob(change.Id);
                cmd.Parameters.Add("@event_id", SqliteType.Blob).Value = GuidSql.ToBlob(change.EventId);
                cmd.Parameters.Add("@entity_id", SqliteType.Blob).Value = GuidSql.ToBlob(change.EntityId);
                cmd.Parameters.AddWithValue("@field", change.Field);
                cmd.Parameters.AddWithValue("@old_value", (object?)change.OldValue ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@new_value", (object?)change.NewValue ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@old_provider_id", (object?)change.OldProviderId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@new_provider_id", (object?)change.NewProviderId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@confidence", change.Confidence.HasValue ? change.Confidence.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@is_file_original", change.IsFileOriginal ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
        }, ct);
    }

    public Task<IReadOnlyList<EntityFieldChange>> GetFieldChangesByEventAsync(
        Guid eventId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        const string sql = """
            SELECT id, event_id, entity_id, field,
                   old_value, new_value, old_provider_id, new_provider_id,
                   confidence, is_file_original
            FROM entity_field_changes
            WHERE event_id = @event_id
            ORDER BY field ASC
            """;

        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.Add("@event_id", SqliteType.Blob).Value = GuidSql.ToBlob(eventId);
        return Task.FromResult<IReadOnlyList<EntityFieldChange>>(ReadFieldChanges(cmd, ct));
    }

    public Task<IReadOnlyList<EntityFieldChange>> GetFieldHistoryAsync(
        Guid entityId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        const string sql = """
            SELECT fc.id, fc.event_id, fc.entity_id, fc.field,
                   fc.old_value, fc.new_value, fc.old_provider_id, fc.new_provider_id,
                   fc.confidence, fc.is_file_original
            FROM entity_field_changes fc
            INNER JOIN entity_events e ON e.id = fc.event_id
            WHERE fc.entity_id = @entity_id
            ORDER BY e.occurred_at DESC, fc.field ASC
            """;

        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.Add("@entity_id", SqliteType.Blob).Value = GuidSql.ToBlob(entityId);
        return Task.FromResult<IReadOnlyList<EntityFieldChange>>(ReadFieldChanges(cmd, ct));
    }

    public Task<IReadOnlyList<EntityFieldChange>> GetFieldHistoryAsync(
        Guid entityId,
        string field,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        const string sql = """
            SELECT fc.id, fc.event_id, fc.entity_id, fc.field,
                   fc.old_value, fc.new_value, fc.old_provider_id, fc.new_provider_id,
                   fc.confidence, fc.is_file_original
            FROM entity_field_changes fc
            INNER JOIN entity_events e ON e.id = fc.event_id
            WHERE fc.entity_id = @entity_id AND fc.field = @field
            ORDER BY e.occurred_at DESC
            """;

        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.Add("@entity_id", SqliteType.Blob).Value = GuidSql.ToBlob(entityId);
        cmd.Parameters.AddWithValue("@field", field);
        return Task.FromResult<IReadOnlyList<EntityFieldChange>>(ReadFieldChanges(cmd, ct));
    }

    public Task<IReadOnlyList<EntityFieldChange>> GetFileOriginalsForEventAsync(
        Guid eventId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        const string sql = """
            SELECT id, event_id, entity_id, field,
                   old_value, new_value, old_provider_id, new_provider_id,
                   confidence, is_file_original
            FROM entity_field_changes
            WHERE event_id = @event_id AND is_file_original = 1
            ORDER BY field ASC
            """;

        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.Add("@event_id", SqliteType.Blob).Value = GuidSql.ToBlob(eventId);
        return Task.FromResult<IReadOnlyList<EntityFieldChange>>(ReadFieldChanges(cmd, ct));
    }

    public Task<IReadOnlyDictionary<Guid, EntityEvent>> GetLatestStage2EventsAsync(
        IReadOnlyList<Guid> entityIds,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (entityIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<Guid, EntityEvent>>(
                new Dictionary<Guid, EntityEvent>());
        }

        // The temp table is connection-scoped, so population and selection share
        // one structurally locked transaction and one physical connection.
        return _db.ExecuteWriteAsync<IReadOnlyDictionary<Guid, EntityEvent>>((conn, tx, innerCt) =>
        {
            using (var createCmd = new SqliteCommand(
                "CREATE TEMP TABLE IF NOT EXISTS _tmp_entity_ids (id BLOB NOT NULL PRIMARY KEY)",
                conn,
                tx))
            {
                createCmd.ExecuteNonQuery();
            }

            using (var clearCmd = new SqliteCommand("DELETE FROM _tmp_entity_ids", conn, tx))
            {
                clearCmd.ExecuteNonQuery();
            }

            foreach (var entityId in entityIds)
            {
                innerCt.ThrowIfCancellationRequested();
                using var insertCmd = new SqliteCommand(
                    "INSERT OR IGNORE INTO _tmp_entity_ids (id) VALUES (@id)",
                    conn,
                    tx);
                insertCmd.Parameters.Add("@id", SqliteType.Blob).Value = GuidSql.ToBlob(entityId);
                insertCmd.ExecuteNonQuery();
            }

            const string sql = """
                SELECT e.id, e.entity_id, e.entity_type, e.event_type, e.stage, e.trigger,
                       e.provider_id, e.provider_name, e.bridge_id_type, e.bridge_id_value,
                       e.resolved_qid, e.confidence,
                       e.score_title, e.score_author, e.score_year, e.score_format,
                       e.score_cross_field, e.score_cover_art, e.score_composite,
                       e.occurred_at, e.detail
                FROM entity_events e
                INNER JOIN _tmp_entity_ids t ON t.id = e.entity_id
                WHERE e.stage = 2
                  AND e.occurred_at = (
                      SELECT MAX(e2.occurred_at) FROM entity_events e2
                      WHERE e2.entity_id = e.entity_id AND e2.stage = 2
                  )
                """;

            using var cmd = new SqliteCommand(sql, conn, tx);
            var events = ReadEvents(cmd, innerCt);
            var result = new Dictionary<Guid, EntityEvent>();
            foreach (var evt in events)
            {
                result.TryAdd(evt.EntityId, evt);
            }

            return result;
        }, ct);
    }

    public Task<int> CullOldEventsAsync(TimeSpan retention, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var cutoff = DateTimeOffset.UtcNow.Subtract(retention).ToString("o");
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            const string sql = """
                DELETE FROM entity_events
                WHERE occurred_at < @cutoff
                  AND id NOT IN (
                      SELECT id FROM (
                          SELECT id, ROW_NUMBER() OVER (
                              PARTITION BY entity_id, stage ORDER BY occurred_at DESC
                          ) AS rn
                          FROM entity_events
                          WHERE stage IS NOT NULL
                      ) ranked
                      WHERE rn = 1
                  )
                """;

            using var cmd = new SqliteCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            return cmd.ExecuteNonQuery();
        }, ct);
    }

    public Task DeleteByEntityAsync(Guid entityId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return _db.ExecuteWriteAsync((conn, tx, innerCt) =>
        {
            innerCt.ThrowIfCancellationRequested();
            const string sql = "DELETE FROM entity_events WHERE entity_id = @entity_id";
            using var cmd = new SqliteCommand(sql, conn, tx);
            cmd.Parameters.Add("@entity_id", SqliteType.Blob).Value = GuidSql.ToBlob(entityId);
            cmd.ExecuteNonQuery();
        }, ct);
    }

    private static List<EntityEvent> ReadEvents(SqliteCommand cmd, CancellationToken ct)
    {
        var results = new List<EntityEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            results.Add(new EntityEvent
            {
                Id = GuidSql.FromDb(reader.GetValue(0)),
                EntityId = GuidSql.FromDb(reader.GetValue(1)),
                EntityType = reader.GetString(2),
                EventType = reader.GetString(3),
                Stage = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Trigger = reader.GetString(5),
                ProviderId = reader.IsDBNull(6) ? null : reader.GetString(6),
                ProviderName = reader.IsDBNull(7) ? null : reader.GetString(7),
                BridgeIdType = reader.IsDBNull(8) ? null : reader.GetString(8),
                BridgeIdValue = reader.IsDBNull(9) ? null : reader.GetString(9),
                ResolvedQid = reader.IsDBNull(10) ? null : reader.GetString(10),
                Confidence = reader.IsDBNull(11) ? null : reader.GetDouble(11),
                ScoreTitle = reader.IsDBNull(12) ? null : reader.GetDouble(12),
                ScoreAuthor = reader.IsDBNull(13) ? null : reader.GetDouble(13),
                ScoreYear = reader.IsDBNull(14) ? null : reader.GetDouble(14),
                ScoreFormat = reader.IsDBNull(15) ? null : reader.GetDouble(15),
                ScoreCrossField = reader.IsDBNull(16) ? null : reader.GetDouble(16),
                ScoreCoverArt = reader.IsDBNull(17) ? null : reader.GetDouble(17),
                ScoreComposite = reader.IsDBNull(18) ? null : reader.GetDouble(18),
                OccurredAt = DateTimeOffset.Parse(reader.GetString(19)),
                Detail = reader.IsDBNull(20) ? null : reader.GetString(20),
            });
        }

        return results;
    }

    private static List<EntityFieldChange> ReadFieldChanges(
        SqliteCommand cmd,
        CancellationToken ct)
    {
        var results = new List<EntityFieldChange>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            results.Add(new EntityFieldChange
            {
                Id = GuidSql.FromDb(reader.GetValue(0)),
                EventId = GuidSql.FromDb(reader.GetValue(1)),
                EntityId = GuidSql.FromDb(reader.GetValue(2)),
                Field = reader.GetString(3),
                OldValue = reader.IsDBNull(4) ? null : reader.GetString(4),
                NewValue = reader.IsDBNull(5) ? null : reader.GetString(5),
                OldProviderId = reader.IsDBNull(6) ? null : reader.GetString(6),
                NewProviderId = reader.IsDBNull(7) ? null : reader.GetString(7),
                Confidence = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                IsFileOriginal = !reader.IsDBNull(9) && reader.GetInt32(9) == 1,
            });
        }

        return results;
    }
}
