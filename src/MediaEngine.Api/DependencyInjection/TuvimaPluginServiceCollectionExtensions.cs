using MediaEngine.Api.Services.Plugins;
using MediaEngine.Plugin.CommercialSkip;
using MediaEngine.Plugin.FandomLore;
using MediaEngine.Plugin.MediaSegments;
using MediaEngine.Plugins;

namespace MediaEngine.Api.DependencyInjection;

public static class TuvimaPluginServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaPlugins(this IServiceCollection services)
    {
        services.AddSingletonImplementations<ITuvimaPlugin>(
            expectedCount: 6,
            typeof(CommercialSkipPlugin).Assembly,
            typeof(FandomLorePlugin).Assembly,
            typeof(IntroSkipPlugin).Assembly);
        services.AddSingleton<PluginSettingsService>();
        services.AddSingleton<IPluginApprovalCheck, PluginApprovalCheck>();
        services.AddSingleton<PluginCatalog>();
        services.AddSingleton<PluginDirectoryService>();
        services.AddSingleton<PluginUniverseLoreService>();
        services.AddSingleton<PluginScheduledSegmentService>();
        services.AddSingleton<PluginToolRuntime>();
        services.AddSingleton<PluginAiClient>();
        services.AddSingleton<IPluginPermissionGate, PluginPermissionGate>();
        services.AddSingleton<IPluginExecutionContextFactory, PluginExecutionContextFactory>();
        services.AddSingleton<PluginSegmentDetectionService>();
        return services;
    }
}
