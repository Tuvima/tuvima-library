using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Plugins;

/// <summary>
/// One metadata source (a provider) as the Plugins page shows it (Developer tools spec WP20): who it is, whether it is on,
/// what it can supply for which kinds of media, and where it sends data.
/// </summary>
public sealed class PluginSourceDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary><c>official</c> for a source that ships with the Library, <c>plugin</c> for one a plugin supplies.</summary>
    [JsonPropertyName("origin")]
    public string Origin { get; set; } = "official";

    /// <summary>The plugin that supplies this source, or null for a source that ships with the Library.</summary>
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; set; }

    [JsonPropertyName("requires_key")]
    public bool RequiresKey { get; set; }

    /// <summary>Plain labels for the kinds of media it works with ("Movies", "TV").</summary>
    [JsonPropertyName("media_types")]
    public List<string> MediaTypes { get; set; } = [];

    /// <summary>The capability sheet: one plain phrase for each thing it can supply ("Movie artwork", "Book matching").</summary>
    [JsonPropertyName("supplies")]
    public List<string> Supplies { get; set; } = [];

    /// <summary>The web addresses (hosts) it contacts.</summary>
    [JsonPropertyName("sends_data_to")]
    public List<string> SendsDataTo { get; set; } = [];
}

/// <summary>What would have no source if a metadata source were switched off or removed, and what would still be covered.</summary>
public sealed class PluginSourceImpactDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";

    /// <summary>Things only this source supplies among the sources that are on ("TV artwork").</summary>
    [JsonPropertyName("would_lose")]
    public List<string> WouldLose { get; set; } = [];

    /// <summary>Things another source on would still supply, with that source named.</summary>
    [JsonPropertyName("still_covered")]
    public List<PluginSourceCoverageDto> StillCovered { get; set; } = [];

    /// <summary>Always says that what is already in the Library is kept and nothing is matched again unless the person asks.</summary>
    [JsonPropertyName("keeps")]
    public string Keeps { get; set; } = "";
}

public sealed class PluginSourceCoverageDto
{
    [JsonPropertyName("supplies")]
    public string Supplies { get; set; } = "";

    [JsonPropertyName("by")]
    public List<string> By { get; set; } = [];
}

/// <summary>A side-by-side of two sources: what both supply, and what only one of them does.</summary>
public sealed class PluginSourceComparisonDto
{
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    [JsonPropertyName("both")]
    public List<string> Both { get; set; } = [];

    [JsonPropertyName("only_from")]
    public List<string> OnlyFrom { get; set; } = [];

    [JsonPropertyName("only_to")]
    public List<string> OnlyTo { get; set; } = [];

    [JsonPropertyName("keeps")]
    public string Keeps { get; set; } = "";
}

/// <summary>The Plugins page's list of metadata sources, and whether the Engine must restart before the list is what is running.</summary>
public sealed class PluginSourcesResponse
{
    [JsonPropertyName("sources")]
    public List<PluginSourceDto> Sources { get; set; } = [];

    /// <summary>True when a plugin's source was added or removed since the Engine started; sources are read at startup.</summary>
    [JsonPropertyName("restart_needed")]
    public bool RestartNeeded { get; set; }
}
