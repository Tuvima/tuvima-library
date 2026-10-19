using System.Text.Json.Serialization;

namespace MediaEngine.Api.Models;

public sealed class UniverseManualAssignRequest
{
    [JsonPropertyName("work_id")] public Guid WorkId { get; init; }
    [JsonPropertyName("collection_id")] public Guid CollectionId { get; init; }
}
