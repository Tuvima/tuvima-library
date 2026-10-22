using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Plugins;

/// <summary>One menu entry a plugin adds to the Library (<c>GET /plugin-pages/menu</c>).</summary>
public sealed class PluginMenuEntryDto
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    [JsonPropertyName("page_key")]
    public string PageKey { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>One of list, grid, guide, form or detail: the Library's own page types.</summary>
    [JsonPropertyName("page_type")]
    public string PageType { get; set; } = "list";

    /// <summary>False for a page type that needs a wide screen (the guide timeline); phones do not list it.</summary>
    [JsonPropertyName("phone_supported")]
    public bool PhoneSupported { get; set; }
}

public sealed class PluginMenuResponse
{
    [JsonPropertyName("entries")]
    public List<PluginMenuEntryDto> Entries { get; set; } = [];
}

/// <summary>A column of a list page, or a label of a detail page.</summary>
public sealed class PluginPageColumnDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}

/// <summary>One row, tile, programme or record of a plugin page. Only plain text and times: a plugin supplies data, never screens.</summary>
public sealed class PluginPageItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    /// <summary>An https picture from a host the plugin declared in its network list, or null.</summary>
    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    /// <summary>The row a guide item sits in (a channel); empty for other page types.</summary>
    [JsonPropertyName("group")]
    public string Group { get; set; } = "";

    [JsonPropertyName("starts_at")]
    public DateTimeOffset? StartsAt { get; set; }

    [JsonPropertyName("ends_at")]
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>Text for each declared column, by column key.</summary>
    [JsonPropertyName("fields")]
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>One input of a form page.</summary>
public sealed class PluginPageFormFieldDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>One of text, number or yes_no.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "text";

    [JsonPropertyName("required")]
    public bool Required { get; set; }
}

/// <summary>A plugin page ready to show (<c>GET /plugin-pages/{pluginId}/{pageKey}</c>).</summary>
public sealed class PluginPageResponse
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    [JsonPropertyName("plugin_name")]
    public string PluginName { get; set; } = "";

    [JsonPropertyName("page_key")]
    public string PageKey { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("page_type")]
    public string PageType { get; set; } = "list";

    [JsonPropertyName("phone_supported")]
    public bool PhoneSupported { get; set; }

    [JsonPropertyName("columns")]
    public List<PluginPageColumnDto> Columns { get; set; } = [];

    [JsonPropertyName("items")]
    public List<PluginPageItemDto> Items { get; set; } = [];

    [JsonPropertyName("form_fields")]
    public List<PluginPageFormFieldDto> FormFields { get; set; } = [];

    [JsonPropertyName("submit_label")]
    public string SubmitLabel { get; set; } = "";

    /// <summary>True when the page has more records than it shows.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }
}

/// <summary>Body of <c>POST /plugin-pages/{pluginId}/{pageKey}/submit</c>: the form values by field key.</summary>
public sealed class PluginPageSubmitRequest
{
    [JsonPropertyName("values")]
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);
}

public sealed class PluginPageSubmitResponse
{
    [JsonPropertyName("saved")]
    public bool Saved { get; set; }

    [JsonPropertyName("record_key")]
    public string RecordKey { get; set; } = "";
}
