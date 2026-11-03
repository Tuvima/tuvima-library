using System.Text.Json;
using System.Text.Json.Nodes;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Files;

/// <summary>
/// Tuvima's own copy of each plugin file that ships with Library (Plugins and Integrations screens v5 D55): the pristine text that
/// "differs from Tuvima's version", the locked-field comparison, Reset and Restore all measure against. It is kept in the install
/// folder (inside the Engine, or in the <c>shipped-plugins</c> folder beside it), never in the data store, so editing a file can
/// never change the copy it is compared with. A private plugin has none.
/// </summary>
public interface IShippedPluginFiles
{
    /// <summary>The shipped text of a provider source file by file name (<c>tmdb.json</c>), or null when none ships.</summary>
    string? ReadSourceFile(string fileName);

    /// <summary>The shipped text of a plugin manifest by plugin id, or null when none ships.</summary>
    string? ReadManifest(string pluginId);

    /// <summary>The file names of every provider source file that ships with Library, for example <c>tmdb.json</c>.</summary>
    IReadOnlyList<string> SourceFileNames();

    /// <summary>The ids of every plugin manifest that ships with Library.</summary>
    IReadOnlyList<string> ManifestPluginIds();
}

/// <summary>The JSON that ships with Library to describe a plugin that is compiled in: its file name and text.</summary>
public sealed record ShippedPluginDescription(string FileName, string Text);

/// <summary>
/// Finds the shipped JSON that describes a compiled-in plugin (D58), so its page can offer a read-only View file. The locator is a
/// seam: the packet that adds those descriptions registers its own implementation, and until then no plugin has one.
/// </summary>
public interface IShippedPluginDescriptions
{
    /// <summary>The shipped description of the compiled-in plugin with this id, or null when there is none.</summary>
    ShippedPluginDescription? Find(string pluginId);
}

/// <summary>
/// The shipped description of each compiled-in plugin (D58): the settings JSON its assembly embeds (<see cref="ShippedPluginSettings"/>).
/// A plugin is matched to its file by what the file says: the embedded file whose content equals the settings description the plugin
/// reports. A plugin with no settings of its own has no file, so its page has no Plugin file card. Read only (D54).
/// </summary>
public sealed class CompiledPluginDescriptions : IShippedPluginDescriptions
{
    private readonly Dictionary<string, ShippedPluginDescription> _byPlugin = new(StringComparer.OrdinalIgnoreCase);

    public CompiledPluginDescriptions(IEnumerable<ITuvimaPlugin> plugins)
    {
        foreach (var plugin in plugins)
        {
            if (plugin.Manifest.SettingsSchema is not { ValueKind: JsonValueKind.Object } schema)
            {
                continue;
            }

            var assembly = plugin.GetType().Assembly;
            var wanted = JsonNode.Parse(schema.GetRawText());
            foreach (var file in ShippedPluginSettings.FilesIn(assembly))
            {
                var text = ShippedPluginSettings.ReadText(assembly, file);
                if (text is not null && SameJson(text, wanted))
                {
                    _byPlugin[plugin.Manifest.Id] = new ShippedPluginDescription(file, text);
                    break;
                }
            }
        }
    }

    public ShippedPluginDescription? Find(string pluginId) => _byPlugin.GetValueOrDefault(pluginId);

    private static bool SameJson(string text, JsonNode? other)
    {
        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }), other);
        }
        catch (JsonException)
        {
            // An embedded file that is not JSON is a build mistake the settings test reports; here it simply does not match.
            return false;
        }
    }
}

/// <summary>No compiled-in plugin has a shipped description.</summary>
public sealed class NoShippedPluginDescriptions : IShippedPluginDescriptions
{
    public ShippedPluginDescription? Find(string pluginId) => null;
}

/// <summary>Reads the shipped provider files embedded in the Engine and shipped manifests from the <c>shipped-plugins/{id}/plugin.json</c> folder in the install folder.</summary>
public sealed class ShippedPluginFiles(string? manifestRoot = null) : IShippedPluginFiles
{
    private readonly string _manifestRoot = manifestRoot ?? Path.Combine(AppContext.BaseDirectory, "shipped-plugins");

    public string? ReadSourceFile(string fileName) =>
        string.IsNullOrWhiteSpace(fileName) || fileName.AsSpan().IndexOfAny('/', '\\') >= 0 ? null : ShippedProviderFiles.ReadText(fileName);

    public IReadOnlyList<string> SourceFileNames() => ShippedProviderFiles.FileNames();

    public IReadOnlyList<string> ManifestPluginIds()
    {
        try
        {
            return Directory.Exists(_manifestRoot)
                ? Directory.EnumerateDirectories(_manifestRoot)
                    .Select(Path.GetFileName)
                    .OfType<string>()
                    .Where(id => PluginPackageRules.IsSafeId(id) && File.Exists(Path.Combine(_manifestRoot, id, "plugin.json")))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The shipped folder cannot be listed, so no manifest is offered for restoring.
            _ = ex;
            return [];
        }
    }

    public string? ReadManifest(string pluginId)
    {
        if (!PluginPackageRules.IsSafeId(pluginId))
        {
            return null;
        }

        var path = Path.Combine(_manifestRoot, pluginId, "plugin.json");
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The shipped copy is unreadable; the plugin then simply has none to compare with.
            _ = ex;
            return null;
        }
    }
}
