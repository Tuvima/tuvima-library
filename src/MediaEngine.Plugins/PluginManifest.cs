using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaEngine.Plugins;

public sealed record PluginManifest
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0.0";

    /// <summary>What sort of plugin this is. Absent means <see cref="PluginKind.Official"/>, the in-process .NET contract.</summary>
    [JsonPropertyName("kind")]
    public PluginKind Kind { get; init; } = PluginKind.Official;

    /// <summary>Oldest Library version the plugin works with (major.minor.patch).</summary>
    [JsonPropertyName("minimum_library_version")]
    public string MinimumLibraryVersion { get; init; } = "1.0.0";

    /// <summary>Newest Library version the plugin works with (inclusive), or null for no upper limit.</summary>
    [JsonPropertyName("maximum_library_version")]
    public string? MaximumLibraryVersion { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("entry_assembly")]
    public string? EntryAssembly { get; init; }

    [JsonPropertyName("entry_type")]
    public string? EntryType { get; init; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<PluginCapabilityDescriptor> Capabilities { get; init; } = [];

    [JsonPropertyName("permissions")]
    public IReadOnlyList<string> Permissions { get; init; } = [];

    [JsonPropertyName("tool_requirements")]
    public IReadOnlyList<PluginToolRequirement> ToolRequirements { get; init; } = [];

    [JsonPropertyName("ai_permissions")]
    public IReadOnlyList<PluginAiPermission> AiPermissions { get; init; } = [];

    /// <summary>
    /// Runtime identifiers (<c>win-x64</c>, <c>linux-arm64</c>, ...) for an exact match, or an OS family
    /// (<c>windows</c>, <c>linux</c>, <c>macos</c>) matching any architecture. Empty means every platform.
    /// </summary>
    [JsonPropertyName("supported_platforms")]
    public IReadOnlyList<string> SupportedPlatforms { get; init; } = [];

    [JsonPropertyName("default_settings")]
    public Dictionary<string, JsonElement> DefaultSettings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("settings_schema")]
    public JsonElement? SettingsSchema { get; init; }
}

/// <summary>How a plugin runs. Only <see cref="Official"/> can run in this version of the Library.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PluginKind>))]
public enum PluginKind
{
    /// <summary>In-process .NET plugin (today's contract).</summary>
    [JsonStringEnumMemberName("official")]
    Official = 0,

    /// <summary>Described entirely in JSON.</summary>
    [JsonStringEnumMemberName("declarative")]
    Declarative,

    /// <summary>A separate program talking to the Library over a published contract.</summary>
    [JsonStringEnumMemberName("service")]
    Service,

    /// <summary>A WebAssembly module run in a sandbox.</summary>
    [JsonStringEnumMemberName("sandboxed")]
    Sandboxed,
}

/// <summary>
/// How far the Library trusts a plugin. Always computed by the Library, never declared in a manifest.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PluginTrustLevel>))]
public enum PluginTrustLevel
{
    /// <summary>Built into the Library.</summary>
    [JsonStringEnumMemberName("official")]
    Official = 0,

    /// <summary>Listed in the plugin directory and checked by Tuvima.</summary>
    [JsonStringEnumMemberName("verified")]
    Verified,

    /// <summary>Listed in the plugin directory, not checked.</summary>
    [JsonStringEnumMemberName("community")]
    Community,

    /// <summary>Added by hand to the plugins folder.</summary>
    [JsonStringEnumMemberName("private")]
    Private,
}

public sealed record PluginCapabilityDescriptor
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    /// <summary>Field keys this capability supplies (for example <c>series_position</c>).</summary>
    [JsonPropertyName("fields")]
    public IReadOnlyList<string> Fields { get; init; } = [];
}

public sealed record PluginToolRequirement
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("executable_name")]
    public string ExecutableName { get; init; } = "";

    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("source_url")]
    public string? SourceUrl { get; init; }

    [JsonPropertyName("platforms")]
    public IReadOnlyList<PluginToolPlatform> Platforms { get; init; } = [];
}

public sealed record PluginToolPlatform
{
    [JsonPropertyName("rid")]
    public string Rid { get; init; } = "";

    [JsonPropertyName("download_url")]
    public string? DownloadUrl { get; init; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    [JsonPropertyName("relative_executable_path")]
    public string? RelativeExecutablePath { get; init; }
}

public sealed record PluginAiPermission
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "";

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; }

    [JsonPropertyName("schedule")]
    public string Schedule { get; init; } = "scheduled";

    [JsonPropertyName("resource_class")]
    public string ResourceClass { get; init; } = "ai";
}
