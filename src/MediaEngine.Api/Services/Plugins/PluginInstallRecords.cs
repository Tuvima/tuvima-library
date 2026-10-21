using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>What the Library remembers about a plugin it installed from the directory.</summary>
public sealed record PluginInstallRecord
{
    [JsonPropertyName("plugin_id")]
    public string PluginId { get; init; } = "";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    /// <summary><c>verified</c> (checked against a Tuvima signature) or <c>community</c> (checksum only).</summary>
    [JsonPropertyName("trust")]
    public string Trust { get; init; } = "community";

    /// <summary>SHA-256 of the installed <c>plugin.json</c>; if the file changes later, the plugin is no longer the package that was checked.</summary>
    [JsonPropertyName("manifest_sha256")]
    public string ManifestSha256 { get; init; } = "";

    [JsonPropertyName("package_sha256")]
    public string PackageSha256 { get; init; } = "";

    [JsonPropertyName("installed_at")]
    public DateTimeOffset InstalledAt { get; init; }
}

/// <summary>
/// The install records, one small file per plugin, kept next to (not inside) the plugins folder so a package can never
/// ship its own. A plugin is trusted as Verified or Community only while its record exists and its manifest is still the
/// one that was installed; anything else in the plugins folder is a private (sideloaded) plugin.
/// </summary>
public sealed class PluginInstallRecords
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _root;

    public PluginInstallRecords(string root)
    {
        _root = root;
    }

    public PluginInstallRecord? Find(string pluginId)
    {
        var path = PathFor(pluginId);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PluginInstallRecord>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable record means the plugin is treated as private (the least trusted level).
            return null;
        }
    }

    public void Save(PluginInstallRecord record)
    {
        var path = PathFor(record.PluginId) ?? throw new InvalidOperationException("The plugin id cannot be used as a file name.");
        Directory.CreateDirectory(_root);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Json));
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(string pluginId)
    {
        var path = PathFor(pluginId);
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>The trust the Library gives a plugin file on disk: its install record's level while the manifest is unchanged, otherwise private.</summary>
    public PluginTrustLevel TrustFor(PluginManifest manifest, string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || Find(manifest.Id) is not { } record
            || !string.Equals(record.Version, manifest.Version, StringComparison.OrdinalIgnoreCase))
        {
            return PluginTrustLevel.Private;
        }

        try
        {
            if (!string.Equals(HashFile(manifestPath), record.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            {
                return PluginTrustLevel.Private;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PluginTrustLevel.Private;
        }

        return string.Equals(record.Trust, "verified", StringComparison.Ordinal) ? PluginTrustLevel.Verified : PluginTrustLevel.Community;
    }

    public static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    // Plugin ids come from outside, so only a plain name is allowed to become a file name.
    private string? PathFor(string pluginId) =>
        PluginPackageRules.IsSafeId(pluginId) ? Path.Combine(_root, pluginId.ToLowerInvariant() + ".json") : null;
}
