using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Operations;

public sealed class OperationDetailDto
{
    [JsonPropertyName("operation")]
    public OperationDto Operation { get; set; } = new();

    [JsonPropertyName("events")]
    public List<OperationEventDto> Events { get; set; } = [];
}

public sealed record OperationDto
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("operation_type")] public string OperationType { get; set; } = "";
    [JsonPropertyName("operation_kind")] public string OperationKind { get; set; } = "";
    [JsonPropertyName("entity_id")] public Guid? EntityId { get; set; }
    [JsonPropertyName("entity_kind")] public string? EntityKind { get; set; }
    [JsonPropertyName("source_path")] public string? SourcePath { get; set; }
    [JsonPropertyName("capability_id")] public string? CapabilityId { get; set; }
    [JsonPropertyName("capability_version")] public string? CapabilityVersion { get; set; }
    [JsonPropertyName("sub_key")] public string? SubKey { get; set; }
    [JsonPropertyName("plugin_id")] public string? PluginId { get; set; }
    [JsonPropertyName("plugin_version")] public string? PluginVersion { get; set; }
    [JsonPropertyName("provider_id")] public string? ProviderId { get; set; }
    [JsonPropertyName("model_id")] public string? ModelId { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("stage")] public string? Stage { get; set; }
    [JsonPropertyName("priority")] public int Priority { get; set; }
    [JsonPropertyName("queue_name")] public string QueueName { get; set; } = "";
    [JsonPropertyName("queue_position")] public int? QueuePosition { get; set; }
    [JsonPropertyName("attempt_count")] public int AttemptCount { get; set; }
    [JsonPropertyName("lease_owner")] public string? LeaseOwner { get; set; }
    [JsonPropertyName("lease_expires_at")] public DateTimeOffset? LeaseExpiresAt { get; set; }
    [JsonPropertyName("heartbeat_at")] public DateTimeOffset? HeartbeatAt { get; set; }
    [JsonPropertyName("next_retry_at")] public DateTimeOffset? NextRetryAt { get; set; }
    [JsonPropertyName("progress_percent")] public int ProgressPercent { get; set; }
    [JsonPropertyName("items_total")] public int ItemsTotal { get; set; }
    [JsonPropertyName("items_completed")] public int ItemsCompleted { get; set; }
    [JsonPropertyName("items_failed")] public int ItemsFailed { get; set; }
    [JsonPropertyName("result_summary")] public string? ResultSummary { get; set; }
    [JsonPropertyName("last_error")] public string? LastError { get; set; }
    [JsonPropertyName("missing_reason")] public string? MissingReason { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
    [JsonPropertyName("completed_at")] public DateTimeOffset? CompletedAt { get; set; }
}

public sealed record OperationEventDto
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("operation_id")] public Guid OperationId { get; set; }
    [JsonPropertyName("entity_id")] public Guid? EntityId { get; set; }
    [JsonPropertyName("event_type")] public string EventType { get; set; } = "";
    [JsonPropertyName("old_status")] public string? OldStatus { get; set; }
    [JsonPropertyName("new_status")] public string? NewStatus { get; set; }
    [JsonPropertyName("old_stage")] public string? OldStage { get; set; }
    [JsonPropertyName("new_stage")] public string? NewStage { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("detail_json")] public string? DetailJson { get; set; }
    [JsonPropertyName("occurred_at")] public DateTimeOffset OccurredAt { get; set; }
}
