using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

public sealed class SeriesMissingItemPreferenceDto
{
    [JsonPropertyName("profile_id")]
    public Guid ProfileId { get; init; }

    [JsonPropertyName("media_type")]
    public required string MediaType { get; init; }

    [JsonPropertyName("container_key")]
    public required string ContainerKey { get; init; }

    [JsonPropertyName("show_missing")]
    /// <summary>The profile's tick for this Series. A Series starts unticked, so no stored tick reads as false.</summary>
    public bool ShowMissing { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed class SaveSeriesMissingItemPreferenceRequest
{
    [JsonPropertyName("media_type")]
    public required string MediaType { get; init; }

    [JsonPropertyName("container_key")]
    public required string ContainerKey { get; init; }

    [JsonPropertyName("show_missing")]
    public bool ShowMissing { get; init; }
}
