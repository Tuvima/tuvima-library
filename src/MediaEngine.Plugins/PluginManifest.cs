using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Domain.Configuration;

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

    /// <summary>
    /// One sentence of at most 90 characters, starting with what the plugin does, for the tile and the row (Plugins and
    /// Integrations screens v4 D35). Absent means the first sentence of <see cref="Description"/> is used.
    /// </summary>
    [JsonPropertyName("short_description")]
    public string? ShortDescription { get; init; }

    /// <summary>
    /// What the plugin provides to Library, from the fixed list in <c>PluginProvides</c> (Identification, Details, Artwork, People,
    /// Series order, Lyrics, Subtitles, Skip markers, Insights, Lists &amp; ratings, Account sync). Unknown words are ignored.
    /// </summary>
    [JsonPropertyName("provides")]
    public IReadOnlyList<string> Provides { get; init; } = [];

    /// <summary>
    /// Which tab the plugin is listed under on the Plugins page: <c>metadata</c>, <c>media-processing</c>, <c>services</c> or
    /// <c>utilities</c>. Absent or unknown means <c>utilities</c> (see <see cref="PluginCategories"/>).
    /// </summary>
    [JsonPropertyName("category")]
    public string? Category { get; init; }

    /// <summary>
    /// The media types the plugin applies to (<c>books</c>, <c>comics</c>, <c>audiobooks</c>, <c>movies</c>, <c>tv</c>,
    /// <c>music</c>, <c>photos</c>). Empty means every type.
    /// </summary>
    [JsonPropertyName("media_types")]
    public IReadOnlyList<string> MediaTypes { get; init; } = [];

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

    /// <summary>
    /// The shared declaration block (Plugin architecture spec PA1): who may use the plugin, what it requires, how heavy it is,
    /// what it stores and how it fails. Absent means administrators only with every other default. Read through
    /// <see cref="PluginDeclarationRules.Resolve"/>.
    /// </summary>
    [JsonPropertyName("declaration")]
    public PluginDeclaration? Declaration { get; init; }

    /// <summary>
    /// Languages the plugin's own screens and messages are written in (tags such as <c>en</c>, <c>fr</c>, <c>pt-BR</c>).
    /// Empty means English only. The plugin's words come from <c>strings/&lt;tag&gt;.json</c> inside its folder and follow the
    /// server display language through <see cref="PluginStringTable"/>; English is the fallback.
    /// </summary>
    [JsonPropertyName("supported_languages")]
    public IReadOnlyList<string> SupportedLanguages { get; init; } = [];

    /// <summary>
    /// True for a built-in that exists so its settings and switch are ready but has nothing to do yet (its hook is not built).
    /// It is left off the Plugins page until it does.
    /// </summary>
    [JsonPropertyName("hidden")]
    public bool Hidden { get; init; }

    /// <summary>The declaration with every default filled in. Nothing is sent out when the manifest lists no network destinations.</summary>
    [JsonIgnore]
    public ResolvedPluginDeclaration ResolvedDeclaration =>
        PluginDeclarationRules.Resolve(Declaration, Network.Count > 0, MediaTypes, Category);
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

    /// <summary>
    /// How a profile links this credential without typing it: approve a short code on another device. Only for a credential
    /// declared personal. Absent means the credential is entered by hand.
    /// </summary>
    [JsonPropertyName("sign_in")]
    public PluginDeviceSignIn? SignIn { get; init; }
}

/// <summary>
/// "Approve on another device" sign-in for a personal credential (Plugin architecture spec C13). The Library asks <see cref="Host"/>
/// for a short code, shows it with the site's address, and polls until the person has approved it on a phone or computer. The
/// access token that comes back is kept as the profile's credential, encrypted like any other. The app's own client id (and
/// client secret, when the site wants one) are household secrets the administrator entered for the plugin.
/// </summary>
public sealed record PluginDeviceSignIn
{
    /// <summary>The only method there is for now: <c>device_code</c>.</summary>
    [JsonPropertyName("method")]
    public string Method { get; init; } = PluginDeviceSignInMethods.DeviceCode;

    /// <summary>The host the codes and tokens come from. It must be one of the plugin's <c>network</c> hosts.</summary>
    [JsonPropertyName("host")]
    public string Host { get; init; } = "";

    /// <summary>Where a code is requested (a path on <see cref="Host"/>).</summary>
    [JsonPropertyName("code_path")]
    public string CodePath { get; init; } = "";

    /// <summary>Where the code is exchanged for a token once approved.</summary>
    [JsonPropertyName("token_path")]
    public string TokenPath { get; init; } = "";

    /// <summary>Where a token that ran out is renewed with its refresh token. Absent means a token cannot be renewed.</summary>
    [JsonPropertyName("refresh_path")]
    public string? RefreshPath { get; init; }

    /// <summary>The household secret that holds the app's client id.</summary>
    [JsonPropertyName("client_id_secret")]
    public string ClientIdSecret { get; init; } = "";

    /// <summary>The household secret that holds the app's client secret, or null when the site wants none.</summary>
    [JsonPropertyName("client_secret_secret")]
    public string? ClientSecretSecret { get; init; }

    /// <summary>Headers the site asks every request to carry, besides the client id (for example an API version).</summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The header that carries the client id on the site's requests, or null when the site does not use one.</summary>
    [JsonPropertyName("client_id_header")]
    public string? ClientIdHeader { get; init; }
}

/// <summary>The names of the sign-in methods a personal credential can use.</summary>
public static class PluginDeviceSignInMethods
{
    public const string DeviceCode = "device_code";
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
