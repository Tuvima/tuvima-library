namespace MediaEngine.Domain.Services;

/// <summary>
/// Resolves the global Tuvima data folder — the single location where
/// per-Person, per-Universe, per-Character, per-Fictional-Entity assets and
/// the file-hash cache live, separate from any one media library.
///
/// Resolution order at construction:
/// <list type="number">
///   <item><c>TUVIMA_DATA_DIR</c> environment variable.</item>
///   <item>Constructor-supplied <c>configuredPath</c> (typically from
///         <c>core.json → data_directory</c>).</item>
///   <item>Platform default:
///         <c>%LOCALAPPDATA%\Tuvima\data</c> on Windows,
///         <c>~/.local/share/tuvima</c> on Linux/macOS.</item>
/// </list>
///
/// The folder layout is documented in plan
/// <c>.claude/plans/wise-rolling-beacon.md</c> §E. Subdirectory names use
/// the human-readable <c>Name (QID)</c> form for known entities and
/// <c>_pending/{tempId}</c> for entities still under review.
/// </summary>
public sealed class TuvimaDataPaths
{
    /// <summary>
    /// Identifies how the active <see cref="Root"/> was resolved. Surfaced
    /// by the SystemTab so users can see *why* their data lives where it
    /// lives without having to read source code.
    /// </summary>
    public enum PathSource
    {
        /// <summary>Resolved from the <c>TUVIMA_DATA_DIR</c> env var.</summary>
        EnvironmentVariable,
        /// <summary>Resolved from <c>core.json → data_directory</c>.</summary>
        CoreConfig,
        /// <summary>No override present — using the platform default.</summary>
        PlatformDefault,
    }

    /// <summary>Absolute path of the global Tuvima data folder.</summary>
    public string Root { get; }

    /// <summary>How <see cref="Root"/> was selected (env / config / default).</summary>
    public PathSource Source { get; }

    /// <summary>People assets: <c>{Root}/people/{Name (QID)}/...</c></summary>
    public string People => Path.Combine(Root, "people");

    /// <summary>Universe assets: <c>{Root}/universes/{Name (QID)}/...</c></summary>
    public string Universes => Path.Combine(Root, "universes");

    /// <summary>Character assets: <c>{Root}/characters/{Name (QID)}/...</c></summary>
    public string Characters => Path.Combine(Root, "characters");

    /// <summary>Fictional-entity assets: <c>{Root}/fictional/{Name (QID)}/...</c></summary>
    public string Fictional => Path.Combine(Root, "fictional");

    /// <summary>
    /// Holding area for entities still in a <i>needs review</i> state — they
    /// have no confirmed name yet, so they live under
    /// <c>{Root}/_pending/{tempId}/</c> until promoted.
    /// </summary>
    public string Pending => Path.Combine(Root, "_pending");

    /// <summary>
    /// Sidecar parse cache — extracted Kodi NFO / ABS metadata.json content
    /// keyed by source-file hash. Phase 2/3 of the side-by-side plan.
    /// </summary>
    public string SidecarsCache => Path.Combine(Root, "sidecars-cache");

    /// <summary>
    /// SQLite file backing the file-hash cache table
    /// <c>(absolute_path, size_bytes, mtime_utc → sha256)</c> used by the
    /// initial sweep. Stored under the global folder so the cache is shared
    /// across libraries on the same host.
    /// </summary>
    public string HashCacheDatabase => Path.Combine(Root, "hash-cache.db");

    /// <param name="configuredPath">
    /// Optional path from <c>core.json → data_directory</c>. May be
    /// <see langword="null"/> or whitespace; the env var still takes
    /// precedence and the platform default still applies as a fallback.
    /// </param>
    public TuvimaDataPaths(string? configuredPath = null)
    {
        var envPath = Environment.GetEnvironmentVariable("TUVIMA_DATA_DIR");

        if (!string.IsNullOrWhiteSpace(envPath))
        {
            Root = Path.GetFullPath(envPath);
            Source = PathSource.EnvironmentVariable;
        }
        else if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            Root = Path.GetFullPath(configuredPath);
            Source = PathSource.CoreConfig;
        }
        else
        {
            Root = Path.GetFullPath(GetPlatformDefault());
            Source = PathSource.PlatformDefault;
        }
    }

    /// <summary>
    /// Ensures <see cref="Root"/> exists on disk. Subdirectories
    /// (people / universes / characters / fictional) are created lazily by
    /// the writers that need them — eagerly creating them would clutter the
    /// data folder for users who never enrich a particular entity type.
    /// </summary>
    public void EnsureRootExists()
    {
        Directory.CreateDirectory(Root);
    }

    /// <summary>
    /// Placeholder the shipped <c>core.json</c> and <c>libraries.json</c> use instead of an absolute path, so a fresh
    /// install follows wherever this server keeps its data (Windows, Linux or a container). A value that starts with
    /// it is replaced by <see cref="Root"/> when the file is loaded.
    /// </summary>
    public const string ConfigToken = "{TuvimaData}";

    /// <summary>
    /// Replaces a leading <see cref="ConfigToken"/> in one configured path with the resolved data folder; any other
    /// value is returned unchanged.
    /// </summary>
    public static string? ExpandConfigToken(string? value)
    {
        if (value is null || !value.StartsWith(ConfigToken, StringComparison.Ordinal))
        {
            return value;
        }

        var rest = value[ConfigToken.Length..].TrimStart('/', '\\');
        var root = new TuvimaDataPaths().Root;
        return rest.Length == 0 ? root : Path.GetFullPath(Path.Combine(root, rest));
    }

    /// <summary>
    /// Expands <see cref="ConfigToken"/> in every string value of a configuration file's JSON text. Text without the
    /// placeholder is returned untouched.
    /// </summary>
    public static string ExpandConfigTokens(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!json.Contains(ConfigToken, StringComparison.Ordinal))
        {
            return json;
        }

        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
        if (node is null)
        {
            return json;
        }

        ExpandInPlace(node);
        return node.ToJsonString();
    }

    private static void ExpandInPlace(System.Text.Json.Nodes.JsonNode node)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToList())
                {
                    ExpandChild(obj[key], replacement => obj[key] = replacement);
                }
                break;
            case System.Text.Json.Nodes.JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    var position = index;
                    ExpandChild(array[position], replacement => array[position] = replacement);
                }
                break;
        }
    }

    private static void ExpandChild(System.Text.Json.Nodes.JsonNode? child, Action<System.Text.Json.Nodes.JsonNode?> replace)
    {
        if (child is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text))
        {
            replace(System.Text.Json.Nodes.JsonValue.Create(ExpandConfigToken(text)));
        }
        else if (child is not null)
        {
            ExpandInPlace(child);
        }
    }

    private static string GetPlatformDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            // %LOCALAPPDATA%\Tuvima\data
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Tuvima", "data");
        }

        // ~/.local/share/tuvima  (also fine for macOS — keeps the layout uniform
        // across non-Windows platforms instead of branching to ~/Library/Application Support).
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "tuvima");
    }
}
