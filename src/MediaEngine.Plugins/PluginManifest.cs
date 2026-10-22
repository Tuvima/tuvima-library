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
    /// Keys and logins the plugin needs the administrator to enter. A plugin can only read names listed here,
    /// and only with the <c>secrets.plugin</c> permission.
    /// </summary>
    [JsonPropertyName("secrets")]
    public IReadOnlyList<PluginSecretDeclaration> Secrets { get; init; } = [];

    /// <summary>
    /// Where the plugin may send data on the internet, and what it sends there. A plugin that declares the
    /// <c>network.http</c> permission must list at least one destination, and only such a plugin may list any.
    /// The destinations are shown when the plugin is approved, so changing one asks for approval again.
    /// </summary>
    [JsonPropertyName("network")]
    public IReadOnlyList<PluginNetworkDestination> Network { get; init; } = [];

    /// <summary>
    /// Runtime identifiers (<c>win-x64</c>, <c>linux-arm64</c>, ...) for an exact match, or an OS family
    /// (<c>windows</c>, <c>linux</c>, <c>macos</c>) matching any architecture. Empty means every platform.
    /// </summary>
    [JsonPropertyName("supported_platforms")]
    public IReadOnlyList<string> SupportedPlatforms { get; init; } = [];

    [JsonPropertyName("default_settings")]
    public Dictionary<string, JsonElement> DefaultSettings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What a plugin of kind <see cref="PluginKind.Declarative"/> does, described in JSON: <c>ai_steps</c>,
    /// <c>channel_sources</c> and <c>metadata_sources</c>. Ignored by other kinds.
    /// </summary>
    [JsonPropertyName("declarative")]
    public JsonElement? Declarative { get; init; }

    /// <summary>
    /// How a plugin of kind <see cref="PluginKind.Service"/> (a separate program) is started or reached: <c>command</c> and
    /// <c>arguments</c>, or <c>address</c> for a program that is already running, plus <c>health_path</c>, <c>lookup_path</c>,
    /// <c>max_memory_mb</c> and <c>startup_seconds</c>. Ignored by other kinds.
    /// </summary>
    [JsonPropertyName("service")]
    public JsonElement? Service { get; init; }

    /// <summary>
    /// How a plugin of kind <see cref="PluginKind.Sandboxed"/> (WebAssembly) runs: <c>module</c> (a .wasm file inside the plugin's
    /// folder), <c>jobs</c>, and the limits <c>max_memory_mb</c>, <c>max_seconds</c> and <c>max_steps</c>. Ignored by other kinds.
    /// </summary>
    [JsonPropertyName("sandbox")]
    public JsonElement? Sandbox { get; init; }

    /// <summary>
    /// Pages and menu entries the plugin adds, chosen from the Library's own page types (list, grid, guide, form, detail).
    /// Needs the <c>plugin-pages</c> and <c>plugin-records</c> permissions. Read by <c>PluginPageDeclarations</c>.
    /// </summary>
    [JsonPropertyName("pages")]
    public JsonElement? Pages { get; init; }

    [JsonPropertyName("settings_schema")]
    public JsonElement? SettingsSchema { get; init; }
}

/// <summary>How a plugin runs. <see cref="Official"/> and <see cref="Declarative"/> run in this version of the Library.</summary>
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

/// <summary>One secret a plugin declares: the name it reads, and what to tell the administrator who enters it.</summary>
public sealed record PluginSecretDeclaration
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";
}

/// <summary>One place a plugin says it will contact, and a plain sentence on what it sends there.</summary>
public sealed record PluginNetworkDestination
{
    /// <summary>
    /// An exact host such as <c>api.example.com</c>, or one leading wildcard label such as <c>*.fandom.com</c>.
    /// No scheme, path, port or bare <c>*</c>.
    /// </summary>
    [JsonPropertyName("host")]
    public string Host { get; init; } = "";

    /// <summary>What is sent there, in one plain sentence (1 to 200 characters), for example "Searches for character and place names".</summary>
    [JsonPropertyName("sends")]
    public string Sends { get; init; } = "";

    /// <summary>The most requests per minute the plugin asks to make to this destination (1 to 600), or null to use the Library's default.</summary>
    [JsonPropertyName("max_requests_per_minute")]
    public int? MaxRequestsPerMinute { get; init; }
}

/// <summary>The one set of rules for the <c>network</c> list of a plugin manifest.</summary>
public static class PluginNetworkRules
{
    public const int MaxSendsLength = 200;
    public const int MinRequestsPerMinute = 1;
    public const int MaxRequestsPerMinute = 600;

    /// <summary>
    /// Checks a host: lower- or upper-case letters, digits and hyphens in dot-separated labels, optionally with one
    /// leading <c>*.</c> wildcard label in front of at least two more labels. Returns null when the host is fine,
    /// otherwise a short plain reason.
    /// </summary>
    public static string? ValidateHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return "is empty";
        }

        if (host == "*")
        {
            return "cannot be a bare '*'";
        }

        if (host.Contains("://", StringComparison.Ordinal))
        {
            return "must not include a scheme such as https://";
        }

        if (host.AsSpan().IndexOfAny('/', '?', '#') >= 0)
        {
            return "must not include a path";
        }

        if (host.Contains(':', StringComparison.Ordinal))
        {
            return "must not include a port";
        }

        var name = host;
        var wildcard = host.StartsWith("*.", StringComparison.Ordinal);
        if (wildcard)
        {
            name = host[2..];
        }

        if (name.Length > 253)
        {
            return "is too long";
        }

        var labels = name.Split('.');
        if (wildcard && labels.Length < 2)
        {
            return "wildcard must sit in front of a full domain such as *.example.com";
        }

        foreach (var label in labels)
        {
            if (label.Length is 0 or > 63
                || label[0] == '-'
                || label[^1] == '-'
                || !label.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                return "must be a host name such as api.example.com or *.example.com";
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="host"/> is the declared <paramref name="pattern"/>: an exact host (letters compared without
    /// regard to case), or for <c>*.example.com</c> any host with at least one more label in front of <c>example.com</c>.
    /// The wildcard never matches <c>example.com</c> itself or an unrelated host that merely ends the same way.
    /// </summary>
    public static bool HostMatches(string pattern, string host)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(host))
        {
            return false;
        }

        host = host.TrimEnd('.');
        if (!pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            return string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);
        }

        var suffix = pattern[1..]; // ".example.com"
        return host.Length > suffix.Length
            && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && !host.StartsWith('.');
    }

    /// <summary>The declared destination that covers <paramref name="host"/> (an exact entry wins over a wildcard), or null when none does.</summary>
    public static PluginNetworkDestination? FindDestination(IReadOnlyList<PluginNetworkDestination> network, string host)
    {
        ArgumentNullException.ThrowIfNull(network);
        return network.FirstOrDefault(destination => !destination.Host.StartsWith("*.", StringComparison.Ordinal) && HostMatches(destination.Host, host))
            ?? network.FirstOrDefault(destination => HostMatches(destination.Host, host));
    }

    /// <summary>
    /// Checks a whole <c>network</c> list against the permissions the manifest declares. Returns a plain load-error
    /// message, or null when the list is fine.
    /// </summary>
    public static string? Validate(IReadOnlyList<string> permissions, IReadOnlyList<PluginNetworkDestination> network)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(network);
        var declaresHttp = permissions.Any(permission =>
            string.Equals(permission, PluginPermissionIds.NetworkHttp, StringComparison.OrdinalIgnoreCase));
        if (declaresHttp && network.Count == 0)
        {
            return "Plugin manifest declares network.http but lists no network destinations. Add a 'network' entry with a host and what is sent there.";
        }

        if (!declaresHttp && network.Count > 0)
        {
            return "Plugin manifest lists network destinations but does not declare the network.http permission.";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var destination in network)
        {
            if (destination is null)
            {
                return "Plugin manifest has an empty network destination.";
            }

            if (ValidateHost(destination.Host) is { } hostProblem)
            {
                return $"Plugin manifest network host '{destination.Host}' {hostProblem}.";
            }

            if (!seen.Add(destination.Host))
            {
                return $"Plugin manifest lists the network host '{destination.Host}' more than once.";
            }

            var sends = destination.Sends?.Trim();
            if (string.IsNullOrEmpty(sends) || sends.Length > MaxSendsLength || sends.Any(char.IsControl))
            {
                return $"Plugin manifest network host '{destination.Host}' must say what it sends in 1 to {MaxSendsLength} characters.";
            }

            if (destination.MaxRequestsPerMinute is { } rate && (rate < MinRequestsPerMinute || rate > MaxRequestsPerMinute))
            {
                return $"Plugin manifest network host '{destination.Host}' must set max_requests_per_minute between {MinRequestsPerMinute} and {MaxRequestsPerMinute}.";
            }
        }

        return null;
    }
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
