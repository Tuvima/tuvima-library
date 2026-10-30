namespace MediaEngine.Api.Services.Plugins.Sources;

/// <summary>
/// The plugin-supplied metadata sources that are switched on in the Engine's running provider list. Sources join and leave
/// the running list as plugins are switched on and off, so a difference from the provider files only appears if a source
/// could not be taken in, and then the Engine has to restart.
/// </summary>
public sealed class ProviderStartupSnapshot(IEnumerable<string> enabledPluginSourceNames)
{
    private volatile IReadOnlySet<string> _names = new HashSet<string>(enabledPluginSourceNames, StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> EnabledPluginSourceNames => _names;

    /// <summary>Records the plugin sources the running provider list now holds.</summary>
    public void Update(IEnumerable<string> enabledPluginSourceNames) =>
        _names = new HashSet<string>(enabledPluginSourceNames, StringComparer.OrdinalIgnoreCase);
}
