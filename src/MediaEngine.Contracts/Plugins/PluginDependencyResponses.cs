using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Plugins;

/// <summary>What a plugin needs from other plugins, who needs it, and whether it is waiting (Plugin architecture spec C22). Feeds the "Needs" and "Needed by" lists.</summary>
public sealed class PluginDependenciesResponse
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    /// <summary>"Waiting for X" when a required plugin is off, missing or at the wrong version; null when nothing holds it up.</summary>
    [JsonPropertyName("waiting_for")]
    public string? WaitingFor { get; set; }

    [JsonPropertyName("needs")]
    public List<PluginNeedDto> Needs { get; set; } = [];

    /// <summary>Switched-on plugins that need this one, directly or through another. Removing or switching this one off offers to switch them off too.</summary>
    [JsonPropertyName("needed_by")]
    public List<PluginNeededByDto> NeededBy { get; set; } = [];
}

public sealed class PluginNeedDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>True when the plugin only works better with it.</summary>
    [JsonPropertyName("optional")]
    public bool Optional { get; set; }

    [JsonPropertyName("version_range")]
    public string? VersionRange { get; set; }

    /// <summary><c>ready</c>, <c>off</c>, <c>missing</c>, <c>wrong_version</c> or <c>broken</c>.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "ready";
}

public sealed class PluginNeededByDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}
