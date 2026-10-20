using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Plugins;

/// <summary>
/// Response for <c>POST /plugins/{pluginId}/enable</c> and <c>/disable</c>. Property names
/// (including snake_case spelling) are byte-identical to the anonymous object this record
/// replaced (Stage 5A wave 2 response-shape promotion) so the wire shape does not change.
/// </summary>
public sealed record PluginEnabledResponse
{
    public string plugin_id { get; init; } = string.Empty;
    public bool enabled { get; init; }
}

/// <summary>
/// Response for <c>PUT /plugins/{pluginId}/settings</c> and <c>PUT /plugins/{pluginId}/manifest</c>.
/// </summary>
public sealed record PluginSavedResponse
{
    public string plugin_id { get; init; } = string.Empty;
    public bool saved { get; init; }
}

/// <summary>
/// Response for <c>GET /plugins/{pluginId}/manifest</c>.
/// </summary>
public sealed record PluginManifestJsonResponse
{
    public string plugin_id { get; init; } = string.Empty;
    public string json { get; init; } = string.Empty;
}

/// <summary>
/// Response for <c>DELETE /plugins/{pluginId}</c>.
/// </summary>
public sealed record PluginDeletedResponse
{
    public string plugin_id { get; init; } = string.Empty;
    public bool deleted { get; init; }
}

/// <summary>
/// Public plugin summary returned by plugin catalogue routes. The lower_snake_case member names
/// preserve the anonymous response object's existing JSON contract.
/// </summary>
public sealed record PluginSummaryResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("is_built_in")]
    public bool IsBuiltIn { get; init; }

    /// <summary>
    /// True when the plugin was switched on but is not running because what it may do changed since it was approved (a
    /// manifest gained a permission). <see cref="Enabled"/> is false; switching it on asks for approval again.
    /// </summary>
    [JsonPropertyName("needs_approval")]
    public bool NeedsApproval { get; set; }

    [JsonPropertyName("load_error")]
    public string? LoadError { get; set; }

    /// <summary>How the plugin runs: <c>official</c>, <c>declarative</c>, <c>service</c> or <c>sandboxed</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "official";

    /// <summary>How far the Library trusts the plugin, decided by the Library: <c>official</c>, <c>verified</c>, <c>community</c> or <c>private</c>.</summary>
    [JsonPropertyName("trust_level")]
    public string TrustLevel { get; set; } = "private";

    [JsonPropertyName("minimum_library_version")]
    public string MinimumLibraryVersion { get; set; } = "1.0.0";

    [JsonPropertyName("maximum_library_version")]
    public string? MaximumLibraryVersion { get; set; }

    /// <summary>Operating systems or runtime identifiers the plugin supports; empty means every platform.</summary>
    [JsonPropertyName("supported_platforms")]
    public List<string> SupportedPlatforms { get; set; } = [];

    /// <summary>False when the plugin cannot be switched on here (Library version, platform, kind or a load problem).</summary>
    [JsonPropertyName("is_compatible")]
    public bool IsCompatible { get; set; } = true;

    /// <summary>Plain-English reason the plugin cannot be switched on, or null when it can.</summary>
    [JsonPropertyName("compatibility_problem")]
    public string? CompatibilityProblem { get; set; }

    [JsonPropertyName("capabilities")]
    public List<PluginCapabilityDto> Capabilities { get; set; } = [];

    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = [];

    [JsonPropertyName("tool_requirements")]
    public List<PluginToolRequirementDto> ToolRequirements { get; set; } = [];

    [JsonPropertyName("ai_permissions")]
    public List<PluginAiPermissionDto> AiPermissions { get; set; } = [];

    /// <summary>How many secrets (keys, logins) the plugin declares it needs.</summary>
    [JsonPropertyName("declared_secrets")]
    public int DeclaredSecrets { get; set; }

    /// <summary>How many of the declared secrets an administrator has entered. Never the values.</summary>
    [JsonPropertyName("configured_secrets")]
    public int ConfiguredSecrets { get; set; }

    /// <summary>What the plugin is allowed to do, in plain words written by the Engine (one line per permission, tool, AI use and secret).</summary>
    [JsonPropertyName("allowed_to")]
    public List<string> AllowedTo { get; set; } = [];

    /// <summary>Where the plugin can send data (for example "the internet"), or null when nothing leaves this machine.</summary>
    [JsonPropertyName("sends_data_to")]
    public string? SendsDataTo { get; set; }

    [JsonPropertyName("settings")]
    public Dictionary<string, JsonElement> Settings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("settings_schema")]
    public JsonElement? SettingsSchema { get; set; }

    [JsonPropertyName("manifest_path")]
    public string? ManifestPath { get; set; }
}

public sealed record PluginCapabilityDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}

public sealed record PluginToolRequirementDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("executable_name")]
    public string ExecutableName { get; set; } = "";

    [JsonPropertyName("license")]
    public string? License { get; set; }

    [JsonPropertyName("source_url")]
    public string? SourceUrl { get; set; }

    [JsonPropertyName("platforms")]
    public List<PluginToolPlatformDto> Platforms { get; set; } = [];
}

public sealed record PluginToolPlatformDto
{
    [JsonPropertyName("rid")]
    public string Rid { get; set; } = "";

    [JsonPropertyName("download_url")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("relative_executable_path")]
    public string? RelativeExecutablePath { get; set; }
}

public sealed record PluginAiPermissionDto
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("schedule")]
    public string Schedule { get; set; } = "";

    [JsonPropertyName("resource_class")]
    public string ResourceClass { get; set; } = "";
}

/// <summary>
/// One secret a plugin declares, for <c>GET /plugins/{pluginId}/secrets</c>. Says whether a value is set; never carries the value.
/// </summary>
public sealed record PluginSecretStatusResponse
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("configured")]
    public bool Configured { get; set; }
}

/// <summary>Body for <c>PUT /plugins/{pluginId}/secrets/{name}</c>.</summary>
public sealed record PluginSecretWriteRequest
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

public sealed record PluginHealthResponse
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("checks")]
    public List<PluginHealthCheckDto> Checks { get; set; } = [];
}

public sealed record PluginHealthCheckDto
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = [];
}

public sealed record PluginJobSnapshot
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("pluginId")]
    public string PluginId { get; init; } = "";

    [JsonPropertyName("jobType")]
    public string JobType { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "queued";

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; set; }

    [JsonPropertyName("assetsScanned")]
    public int AssetsScanned { get; set; }

    [JsonPropertyName("segmentsWritten")]
    public int SegmentsWritten { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed record PluginJsonUpdateRequest(
    [property: JsonPropertyName("json")] string Json);

public sealed class ApprovedPluginCatalogDto
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1.0";

    [JsonPropertyName("source_url")]
    public string SourceUrl { get; set; } = "";

    [JsonPropertyName("last_updated")]
    public DateTimeOffset? LastUpdated { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "ok";

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("plugins")]
    public List<ApprovedPluginDto> Plugins { get; set; } = [];
}

public sealed class ApprovedPluginDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "approved";

    [JsonPropertyName("repository_url")]
    public string? RepositoryUrl { get; set; }

    [JsonPropertyName("release_url")]
    public string? ReleaseUrl { get; set; }

    [JsonPropertyName("package_url")]
    public string? PackageUrl { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("minimum_library_version")]
    public string MinimumLibraryVersion { get; set; } = "1.0.0";

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];

    [JsonPropertyName("install_notes")]
    public string? InstallNotes { get; set; }
}
