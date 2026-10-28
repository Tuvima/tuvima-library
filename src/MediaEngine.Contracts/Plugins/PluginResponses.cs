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

    /// <summary>Plugins that were switched on or off along with this one, in the order they changed (needed plugins first on enable, dependants first on disable). Empty when only this one changed.</summary>
    public List<string> also_changed { get; init; } = [];
}

/// <summary>
/// Response for <c>PUT /plugins/{pluginId}/settings</c> and <c>PUT /plugins/{pluginId}/manifest</c>.
/// </summary>
public sealed record PluginSavedResponse
{
    public string plugin_id { get; init; } = string.Empty;
    public bool saved { get; init; }
}

/// <summary>Body of <c>POST /plugins/{pluginId}/ai-steps/{stepKey}/run</c>: the values for the step's <c>{name}</c> placeholders.</summary>
public sealed record PluginAiStepRunRequest
{
    public Dictionary<string, string> inputs { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Response for <c>POST /plugins/{pluginId}/ai-steps/{stepKey}/run</c>. <c>status</c> is one of succeeded, refused,
/// ai_unavailable, busy, over_allowance, timed_out, invalid_answer or failed; <c>text</c> is set only when it succeeded.
/// </summary>
public sealed record PluginAiStepRunResponse
{
    public string plugin_id { get; init; } = string.Empty;
    public string step { get; init; } = string.Empty;
    public string status { get; init; } = string.Empty;
    public string message { get; init; } = string.Empty;
    public string? text { get; init; }
}

/// <summary>
/// Response for <c>POST /plugins/{pluginId}/service/lookup</c>. <c>status</c> is succeeded, not_running or failed; <c>result</c>
/// is the program's JSON answer when it succeeded.
/// </summary>
public sealed record PluginServiceLookupResponse
{
    public string plugin_id { get; init; } = string.Empty;
    public string status { get; init; } = string.Empty;
    public string message { get; init; } = string.Empty;
    public global::System.Text.Json.JsonElement? result { get; init; }
}

/// <summary>Response for <c>GET /plugin-host/v1/whoami</c>: the plugin the key belongs to and what it was approved to do.</summary>
public sealed record PluginHostWhoAmIResponse
{
    public string plugin_id { get; init; } = string.Empty;
    public string name { get; init; } = string.Empty;
    public IReadOnlyList<string> permissions { get; init; } = [];
}

/// <summary>
/// Response for <c>POST /plugin-host/v1/ai/text</c>. <c>status</c> is one of succeeded, refused, ai_unavailable, busy,
/// over_allowance, timed_out, invalid_answer or failed; <c>text</c> is set only when it succeeded.
/// </summary>
public sealed record PluginHostAiTextResponse
{
    public string status { get; init; } = string.Empty;
    public string message { get; init; } = string.Empty;
    public string? text { get; init; }
}

/// <summary>Response for <c>GET /plugin-host/v1/records/{collection}/{key}</c>: one of the plugin's own records.</summary>
public sealed record PluginHostRecordResponse
{
    public string collection { get; init; } = string.Empty;
    public string key { get; init; } = string.Empty;
    public global::System.Text.Json.JsonElement value { get; init; }
    public DateTimeOffset updated_at { get; init; }
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

    /// <summary>The tab the plugin is listed under: <c>metadata</c>, <c>media-processing</c>, <c>services</c> or <c>utilities</c>.</summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = "utilities";

    /// <summary>The media types the plugin applies to (<c>books</c>, <c>comics</c>, <c>audiobooks</c>, <c>movies</c>, <c>tv</c>, <c>music</c>, <c>photos</c>); empty means every type.</summary>
    [JsonPropertyName("media_types")]
    public List<string> MediaTypes { get; set; } = [];

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

    /// <summary>Each place the plugin says it contacts, and what it sends there. Empty when the plugin does not use the internet.</summary>
    [JsonPropertyName("network_destinations")]
    public List<PluginNetworkDestinationDto> NetworkDestinations { get; set; } = [];

    /// <summary>Who may use it, where it works, how it treats Vault items, what leaves the machine, what it needs and stores (Plugin architecture spec).</summary>
    [JsonPropertyName("access")]
    public PluginAccessDto Access { get; set; } = new();

    [JsonPropertyName("settings")]
    public Dictionary<string, JsonElement> Settings { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("settings_schema")]
    public JsonElement? SettingsSchema { get; set; }

    [JsonPropertyName("manifest_path")]
    public string? ManifestPath { get; set; }

    /// <summary>How the plugin is getting on with the Library's host services (its own records, recurring jobs, events, background work and web connection). Always present; kept in memory, so it starts clean after an Engine restart.</summary>
    [JsonPropertyName("host_services")]
    public PluginHostServicesDto HostServices { get; set; } = new();
}

/// <summary>The state of a plugin's host services. <see cref="State"/> is <c>working</c>, <c>needs_attention</c> or <c>broken</c>.</summary>
public sealed record PluginHostServicesDto
{
    /// <summary><c>working</c>, <c>needs_attention</c> (a limit was reached; see <see cref="Problem"/>) or <c>broken</c> (its always-on work stopped for good).</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "working";

    /// <summary>The plain-English reason behind <see cref="State"/>, or null while it is <c>working</c>. Examples: "Reached its storage limit (16 MB)", "Stopped after crashing 5 times in an hour".</summary>
    [JsonPropertyName("problem")]
    public string? Problem { get; set; }

    /// <summary>The most recent thing that went wrong that does not change <see cref="State"/> (an event handler failed, a web address was refused), or null.</summary>
    [JsonPropertyName("last_problem")]
    public string? LastProblem { get; set; }

    /// <summary>How many records the plugin keeps in the Library's data store.</summary>
    [JsonPropertyName("records_used")]
    public int RecordsUsed { get; set; }

    /// <summary>How much space those records take, in bytes.</summary>
    [JsonPropertyName("record_bytes_used")]
    public long RecordBytesUsed { get; set; }

    /// <summary>The plugin's recurring jobs. Empty when it has none or is switched off.</summary>
    [JsonPropertyName("jobs")]
    public List<PluginHostJobDto> Jobs { get; set; } = [];

    /// <summary><c>active</c> or <c>paused</c> when the plugin subscribes to Library events; null when it does not.</summary>
    [JsonPropertyName("event_subscription")]
    public string? EventSubscription { get; set; }

    /// <summary><c>running</c>, <c>restarting</c>, <c>stopping</c> (asked to stop and not finished yet) or <c>stopped</c> when the plugin has always-on work; null when it does not.</summary>
    [JsonPropertyName("background_service")]
    public string? BackgroundService { get; set; }
}

/// <summary>One recurring job of a plugin.</summary>
public sealed record PluginHostJobDto
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary><c>scheduled</c>, <c>running</c> or <c>paused</c> (stopped after running too long three times in a row).</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "scheduled";
}

/// <summary>One place a plugin says it contacts: a host (exact, or one leading <c>*.</c> wildcard) and a plain sentence on what it sends there.</summary>
public sealed record PluginNetworkDestinationDto
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("sends")]
    public string Sends { get; set; } = "";
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

/// <summary>
/// The plugin directory as the Engine shows it (<c>GET /plugins/directory</c>): the saved copy of the list Tuvima keeps
/// on GitHub, when it was last checked, and whether the last check worked. The Engine reads the list; it never installs
/// from it yet.
/// </summary>
public sealed class PluginDirectoryResponse
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "2.0";

    [JsonPropertyName("source_url")]
    public string SourceUrl { get; set; } = "";

    /// <summary>When the list itself says it was last changed.</summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary><c>fresh</c> (the last check worked), <c>saved_copy</c> (the last check failed; showing the copy kept earlier), <c>unavailable</c> (no copy and the check failed), <c>disabled</c> (the switch is off) or <c>invalid_source</c>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "fresh";

    /// <summary>A plain sentence for the person when something needs saying; null otherwise.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>Whether the directory switch is on. When off, the Engine makes no request and shows the saved copy, if any.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>When the Engine last tried to reach the list (worked or not); null before the first try.</summary>
    [JsonPropertyName("last_checked_at")]
    public DateTimeOffset? LastCheckedAt { get; set; }

    /// <summary>When the Engine last got the list successfully; the saved copy is from this time.</summary>
    [JsonPropertyName("last_success_at")]
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>How many listed plugins the person has not yet been shown (see <c>POST /plugins/directory/seen</c>).</summary>
    [JsonPropertyName("new_count")]
    public int NewCount { get; set; }

    /// <summary>How many entries in the list could not be read and were left out.</summary>
    [JsonPropertyName("skipped_count")]
    public int SkippedCount { get; set; }

    /// <summary>True only when this Library can install a listed plugin. The Dashboard shows an Install button only when it is true.</summary>
    [JsonPropertyName("install_supported")]
    public bool InstallSupported { get; set; }

    [JsonPropertyName("plugins")]
    public List<PluginDirectoryEntryDto> Plugins { get; set; } = [];
}

public sealed class PluginDirectoryEntryDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    /// <summary><c>official</c>, <c>declarative</c>, <c>service</c> or <c>sandboxed</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "declarative";

    /// <summary><c>official</c>, <c>verified</c> or <c>community</c> (not verified). Anything missing or unknown is <c>community</c>.</summary>
    [JsonPropertyName("trust_level")]
    public string TrustLevel { get; set; } = "community";

    /// <summary>The plain warning to show beside a plugin Tuvima has not checked; null for official and verified plugins.</summary>
    [JsonPropertyName("trust_warning")]
    public string? TrustWarning { get; set; }

    /// <summary>The permission ids the plugin declares.</summary>
    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = [];

    /// <summary>What the plugin may do, in the Engine's plain words (one line per declared permission).</summary>
    [JsonPropertyName("allowed_to")]
    public List<string> AllowedTo { get; set; } = [];

    /// <summary>Where the plugin says it will send data and what it sends there (list format 2.x <c>network</c>); empty when the list does not say.</summary>
    [JsonPropertyName("network")]
    public List<PluginNetworkDestinationDto> Network { get; set; } = [];

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = [];

    [JsonPropertyName("minimum_library_version")]
    public string MinimumLibraryVersion { get; set; } = "1.0.0";

    [JsonPropertyName("maximum_library_version")]
    public string? MaximumLibraryVersion { get; set; }

    [JsonPropertyName("supported_platforms")]
    public List<string> SupportedPlatforms { get; set; } = [];

    [JsonPropertyName("repository_url")]
    public string? RepositoryUrl { get; set; }

    [JsonPropertyName("release_url")]
    public string? ReleaseUrl { get; set; }

    [JsonPropertyName("package_url")]
    public string? PackageUrl { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    /// <summary>The signature the list gives for the package. Used by the install check only; never sent to the Dashboard.</summary>
    [JsonIgnore]
    public string? Signature { get; set; }

    [JsonPropertyName("added_at")]
    public DateTimeOffset? AddedAt { get; set; }

    /// <summary>True when the plugin was not in the list the last time the person looked.</summary>
    [JsonPropertyName("is_new")]
    public bool IsNew { get; set; }

    /// <summary>True when a plugin with this id is already installed on this server.</summary>
    [JsonPropertyName("is_installed")]
    public bool IsInstalled { get; set; }

    /// <summary>The version installed on this server, or null when it is not installed.</summary>
    [JsonPropertyName("installed_version")]
    public string? InstalledVersion { get; set; }

    /// <summary>True when the installed copy came from this list and the list now has a newer version.</summary>
    [JsonPropertyName("update_available")]
    public bool UpdateAvailable { get; set; }
}

/// <summary>Body of <c>PUT /plugins/directory/settings</c>: the plugin directory switch.</summary>
public sealed class PluginDirectorySettingsRequest
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

/// <summary>Result of <c>POST /plugins/directory/{pluginId}/install</c>.</summary>
public sealed class PluginInstallResponse
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    /// <summary>True when an installed copy was replaced by a newer version.</summary>
    [JsonPropertyName("was_update")]
    public bool WasUpdate { get; set; }

    /// <summary>True when the new version asks for more than the person approved, so the plugin is off until they approve again.</summary>
    [JsonPropertyName("needs_approval_again")]
    public bool NeedsApprovalAgain { get; set; }

    /// <summary>One plain sentence about what happened.</summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary>Result of <c>GET /plugins/{pluginId}/removal-impact</c>: what stops working if the plugin is removed.</summary>
public sealed class PluginRemovalImpactResponse
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; set; } = "";

    /// <summary>One plain sentence per thing that would have no source once the plugin is gone. Empty when nothing depends on it.</summary>
    [JsonPropertyName("would_lose")]
    public List<string> WouldLose { get; set; } = [];

    /// <summary>Always says that what the plugin already saved stays.</summary>
    [JsonPropertyName("keeps")]
    public string Keeps { get; set; } = "";
}
