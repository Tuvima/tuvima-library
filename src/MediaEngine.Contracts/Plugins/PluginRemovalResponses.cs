using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Plugins;

/// <summary>What removing a plugin would do, in plain words, before anything is removed (Plugin architecture spec C23).</summary>
public sealed class PluginRemovalPreviewResponse
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>One plain sentence per thing that goes away or stays, ready to show as a list.</summary>
    [JsonPropertyName("lines")]
    public List<string> Lines { get; set; } = [];

    /// <summary>Records the plugin keeps for itself.</summary>
    [JsonPropertyName("records")]
    public int Records { get; set; }

    [JsonPropertyName("record_bytes")]
    public long RecordBytes { get; set; }

    /// <summary>How many of its keys and logins are set.</summary>
    [JsonPropertyName("secrets_set")]
    public int SecretsSet { get; set; }

    /// <summary>The noun for the output it saved ("skip markers"), or null when it saves none.</summary>
    [JsonPropertyName("saved_output")]
    public string? SavedOutput { get; set; }

    /// <summary>How many of those can be removed with it (unreviewed ones; anything a person confirmed or edited stays).</summary>
    [JsonPropertyName("saved_output_count")]
    public int SavedOutputCount { get; set; }

    /// <summary><c>keep</c> or <c>remove</c>: what the choice starts on.</summary>
    [JsonPropertyName("default_output")]
    public string DefaultOutput { get; set; } = "keep";

    /// <summary>Plugins that need this one; they are switched off with it.</summary>
    [JsonPropertyName("needed_by")]
    public List<string> NeededBy { get; set; } = [];

    /// <summary>What would have no source once it is gone.</summary>
    [JsonPropertyName("would_lose")]
    public List<string> WouldLose { get; set; } = [];
}

/// <summary>The choices on removal. Both are optional; the defaults are to keep saved output and to refuse while other plugins need this one.</summary>
public sealed class PluginRemoveRequest
{
    /// <summary><c>keep</c> or <c>remove</c>; null takes the plugin's default.</summary>
    [JsonPropertyName("output")]
    public string? Output { get; set; }

    [JsonPropertyName("also_switch_off_dependants")]
    public bool AlsoSwitchOffDependants { get; set; }
}
