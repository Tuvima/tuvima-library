using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.ScheduledTasks;
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
        services.AddSingleton<ILibraryUniverseQidGate, LibraryUniverseQidGate>();
        services.AddSingleton<PluginUniverseLoreService>();
        services.AddSingleton<PluginScheduledSegmentService>();
        services.AddSingleton<PluginToolRuntime>();
        services.AddSingleton<PluginAiClient>();
        services.AddSingleton<IPluginPermissionGate, PluginPermissionGate>();
        services.AddSingleton<IPluginExecutionContextFactory, PluginExecutionContextFactory>();
        services.AddSingleton<PluginSegmentDetectionService>();

        // Host services: own records, recurring jobs, Library events and always-on work (developer-tools WP15).
        services.AddSingleton(PluginHostLimits.Default);
        services.AddSingleton<PluginHostStatusRegistry>();
        services.AddSingleton<PluginHttpRateLimiter>();
        services.AddSingleton<PluginHostServicesReader>();
        services.AddSingleton<PluginJobScheduler>();
        services.AddSingleton<IScheduledTaskDescriptorSource>(sp => sp.GetRequiredService<PluginJobScheduler>());
        services.AddSingleton<PluginEventDispatcher>();
        services.AddSingleton<PluginBackgroundHost>();
        return services;
    }
}
