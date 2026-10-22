using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins.Service;

/// <summary>
/// A plugin that is a separate program (kind <c>service</c>). No code of it is loaded into the Library: the program runs on
/// its own, talks to the Library only through the plugin door (<c>/plugin-host/v1</c>) with a key limited to this plugin,
/// and the Library answers it only for what the plugin was approved to do.
/// </summary>
internal sealed class ServicePlugin(PluginManifest manifest, ServiceDefinition definition) : ITuvimaPlugin
{
    public PluginManifest Manifest { get; } = manifest;

    public ServiceDefinition Definition { get; } = definition;

    public IReadOnlyList<IPluginCapability> CreateCapabilities() => [new ServiceLookupCapability(Definition)];

    /// <summary>Builds the plugin for a manifest, or returns the plain reason it cannot be built.</summary>
    public static (ITuvimaPlugin? Plugin, string? Error) TryCreate(PluginManifest manifest, string? pluginDirectory)
    {
        var (definition, error) = ServicePluginParser.Parse(manifest, pluginDirectory);
        return definition is null ? (null, error) : (new ServicePlugin(manifest, definition), null);
    }
}
