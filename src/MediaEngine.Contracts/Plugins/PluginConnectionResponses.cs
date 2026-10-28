using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Plugins;

/// <summary>What the signed-in profile has linked for one plugin (Plugin architecture spec C13). Never carries a value.</summary>
public sealed class PluginConnectionsResponse
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    [JsonPropertyName("plugin_name")]
    public string PluginName { get; set; } = "";

    /// <summary>One line on what linking shares, shown beside the consent tick.</summary>
    [JsonPropertyName("consent")]
    public string Consent { get; set; } = "";

    [JsonPropertyName("connections")]
    public List<PluginConnectionDto> Connections { get; set; } = [];
}

/// <summary>One personal credential a plugin asks each profile to link.</summary>
public sealed class PluginConnectionDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("linked")]
    public bool Linked { get; set; }

    [JsonPropertyName("linked_at")]
    public DateTimeOffset? LinkedAt { get; set; }
}

/// <summary>Body for linking a personal credential. <c>agreed</c> must be true: linking is a consent step.</summary>
public sealed class PluginConnectionLinkRequest
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("agreed")]
    public bool Agreed { get; set; }
}
