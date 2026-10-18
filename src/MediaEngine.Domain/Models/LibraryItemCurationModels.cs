using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Models;

public sealed record LibraryItemHistoryEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("entity_id")] Guid EntityId,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("actor_label")] string ActorLabel);
