using System.Text.Json.Serialization;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// The one declaration block every built-in plugin (<c>PluginManifest.Declaration</c>) and metadata source
/// (<c>ProviderConfiguration.Declaration</c>) can carry (Plugin architecture spec, PA1 and D5). It says who may use the item,
/// what it needs, how heavy it is, what it keeps and how it behaves when it fails. Everything is optional: an item that states
/// nothing is for administrators only, light, quiet, and keeps its output on removal. Category and media types stay where each
/// format already has them; <see cref="PluginDeclarationRules.Resolve"/> brings the two together.
/// </summary>
public sealed class PluginDeclaration
{
    /// <summary><c>administrators</c> (default), <c>adults</c> or <c>everyone</c>: who may <em>use</em> what the item provides. Setting it up stays administrator-only.</summary>
    [JsonPropertyName("audience")]
    public string? Audience { get; set; }

    /// <summary>Optional, stricter than the Library's own rule: <c>never</c> keeps the item away from Vault items altogether. Whether it may work on them otherwise is worked out from where it sends data.</summary>
    [JsonPropertyName("vault")]
    public string? Vault { get; set; }

    /// <summary>What the item needs before it can work (other plugins, Local AI abilities, tools, sources, host services).</summary>
    [JsonPropertyName("requires")]
    public List<PluginRequirement> Requires { get; set; } = [];

    /// <summary><c>light</c> (default), <c>medium</c> or <c>heavy</c>. Heavy items run when the machine is idle, after ingestion, one at a time.</summary>
    [JsonPropertyName("weight_class")]
    public string? WeightClass { get; set; }

    /// <summary>What the item keeps in the Library, and what removal does with it.</summary>
    [JsonPropertyName("stores")]
    public PluginStores? Stores { get; set; }

    /// <summary><c>skip_and_retry</c> (default) or <c>skip</c>: what happens to an item when this one is down, slow or over its limit. Never blocks ingestion, playback or browsing.</summary>
    [JsonPropertyName("on_failure")]
    public string? OnFailure { get; set; }

    /// <summary><c>quiet</c> (default for background helpers) or <c>tell_me</c>: whether a failure raises the plugin-broken notification.</summary>
    [JsonPropertyName("notify")]
    public string? Notify { get; set; }

    /// <summary>One line on what changed in this version (1 to 200 characters).</summary>
    [JsonPropertyName("changelog")]
    public string? Changelog { get; set; }

    /// <summary>Language codes the item supports (<c>en</c>, <c>fr</c>, ...); empty means it is not language-specific.</summary>
    [JsonPropertyName("languages")]
    public List<string> Languages { get; set; } = [];

    /// <summary>The Library surfaces the item contributes data to, from <see cref="PluginDeclarationRules.DisplaySlots"/>.</summary>
    [JsonPropertyName("display_slots")]
    public List<string> DisplaySlots { get; set; } = [];

    /// <summary>The actions the item offers to people (look up lyrics, find subtitles, re-scan skip markers), bound to what it supplies, never to its name.</summary>
    [JsonPropertyName("actions")]
    public List<PluginActionDeclaration> Actions { get; set; } = [];

    /// <summary>When the item supplies a text track: <c>lyrics</c> or <c>subtitles</c>. The Library saves it and does any writeback; the item never writes to a person's folders.</summary>
    [JsonPropertyName("text_track")]
    public string? TextTrack { get; set; }

    /// <summary>Secrets the item reads, split into household and personal ones (name to scope).</summary>
    [JsonPropertyName("credential_scopes")]
    public Dictionary<string, string> CredentialScopes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One thing an item needs, and whether it is required or only improves results.</summary>
public sealed class PluginRequirement
{
    /// <summary><c>plugin</c>, <c>local_ai</c>, <c>tool</c>, <c>source</c> or <c>host_service</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>The plugin id, AI ability, tool id, source name or host service name.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>For a plugin: the versions that work, as <c>1.0.0</c> (this or newer) or <c>1.0.0-2.0.0</c> (inclusive range). Absent means any.</summary>
    [JsonPropertyName("version_range")]
    public string? VersionRange { get; set; }

    /// <summary>True when the item only works better with this; false (default) when it cannot work without it.</summary>
    [JsonPropertyName("optional")]
    public bool Optional { get; set; }
}

/// <summary>What an item stores and what removal does with its output (D12, D16).</summary>
public sealed class PluginStores
{
    /// <summary>True when the item keeps its own records in the Library's data store.</summary>
    [JsonPropertyName("records")]
    public bool Records { get; set; }

    /// <summary>A plain noun for the output it saves in the Library ("skip markers", "lore"), or null when it saves none.</summary>
    [JsonPropertyName("saved_output")]
    public string? SavedOutput { get; set; }

    /// <summary><c>keep</c> (default, Shy D16) or <c>remove</c>: what removal offers by default for the saved output.</summary>
    [JsonPropertyName("on_removal")]
    public string? OnRemoval { get; set; }
}

/// <summary>One action an item adds for people (C4).</summary>
public sealed class PluginActionDeclaration
{
    /// <summary>A stable id, lower-case words and hyphens, unique within the item (for example <c>find-lyrics</c>).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>What it does, from <see cref="PluginDeclarationRules.ActionKinds"/>. The button appears only when a switched-on item supplies this kind.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>The words on the button or menu row (1 to 40 characters).</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>Where it shows: <c>item_menu</c> (default), <c>player_overflow</c>, <c>player_core</c> (only for a Library-defined kind) or <c>panel</c>.</summary>
    [JsonPropertyName("slot")]
    public string? Slot { get; set; }

    /// <summary>Who may press it; absent means the item's own audience.</summary>
    [JsonPropertyName("audience")]
    public string? Audience { get; set; }

    /// <summary>Order inside the overflow menu; lower comes first (0 to 100, default 50).</summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 50;

    /// <summary>True when the action changes metadata or files (desktop only). Defaults to true except for lookups of text tracks.</summary>
    [JsonPropertyName("edits")]
    public bool? Edits { get; set; }
}
