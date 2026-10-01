using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Metadata;

public sealed record MediaEditorSelectionHistoryRequestDto(
    [property: JsonPropertyName("asset_ids")] IReadOnlyList<Guid> AssetIds);

public sealed record MediaEditorSelectionHistoryEntryDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("entity_id")] Guid EntityId,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("actor_label")] string ActorLabel,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("selected_asset_ids")] IReadOnlyList<Guid> SelectedAssetIds);

public sealed record MediaEditorSelectionHistoryDto(
    [property: JsonPropertyName("parent_entity_id")] Guid ParentEntityId,
    [property: JsonPropertyName("selected_asset_ids")] IReadOnlyList<Guid> SelectedAssetIds,
    [property: JsonPropertyName("items")] IReadOnlyList<MediaEditorSelectionHistoryEntryDto> Items)
{
    [JsonPropertyName("has_events")]
    public bool HasEvents => Items.Count > 0;
}
