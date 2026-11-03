using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Plugins;

/// <summary>
/// A plugin's page, ready to draw (<c>GET /plugins/{pluginId}/page</c>; Plugins and Integrations screens v5 D46). The Engine builds
/// it from the plugin's own JSON, whichever of the two file formats it is, so the Dashboard draws every plugin the same way and
/// never names one. Not to be confused with the plugin-supplied menu pages (<see cref="PluginPageResponse"/>); this is the
/// plugin's own Set up page.
/// </summary>
public sealed class PluginSetupPageResponse
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    [JsonPropertyName("plugin_name")]
    public string PluginName { get; set; } = "";

    /// <summary>How the plugin is defined: <c>source_file</c> (a provider file), <c>manifest</c> (a plugin manifest file) or <c>compiled</c> (a .NET plugin that describes itself).</summary>
    [JsonPropertyName("definition")]
    public string Definition { get; set; } = "manifest";

    /// <summary>The cards in the order they are drawn: access, provides, settings, privacy, about. A card the plugin has nothing for is absent.</summary>
    [JsonPropertyName("cards")]
    public List<PluginSetupCardDto> Cards { get; set; } = [];

    /// <summary>Words in the plugin's file that Library did not understand and ignored. Shown on the Plugin file card; never an error on the page.</summary>
    [JsonPropertyName("things_to_know")]
    public List<PluginSetupNoteDto> ThingsToKnow { get; set; } = [];
}

/// <summary>
/// One card of a plugin's page. Only the parts that belong to the card's <see cref="Key"/> are filled: a plugin supplies content,
/// never layout.
/// </summary>
public sealed class PluginSetupCardDto
{
    /// <summary><c>access</c>, <c>provides</c>, <c>settings</c>, <c>privacy</c> or <c>about</c>.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>True when the card holds the thing the administrator has to do next (a required key that is not set).</summary>
    [JsonPropertyName("needs_attention")]
    public bool NeedsAttention { get; set; }

    /// <summary>Access card: who may use it, where it works, how it treats Vault items, what leaves the machine.</summary>
    [JsonPropertyName("access")]
    public PluginAccessDto? Access { get; set; }

    /// <summary>Access card: the keys and logins the plugin asks for.</summary>
    [JsonPropertyName("credentials")]
    public List<PluginSetupCredentialDto> Credentials { get; set; } = [];

    /// <summary>Access and Privacy cards: sign-up, help, terms and privacy addresses the plugin gives.</summary>
    [JsonPropertyName("links")]
    public List<PluginSetupLinkDto> Links { get; set; } = [];

    /// <summary>Provides card: the kinds from the fixed list in <see cref="PluginProvides"/>.</summary>
    [JsonPropertyName("provides")]
    public List<string> Provides { get; set; } = [];

    /// <summary>Provides card: plain labels for the media types it covers; empty means every kind.</summary>
    [JsonPropertyName("media_types")]
    public List<string> MediaTypes { get; set; } = [];

    /// <summary>Provides card: one plain phrase for each thing a metadata source supplies ("Movie artwork").</summary>
    [JsonPropertyName("supplies")]
    public List<string> Supplies { get; set; } = [];

    /// <summary>Settings card: the full control list with current values.</summary>
    [JsonPropertyName("controls")]
    public List<PluginSetupControlDto> Controls { get; set; } = [];

    /// <summary>Access and Settings cards: the tests the plugin declares (test connection, check now, dry run).</summary>
    [JsonPropertyName("actions")]
    public List<PluginSetupActionDto> Actions { get; set; } = [];

    /// <summary>Privacy card: each place the plugin contacts and what it sends there.</summary>
    [JsonPropertyName("destinations")]
    public List<PluginSetupDestinationDto> Destinations { get; set; } = [];

    /// <summary>Privacy card: the one-line summary of what leaves the machine, worked out from the destinations and keys.</summary>
    [JsonPropertyName("data_leaving")]
    public string? DataLeaving { get; set; }

    /// <summary>Privacy card: the plain sentence on how Vault items are treated.</summary>
    [JsonPropertyName("vault_words")]
    public string? VaultWords { get; set; }

    /// <summary>About card: the longer description.</summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    /// <summary>About card: version, who it is from, how it runs, and the like.</summary>
    [JsonPropertyName("facts")]
    public List<PluginSetupFactDto> Facts { get; set; } = [];
}

/// <summary>One key or login, from either file format (D48). The value is never part of any response.</summary>
public sealed class PluginSetupCredentialDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("help")]
    public string Help { get; set; } = "";

    [JsonPropertyName("required")]
    public bool Required { get; set; } = true;

    [JsonPropertyName("minimum_length")]
    public int? MinimumLength { get; set; }

    [JsonPropertyName("maximum_length")]
    public int? MaximumLength { get; set; }

    /// <summary><c>household</c> (the administrator enters it once) or <c>profile</c> (each person links their own).</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "household";

    /// <summary>How a profile signs in without typing the credential (<c>device_code</c>), or null when it is entered by hand.</summary>
    [JsonPropertyName("sign_in_method")]
    public string? SignInMethod { get; set; }

    /// <summary>True when a household credential is set, false when it is not, null when it is each person's own.</summary>
    [JsonPropertyName("configured")]
    public bool? Configured { get; set; }
}

public sealed class PluginSetupLinkDto
{
    /// <summary><c>signup</c>, <c>help</c>, <c>terms</c> or <c>privacy</c>.</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

public sealed class PluginSetupDestinationDto
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    /// <summary>One plain sentence on what is sent there.</summary>
    [JsonPropertyName("sends")]
    public string Sends { get; set; } = "";

    [JsonPropertyName("max_requests_per_minute")]
    public int? MaxRequestsPerMinute { get; set; }
}

public sealed class PluginSetupFactDto
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

/// <summary>A test the plugin supports: <c>test_connection</c>, <c>check_now</c> or <c>dry_run</c>.</summary>
public sealed class PluginSetupActionDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}

/// <summary>A word the Engine did not understand in the plugin's file and ignored.</summary>
public sealed class PluginSetupNoteDto
{
    /// <summary>The setting the word was found on, or empty for the file as a whole.</summary>
    [JsonPropertyName("where")]
    public string Where { get; set; } = "";

    [JsonPropertyName("word")]
    public string Word { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public sealed class PluginSetupChoiceDto
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}

/// <summary>Show a control only while another setting has the given value.</summary>
public sealed class PluginSetupShowWhenDto
{
    [JsonPropertyName("setting")]
    public string Setting { get; set; } = "";

    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }
}

/// <summary>What a control needs (a tool, Local AI or a key); the control is greyed with <see cref="Reason"/> until it is there.</summary>
public sealed class PluginSetupRequirementDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}

/// <summary>One setting of the Settings card (D47). Every optional word has already been given its plain default.</summary>
public sealed class PluginSetupControlDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    /// <summary><c>toggle</c>, <c>choice</c>, <c>number</c>, <c>text</c>, <c>url</c>, <c>path</c>, <c>secret</c>, <c>choices</c> or <c>group_list</c>.</summary>
    [JsonPropertyName("control")]
    public string Control { get; set; } = "text";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("placeholder")]
    public string Placeholder { get; set; } = "";

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "";

    /// <summary>The heading inside Settings this control sits under, or empty for none.</summary>
    [JsonPropertyName("group")]
    public string Group { get; set; } = "";

    [JsonPropertyName("order")]
    public int Order { get; set; }

    /// <summary>True when the control goes in the collapsed Advanced section.</summary>
    [JsonPropertyName("advanced")]
    public bool Advanced { get; set; }

    [JsonPropertyName("read_only")]
    public bool ReadOnly { get; set; }

    /// <summary><c>household</c> or <c>profile</c> (each person sets their own).</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "household";

    [JsonPropertyName("minimum")]
    public double? Minimum { get; set; }

    [JsonPropertyName("maximum")]
    public double? Maximum { get; set; }

    /// <summary>True for a whole number.</summary>
    [JsonPropertyName("integer")]
    public bool Integer { get; set; }

    [JsonPropertyName("choices")]
    public List<PluginSetupChoiceDto> Choices { get; set; } = [];

    /// <summary>The current value, or the default when none is saved. Never filled for a secret.</summary>
    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }

    [JsonPropertyName("default_value")]
    public JsonElement? DefaultValue { get; set; }

    [JsonPropertyName("show_when")]
    public PluginSetupShowWhenDto? ShowWhen { get; set; }

    [JsonPropertyName("requires")]
    public PluginSetupRequirementDto? Requires { get; set; }

    /// <summary>For a secret: whether it is set (null when it is each person's own).</summary>
    [JsonPropertyName("configured")]
    public bool? Configured { get; set; }

    /// <summary>For a secret each person links by approving a code on another device: the method (<c>device_code</c>).</summary>
    [JsonPropertyName("sign_in_method")]
    public string? SignInMethod { get; set; }

    /// <summary>For a group list: what one item looks like, and the items.</summary>
    [JsonPropertyName("group_list")]
    public PluginSetupGroupListDto? GroupList { get; set; }
}

/// <summary>A repeating group (D49): what an item looks like, which actions each row has, and the current items.</summary>
public sealed class PluginSetupGroupListDto
{
    [JsonPropertyName("item_title")]
    public string ItemTitle { get; set; } = "";

    [JsonPropertyName("add_label")]
    public string AddLabel { get; set; } = "";

    /// <summary>From <c>test_connection</c>, <c>remove</c>, <c>check_now</c>, <c>open_link</c>.</summary>
    [JsonPropertyName("row_actions")]
    public List<string> RowActions { get; set; } = [];

    /// <summary>The fields of one item (the same shape as a control).</summary>
    [JsonPropertyName("fields")]
    public List<PluginSetupControlDto> Fields { get; set; } = [];

    [JsonPropertyName("items")]
    public List<PluginSetupGroupItemDto> Items { get; set; } = [];
}

public sealed class PluginSetupGroupItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    /// <summary>A short state word for the row ("connected", "no key yet").</summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    /// <summary>True when the row cannot be removed (declared locked in the plugin's file, or the item says so).</summary>
    [JsonPropertyName("locked")]
    public bool Locked { get; set; }

    /// <summary>The item's field values by field key.</summary>
    [JsonPropertyName("values")]
    public Dictionary<string, JsonElement> Values { get; set; } = new(StringComparer.Ordinal);
}
