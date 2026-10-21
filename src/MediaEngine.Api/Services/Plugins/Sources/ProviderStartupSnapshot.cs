namespace MediaEngine.Api.Services.Plugins.Sources;

/// <summary>The plugin-supplied metadata sources that were switched on when the Engine started. Sources are read once at startup, so a difference from now means a restart is needed.</summary>
public sealed class ProviderStartupSnapshot(IEnumerable<string> enabledPluginSourceNames)
{
    public IReadOnlySet<string> EnabledPluginSourceNames { get; } = new HashSet<string>(enabledPluginSourceNames, StringComparer.OrdinalIgnoreCase);
}
