using System.Globalization;
using System.Text.Json;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Contracts.Import;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Services.Import;

/// <summary>
/// A plugin's settings file (Plugin architecture spec C15): the choices on one plugin, for a backup or for moving to another
/// machine. It holds the plugin's id and its settings and never a key or token (those live in the Engine's secret store and are
/// left out). The shared Import recognises it, says which plugin it is for and how many settings it holds, and, for an
/// administrator, writes the settings onto that plugin. The plugin's on/off switch is not part of the file.
/// </summary>
public sealed class PluginSettingsImportKindHandler : IImportKindHandler
{
    /// <summary>The <c>format</c> value a plugin settings file carries.</summary>
    public const string FormatMarker = "tuvima-plugin-settings";

    private readonly PluginCatalog _catalog;

    public PluginSettingsImportKindHandler(PluginCatalog catalog) => _catalog = catalog;

    public string Kind => ImportKinds.PluginSettings;

    public int Priority => 30;

    public bool Detect(ImportSource source) => !source.IsZip && TryRead(source.Text, out _, out _);

    public Task<ImportPreviewDto> PreviewAsync(ImportSource source, ImportContext context, CancellationToken ct)
    {
        TryRead(source.Text, out var pluginId, out var settings);
        var registration = _catalog.Get(pluginId!);
        var facts = new List<ImportFactDto>
        {
            new("Plugin", registration?.Manifest.Name ?? pluginId!),
            new("Settings", settings!.Count.ToString(CultureInfo.InvariantCulture)),
        };

        var known = registration is not null;
        return Task.FromResult(new ImportPreviewDto
        {
            Kind = Kind,
            KindLabel = "Plugin settings",
            Recognised = true,
            Heading = registration?.Manifest.Name ?? pluginId!,
            Subheading = $"{settings.Count} setting{(settings.Count == 1 ? string.Empty : "s")}",
            Facts = facts,
            Notes = ["The file holds the plugin's choices only. No keys or tokens are in it, and the plugin stays switched on or off as it is."],
            Message = known ? null : "That plugin is not installed on this Library, so its settings cannot be added.",
            CanApply = known,
            ApplyLabel = "Add plugin settings",
        });
    }

    public Task<ImportApplyResultDto> ApplyAsync(ImportSource source, ImportContext context, CancellationToken ct)
    {
        if (!TryRead(source.Text, out var pluginId, out var settings))
        {
            return Task.FromResult(new ImportApplyResultDto(false, "This is not a plugin settings file."));
        }

        var registration = _catalog.Get(pluginId!);
        if (registration is null)
        {
            return Task.FromResult(new ImportApplyResultDto(false, "That plugin is not installed on this Library."));
        }

        // The file's choices are laid over the plugin's current ones, so a setting the file does not mention is left alone.
        var merged = registration.Settings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings!)
        {
            merged[key] = value;
        }

        try
        {
            _catalog.SaveSettings(registration.Manifest.Id, merged);
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(new ImportApplyResultDto(false, ex.Message));
        }

        return Task.FromResult(new ImportApplyResultDto(
            true,
            $"Added {settings.Count} setting{(settings.Count == 1 ? string.Empty : "s")} to {registration.Manifest.Name}."));
    }

    /// <summary>Writes the file for a plugin's settings. Used by tests, and by the export when it is built.</summary>
    public static string Write(string pluginId, IReadOnlyDictionary<string, JsonElement> settings) =>
        JsonSerializer.Serialize(
            new { format = FormatMarker, version = 1, plugin_id = pluginId, settings },
            MediaEngineJson.Indented);

    private static bool TryRead(string text, out string? pluginId, out Dictionary<string, JsonElement>? settings)
    {
        pluginId = null;
        settings = null;
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{')
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format)
                || !string.Equals(format.GetString(), FormatMarker, StringComparison.Ordinal)
                || !root.TryGetProperty("plugin_id", out var id)
                || id.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(id.GetString())
                || !root.TryGetProperty("settings", out var values)
                || values.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            pluginId = id.GetString()!.Trim();
            settings = values.EnumerateObject().ToDictionary(
                property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
