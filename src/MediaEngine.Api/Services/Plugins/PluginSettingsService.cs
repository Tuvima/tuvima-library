using System.Text.Json;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugins;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Services.Plugins;

public sealed class PluginSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly string _configRoot;
    private readonly ILogger<PluginSettingsService> _logger;

    public PluginSettingsService(IConfigurationLoader configurationLoader, ILogger<PluginSettingsService>? logger = null)
    {
        _logger = logger ?? NullLogger<PluginSettingsService>.Instance;
        var core = configurationLoader.LoadCore();
        var root = string.IsNullOrWhiteSpace(core.LibraryRoot)
            ? Path.GetFullPath(".data")
            : Path.Combine(core.LibraryRoot, ".data");
        _configRoot = Path.Combine(root, "plugin-config");
    }

    public PluginUserConfiguration Load(PluginManifest manifest)
    {
        var path = GetPath(manifest.Id);
        if (!File.Exists(path))
        {
            return new PluginUserConfiguration
            {
                PluginId = manifest.Id,
                Enabled = false,
                Settings = new Dictionary<string, JsonElement>(manifest.DefaultSettings, StringComparer.OrdinalIgnoreCase),
            };
        }

        try
        {
            var config = JsonSerializer.Deserialize<PluginUserConfiguration>(File.ReadAllText(path), JsonOptions);
            if (config is null)
            {
                return new PluginUserConfiguration { PluginId = manifest.Id };
            }

            config.Settings ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in manifest.DefaultSettings)
            {
                config.Settings.TryAdd(key, value);
            }

            return config;
        }
        catch (Exception ex)
        {
            // An unreadable settings file must not take the plugin down: report it and fall back to the safe
            // default (switched off, manifest default settings).
            _logger.LogWarning(ex, "Plugin {PluginId} settings file {SettingsPath} could not be read; using defaults", manifest.Id, path);
            return new PluginUserConfiguration
            {
                PluginId = manifest.Id,
                Enabled = false,
                Settings = new Dictionary<string, JsonElement>(manifest.DefaultSettings, StringComparer.OrdinalIgnoreCase),
            };
        }
    }

    public void Save(PluginUserConfiguration config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(config.PluginId);
        Directory.CreateDirectory(_configRoot);
        File.WriteAllText(GetPath(config.PluginId), JsonSerializer.Serialize(config, JsonOptions));
    }

    public void SetEnabled(PluginManifest manifest, bool enabled)
    {
        var config = Load(manifest);
        config.Enabled = enabled;
        Save(config);
    }

    public void Delete(string pluginId)
    {
        var path = GetPath(pluginId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetPath(string pluginId)
    {
        var safe = string.Concat(pluginId.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_'));
        return Path.Combine(_configRoot, $"{safe}.json");
    }
}

public sealed class PluginUserConfiguration
{
    public string PluginId { get; set; } = "";
    public bool Enabled { get; set; }
    public Dictionary<string, JsonElement> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
