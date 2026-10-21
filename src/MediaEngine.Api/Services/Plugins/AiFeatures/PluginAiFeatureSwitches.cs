using MediaEngine.AI.Infrastructure;

namespace MediaEngine.Api.Services.Plugins.AiFeatures;

/// <summary>
/// Answers "is this Local AI feature on" from the plugin behind it. A plugin counts as on only when it is switched on,
/// approved and loaded, which is exactly what <see cref="PluginRegistration.Enabled"/> already says. The catalog is
/// looked up on first use because the plugins themselves depend on the services that ask this question.
/// </summary>
internal sealed class PluginAiFeatureSwitches(IServiceProvider services) : IAiFeatureSwitches
{
    private PluginCatalog? _catalog;

    private PluginCatalog Catalog => _catalog ??= services.GetRequiredService<PluginCatalog>();

    public bool IsOn(AiFeature feature) => IsPluginOn(AiFeaturePluginIds.For(feature));

    public bool AnyOn => AiFeaturePluginIds.All.Any(IsPluginOn);

    private bool IsPluginOn(string pluginId) =>
        Catalog.Get(pluginId) is { Enabled: true, LoadError: null };
}
