using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Domain.Enums;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Multi-step lookups that a provider file spells out for the two things the Library asks of a
/// provider: <c>fetch</c> (claims for one item) and <c>search</c> (a list of candidates). Each is a
/// short program of steps (see <see cref="FlowParser"/> for the vocabulary). Declared in
/// <c>flows</c> of a provider file.
/// </summary>
public sealed class ProviderFlowsConfiguration
{
    [JsonPropertyName("fetch")]
    public ProviderFlowConfiguration? Fetch { get; set; }

    [JsonPropertyName("search")]
    public ProviderFlowConfiguration? Search { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }
}

public sealed class ProviderFlowConfiguration
{
    /// <summary>Media types the flow answers; any other request gets an empty answer. Empty = all.</summary>
    [JsonPropertyName("media_types")]
    public List<string> MediaTypes { get; set; } = [];

    /// <summary>Entity types the flow answers. Empty = all.</summary>
    [JsonPropertyName("entity_types")]
    public List<string> EntityTypes { get; set; } = [];

    /// <summary>The flow answers only while the provider is enabled and has the credentials it signs in with.</summary>
    [JsonPropertyName("requires_credentials")]
    public bool RequiresCredentials { get; set; }

    /// <summary>The steps, run in order.</summary>
    [JsonPropertyName("steps")]
    public List<JsonElement> Steps { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnmappedProperties { get; set; }

    public bool AppliesTo(MediaType mediaType, EntityType entityType) =>
        (MediaTypes.Count == 0
            || MediaTypes.Any(name => string.Equals(name, mediaType.ToString(), StringComparison.OrdinalIgnoreCase)))
        && (EntityTypes.Count == 0
            || EntityTypes.Any(name => string.Equals(name, entityType.ToString(), StringComparison.OrdinalIgnoreCase)));
}
