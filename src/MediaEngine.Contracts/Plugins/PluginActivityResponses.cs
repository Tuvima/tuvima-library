using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Plugins;

/// <summary>One line of a plugin's or source's Activity list (Plugin architecture spec D10 and C17).</summary>
public sealed class PluginActivityEntryDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("occurred_at")]
    public DateTimeOffset OccurredAt { get; set; }

    [JsonPropertyName("plugin_version")]
    public string PluginVersion { get; set; } = "";

    /// <summary><c>run</c>, <c>failure</c>, <c>skipped</c> or <c>note</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "run";

    /// <summary>The cause from the fixed list (<c>service_unreachable</c>, <c>tool_missing</c>, ...), or null.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>The cause group: <c>plugin</c>, <c>outside_service</c>, <c>missing_tool</c>, <c>local_ai</c>, <c>item</c>, <c>limit</c>, <c>not_allowed</c>, <c>dependency_off</c> or <c>library</c>.</summary>
    [JsonPropertyName("reason_group")]
    public string? ReasonGroup { get; set; }

    /// <summary>The plain sentence for the cause.</summary>
    [JsonPropertyName("reason_words")]
    public string? ReasonWords { get; set; }

    [JsonPropertyName("item_id")]
    public Guid? ItemId { get; set; }

    [JsonPropertyName("items_changed")]
    public int ItemsChanged { get; set; }

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    /// <summary>A technical line, already redacted of keys and personal paths.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }
}

/// <summary>A plugin's or source's Activity list, newest first, and whether it is paused after repeated failures.</summary>
public sealed class PluginActivityResponse
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    [JsonPropertyName("entries")]
    public List<PluginActivityEntryDto> Entries { get; set; } = [];

    /// <summary>When the Library will try it again after pausing it for repeated failures, or null when it is not paused.</summary>
    [JsonPropertyName("paused_until")]
    public DateTimeOffset? PausedUntil { get; set; }
}

/// <summary>One record of what a person did with a plugin or source that changed shared Library data (Plugin architecture spec C25).</summary>
public sealed class PluginPeopleActionDto
{
    [JsonPropertyName("occurred_at")]
    public DateTimeOffset OccurredAt { get; set; }

    [JsonPropertyName("profile_id")]
    public Guid? ProfileId { get; set; }

    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    /// <summary>What was done, as a stable word ("chose-lyrics", "marked-subtitles-wrong", "linked-account", "switched-plugin-off").</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("item_id")]
    public Guid? ItemId { get; set; }

    [JsonPropertyName("before")]
    public string? Before { get; set; }

    [JsonPropertyName("after")]
    public string? After { get; set; }

    [JsonPropertyName("device")]
    public string? Device { get; set; }
}

public sealed class PluginPeopleActionsResponse
{
    [JsonPropertyName("actions")]
    public List<PluginPeopleActionDto> Actions { get; set; } = [];
}
