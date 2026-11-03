using MediaEngine.Api.Services.Plugins.Sources;

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

/// <summary>No compiled-in plugin has a shipped description yet.</summary>
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
