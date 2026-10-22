using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Settings;

/// <summary>
/// One library kind ("recipe"): a type of library the Library can hold, from the built-in list or supplied by a plugin.
/// Every screen that offers a type (the Libraries front door, group pages and Add folder) is drawn from the list of these.
/// </summary>
public sealed class LibraryKindDto
{
    /// <summary>Stable identifier in lower_snake case (for example <c>movies</c>, <c>other_videos</c>); also the address of the kind's page.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>The area the kind sits under: <c>watch</c>, <c>listen</c>, <c>read</c>, <c>view</c> or <c>vault</c>.</summary>
    [JsonPropertyName("area")]
    public string Area { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>An <c>AppMaterialIcons</c> name the Dashboard draws.</summary>
    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "Folder";

    /// <summary>The Engine media type the kind's files are filed under (Movies, TV, Videos, Music, Audiobooks, Books, Comics); null for View kinds.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    /// <summary>The providers details come from. Empty means no lookup: files are shown by folder and file name only.</summary>
    [JsonPropertyName("lookup_providers")]
    public List<string> LookupProviders { get; set; } = [];

    /// <summary>The naming template (a key under <c>organization_templates</c> in <c>core.json</c>) that names the kind's files and folders; null for View kinds.</summary>
    [JsonPropertyName("naming_template")]
    public string? NamingTemplate { get; set; }

    /// <summary>What happens to a file that does not fit: <c>unmatched</c> or <c>keep</c>.</summary>
    [JsonPropertyName("misfit")]
    public string Misfit { get; set; } = "keep";

    /// <summary>The default for a new folder of this kind: <c>read_only</c> or <c>managed</c>.</summary>
    [JsonPropertyName("default_handling")]
    public string DefaultHandling { get; set; } = "read_only";

    /// <summary>The (i) text beside the kind in Add folder.</summary>
    [JsonPropertyName("tooltip")]
    public string Tooltip { get; set; } = string.Empty;

    /// <summary>Position among the kinds of its area.</summary>
    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("built_in")]
    public bool BuiltIn { get; set; }

    /// <summary>The id of the plugin that supplies the kind, or null for a built-in kind.</summary>
    [JsonPropertyName("plugin_id")]
    public string? PluginId { get; set; }

    /// <summary>False for a kind that is defined but not offered on any screen yet.</summary>
    [JsonPropertyName("available")]
    public bool Available { get; set; } = true;
}
