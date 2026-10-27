using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Plugins;

/// <summary>
/// The Access section of a plugin or metadata source (Plugin architecture spec, section 4a): who may use it, where it works,
/// how it treats Vault items, what leaves the machine, and what it needs and stores. The Engine works out every word, so the
/// Dashboard, applications and the approval statement agree. Carried by both <c>PluginSummaryResponse.Access</c> and
/// <c>PluginSourceDto.Access</c>.
/// </summary>
public sealed class PluginAccessDto
{
    /// <summary><c>administrators</c>, <c>adults</c> or <c>everyone</c>.</summary>
    [JsonPropertyName("audience")]
    public string Audience { get; set; } = "administrators";

    /// <summary>True when the item states nothing and the default applies (the page says "Set by default").</summary>
    [JsonPropertyName("audience_is_default")]
    public bool AudienceIsDefault { get; set; } = true;

    /// <summary>Plain labels for the kinds of media it works on; empty means every kind.</summary>
    [JsonPropertyName("works_on")]
    public List<string> WorksOn { get; set; } = [];

    /// <summary>Library ids the administrator limited it to; empty means every library it works on.</summary>
    [JsonPropertyName("limited_to_library_ids")]
    public List<string> LimitedToLibraryIds { get; set; } = [];

    /// <summary><c>local_only</c>, <c>follows_section_lookup</c>, <c>own_source</c> or <c>never</c>.</summary>
    [JsonPropertyName("vault_use")]
    public string VaultUse { get; set; } = "local_only";

    /// <summary>The plain sentence for the Vault line.</summary>
    [JsonPropertyName("vault_words")]
    public string VaultWords { get; set; } = "";

    /// <summary>The data-leaving label (D8), worked out from the network list and keys: "Nothing leaves this machine", "Sends titles and years to X" or "Sends your key and searches to X".</summary>
    [JsonPropertyName("data_leaving")]
    public string DataLeaving { get; set; } = "";

    /// <summary><c>light</c>, <c>medium</c> or <c>heavy</c>.</summary>
    [JsonPropertyName("weight_class")]
    public string WeightClass { get; set; } = "light";

    /// <summary>True when a failure raises the plugin-broken notification (<c>tell_me</c>); false when quiet.</summary>
    [JsonPropertyName("tell_me_on_failure")]
    public bool TellMeOnFailure { get; set; }

    /// <summary>One line on what changed in this version, or null.</summary>
    [JsonPropertyName("changelog")]
    public string? Changelog { get; set; }

    [JsonPropertyName("languages")]
    public List<string> Languages { get; set; } = [];

    /// <summary>What the item needs, with whether each is required.</summary>
    [JsonPropertyName("requires")]
    public List<PluginRequirementDto> Requires { get; set; } = [];

    /// <summary>The noun for the output it saves in the Library ("skip markers"), or null.</summary>
    [JsonPropertyName("saved_output")]
    public string? SavedOutput { get; set; }

    /// <summary>True when the item keeps its own records in the Library's data store.</summary>
    [JsonPropertyName("keeps_records")]
    public bool KeepsRecords { get; set; }

    /// <summary><c>keep</c> or <c>remove</c>: what removal offers by default for the saved output.</summary>
    [JsonPropertyName("on_removal")]
    public string OnRemoval { get; set; } = "keep";

    [JsonPropertyName("display_slots")]
    public List<string> DisplaySlots { get; set; } = [];

    [JsonPropertyName("actions")]
    public List<PluginActionDto> Actions { get; set; } = [];

    /// <summary><c>lyrics</c>, <c>subtitles</c> or null.</summary>
    [JsonPropertyName("text_track")]
    public string? TextTrack { get; set; }
}

public sealed class PluginRequirementDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("version_range")]
    public string? VersionRange { get; set; }

    [JsonPropertyName("optional")]
    public bool Optional { get; set; }
}

/// <summary>One action a plugin or source offers to people, with who may press it.</summary>
public sealed class PluginActionDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("slot")]
    public string Slot { get; set; } = "item_menu";

    [JsonPropertyName("audience")]
    public string Audience { get; set; } = "administrators";

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 50;

    /// <summary>True when it changes metadata or files, which makes it desktop-only.</summary>
    [JsonPropertyName("edits")]
    public bool Edits { get; set; }
}
