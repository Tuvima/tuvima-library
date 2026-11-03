using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Plugins;

/// <summary>One value of a control that is kept outside the plugin's saved settings (<c>PUT /plugins/{id}/page/values/{key}</c>).</summary>
public sealed class PluginSetupValueRequest
{
    [JsonPropertyName("value")]
    public JsonElement Value { get; set; }
}

/// <summary>A row to add to a group list (<c>POST /plugins/{id}/page/items/{key}</c>): the fields by field key, secrets included.</summary>
public sealed class PluginSetupItemRequest
{
    [JsonPropertyName("values")]
    public Dictionary<string, JsonElement> Values { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>A secret field of one group-list row (<c>PUT /plugins/{id}/page/items/{key}/{itemId}/secret/{field}</c>).</summary>
public sealed class PluginSetupSecretRequest
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

/// <summary>The outcome of changing a group list or an outside value. The message is plain words and never holds a secret.</summary>
public sealed class PluginSetupChangeResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    /// <summary>The id of the row that was added, when one was.</summary>
    [JsonPropertyName("item_id")]
    public string? ItemId { get; set; }
}

/// <summary>How much of a plugin's Activity list is kept (<c>PUT /plugins/{id}/activity-cap</c>): the newest entries, none older than the days.</summary>
public sealed class PluginActivityCapDto
{
    [JsonPropertyName("max_entries")]
    public int MaxEntries { get; set; } = 200;

    [JsonPropertyName("max_days")]
    public int MaxDays { get; set; } = 14;
}
