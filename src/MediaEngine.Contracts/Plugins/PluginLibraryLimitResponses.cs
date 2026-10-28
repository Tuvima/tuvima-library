using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Plugins;

/// <summary>Where a plugin or source may work (Plugin architecture spec D6): every library, or only the ones an administrator chose.</summary>
public sealed class PluginLibraryLimitResponse
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    /// <summary>False (the default) when the item works on every library it can work on.</summary>
    [JsonPropertyName("limited")]
    public bool Limited { get; set; }

    /// <summary>Every library an administrator may choose, with whether it is chosen. Vault libraries are never listed.</summary>
    [JsonPropertyName("libraries")]
    public List<PluginLibraryChoiceDto> Libraries { get; set; } = [];
}

public sealed class PluginLibraryChoiceDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("selected")]
    public bool Selected { get; set; }
}

/// <summary>The libraries to limit a plugin or source to; an empty list removes the limit.</summary>
public sealed class PluginLibraryLimitRequest
{
    [JsonPropertyName("library_ids")]
    public List<string> LibraryIds { get; set; } = [];
}
