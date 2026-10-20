using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Activity;

public sealed class ActivityEntryResponse
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("occurred_at")]
    public string OccurredAt { get; set; } = string.Empty;

    [JsonPropertyName("action_type")]
    public string ActionType { get; set; } = string.Empty;

    [JsonPropertyName("collection_name")]
    public string? CollectionName { get; set; }

    [JsonPropertyName("entity_id")]
    public string? EntityId { get; set; }

    [JsonPropertyName("entity_type")]
    public string? EntityType { get; set; }

    [JsonPropertyName("profile_id")]
    public string? ProfileId { get; set; }

    [JsonPropertyName("changes_json")]
    public string? ChangesJson { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}

public sealed class PruneResponse
{
    [JsonPropertyName("deleted")]
    public int Deleted { get; set; }

    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; set; }
}

public sealed class ActivityStatsResponse
{
    [JsonPropertyName("total_entries")]
    public long TotalEntries { get; set; }

    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; set; }
}
