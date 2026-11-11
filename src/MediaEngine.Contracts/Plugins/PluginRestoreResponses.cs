using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Plugins;

/// <summary>One Tuvima plugin file that is not as Tuvima ships it, in <c>GET /plugins/restore-preview</c> (Plugins and Integrations screens v5 D57).</summary>
public sealed class PluginRestoreItemDto
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    /// <summary>The file's name, for example <c>tmdb.json</c>.</summary>
    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = "";

    /// <summary><c>missing</c> (the file is gone), <c>damaged</c> (it cannot be read) or <c>edited</c> (it differs from Tuvima's version).</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "edited";

    /// <summary><c>restore</c> (a missing or damaged file is put back) or <c>reset</c> (an edited file goes back to Tuvima's version).</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "reset";
}

/// <summary><c>GET /plugins/restore-preview</c>: which official plugin files would change and how many already match. Writes nothing.</summary>
public sealed class PluginRestorePreviewResponse
{
    [JsonPropertyName("items")]
    public List<PluginRestoreItemDto> Items { get; set; } = [];

    /// <summary>How many Tuvima plugin files already match Tuvima's version.</summary>
    [JsonPropertyName("matching_count")]
    public int MatchingCount { get; set; }
}

/// <summary>What happened to one file in <c>POST /plugins/restore</c>.</summary>
public sealed class PluginRestoreResultItemDto
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = "";

    /// <summary><c>restored</c>, <c>reset</c> or <c>failed</c>.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "failed";

    /// <summary>One plain sentence; for a failure, why.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary><c>POST /plugins/restore</c>: puts every missing, damaged or edited Tuvima plugin file back. Keys, switches, settings values and Private plugins are untouched.</summary>
public sealed class PluginRestoreResponse
{
    [JsonPropertyName("items")]
    public List<PluginRestoreResultItemDto> Items { get; set; } = [];

    [JsonPropertyName("restored_count")]
    public int RestoredCount { get; set; }

    [JsonPropertyName("reset_count")]
    public int ResetCount { get; set; }

    [JsonPropertyName("failed_count")]
    public int FailedCount { get; set; }
}
