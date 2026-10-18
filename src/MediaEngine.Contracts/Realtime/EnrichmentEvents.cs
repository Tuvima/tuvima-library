using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Realtime;

public sealed record MetadataHarvestedEvent(
    Guid EntityId,
    string ProviderName,
    IReadOnlyList<string> UpdatedFields);

public sealed record PersonEnrichedEvent(
    Guid PersonId,
    string Name,
    string? HeadshotUrl,
    string? WikidataQid);

public sealed record ReviewItemCreatedEvent(
    Guid ReviewItemId,
    Guid EntityId,
    string Trigger,
    string? EntityTitle);

public sealed record ReviewItemResolvedEvent(
    Guid ReviewItemId,
    Guid EntityId,
    string Status);

public sealed record HydrationStageCompletedEvent(
    Guid EntityId,
    int Stage,
    int ClaimsAdded,
    string ProviderName);

public sealed record ManualMetadataHarvestedEvent(
    [property: JsonPropertyName("entity_id")] Guid EntityId,
    [property: JsonPropertyName("provider_name")] string ProviderName,
    [property: JsonPropertyName("updated_fields")] IReadOnlyList<string> UpdatedFields);

public sealed record RetailMetadataHarvestedEvent(
    [property: JsonPropertyName("entity_id")] Guid EntityId,
    [property: JsonPropertyName("target_scope_id")] string TargetScopeId,
    [property: JsonPropertyName("target_field_group")] string TargetFieldGroup,
    [property: JsonPropertyName("provider_name")] string ProviderName,
    [property: JsonPropertyName("provider_item_id")] string ProviderItemId,
    [property: JsonPropertyName("updated_fields")] IReadOnlyList<string> UpdatedFields);

public sealed record WikidataMetadataHarvestedEvent(
    [property: JsonPropertyName("entity_id")] Guid EntityId,
    [property: JsonPropertyName("target_scope_id")] string TargetScopeId,
    [property: JsonPropertyName("target_field_group")] string TargetFieldGroup,
    [property: JsonPropertyName("provider_name")] string ProviderName,
    [property: JsonPropertyName("updated_fields")] IReadOnlyList<string> UpdatedFields);

public sealed record ReclassifiedMetadataHarvestedEvent(
    [property: JsonPropertyName("entity_id")] Guid EntityId,
    [property: JsonPropertyName("media_type")] string MediaType);

public sealed record ReviewItemResolvedSupplementaryEvent(
    [property: JsonPropertyName("review_item_id")] Guid ReviewItemId,
    [property: JsonPropertyName("entity_id")] Guid EntityId,
    [property: JsonPropertyName("status")] string Status);

public sealed record EntityReviewResolvedEvent(
    [property: JsonPropertyName("entity_id")] Guid EntityId,
    [property: JsonPropertyName("status")] string Status);
