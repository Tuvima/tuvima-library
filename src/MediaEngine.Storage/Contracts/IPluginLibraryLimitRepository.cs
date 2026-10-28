namespace MediaEngine.Storage.Contracts;

/// <summary>
/// The libraries an administrator limited a plugin or source to (<c>plugin_library_limits</c>). A plugin with no rows works on every
/// library it can work on; with rows, only on those.
/// </summary>
public interface IPluginLibraryLimitRepository
{
    /// <summary>The library ids the plugin is limited to; empty means no limit.</summary>
    Task<IReadOnlyList<string>> GetAsync(string pluginId, CancellationToken ct = default);

    /// <summary>Replaces the plugin's limit. An empty list removes the limit.</summary>
    Task ReplaceAsync(string pluginId, IReadOnlyCollection<string> libraryIds, CancellationToken ct = default);

    /// <summary>Removes every limit of one plugin (it was removed from the Library).</summary>
    Task DeleteForPluginAsync(string pluginId, CancellationToken ct = default);
}
