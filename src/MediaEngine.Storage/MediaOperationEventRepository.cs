using Dapper;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage;

public sealed class MediaOperationEventRepository : IMediaOperationEventRepository
{
    private readonly IDatabaseConnection _db;

    public MediaOperationEventRepository(IDatabaseConnection db) => _db = db;

    public Task AddAsync(MediaOperationEvent evt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        conn.Execute("""
            INSERT INTO media_operation_events
                (id, operation_id, entity_id, event_type, old_status, new_status,
                 old_stage, new_stage, message, detail_json, occurred_at)
            VALUES
                (@Id, @OperationId, @EntityId, @EventType, @OldStatus, @NewStatus,
                 @OldStage, @NewStage, @Message, @DetailJson, @OccurredAt);
            """,
            new
            {
                Id = evt.Id,
                OperationId = evt.OperationId,
                EntityId = evt.EntityId,
                evt.EventType,
                evt.OldStatus,
                evt.NewStatus,
                evt.OldStage,
                evt.NewStage,
                evt.Message,
                evt.DetailJson,
                OccurredAt = evt.OccurredAt.ToString("O")
            });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MediaOperationEvent>> GetByOperationAsync(Guid operationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<Row>(
            SelectSql + " WHERE operation_id = @operationId ORDER BY occurred_at ASC;",
            new { operationId });
        return Task.FromResult<IReadOnlyList<MediaOperationEvent>>(rows.Select(Map).ToList());
    }

    public Task<IReadOnlyList<MediaOperationEvent>> GetByEntityAsync(Guid entityId, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var conn = _db.CreateConnection();
        var rows = conn.Query<Row>(
            SelectSql + " WHERE entity_id = @entityId ORDER BY occurred_at DESC LIMIT @limit;",
            new { entityId, limit = Math.Clamp(limit, 1, 1000) });
        return Task.FromResult<IReadOnlyList<MediaOperationEvent>>(rows.Select(Map).ToList());
    }

    private const string SelectSql = """
        SELECT id AS Id,
               operation_id AS OperationId,
               entity_id AS EntityId,
               event_type AS EventType,
               old_status AS OldStatus,
               new_status AS NewStatus,
               old_stage AS OldStage,
               new_stage AS NewStage,
               message AS Message,
               detail_json AS DetailJson,
               occurred_at AS OccurredAt
        FROM media_operation_events
        """;

    private sealed class Row
    {
        public Guid Id { get; set; }
        public Guid OperationId { get; set; }
        public Guid? EntityId { get; set; }
        public string EventType { get; set; } = "";
        public string? OldStatus { get; set; }
        public string? NewStatus { get; set; }
        public string? OldStage { get; set; }
        public string? NewStage { get; set; }
        public string? Message { get; set; }
        public string? DetailJson { get; set; }
        public string OccurredAt { get; set; } = "";
    }

    private static MediaOperationEvent Map(Row row) => new()
    {
        Id = row.Id,
        OperationId = row.OperationId,
        EntityId = row.EntityId,
        EventType = row.EventType,
        OldStatus = row.OldStatus,
        NewStatus = row.NewStatus,
        OldStage = row.OldStage,
        NewStage = row.NewStage,
        Message = row.Message,
        DetailJson = row.DetailJson,
        OccurredAt = DateTimeOffset.Parse(row.OccurredAt)
    };
}
