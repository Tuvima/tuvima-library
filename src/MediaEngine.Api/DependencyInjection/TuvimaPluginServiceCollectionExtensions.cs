using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Plugins.AiFeatures;
using MediaEngine.Api.Services.Plugins.Declarative;
using MediaEngine.Api.Services.Plugins.Pages;
using MediaEngine.Api.Services.Plugins.Service;
using MediaEngine.Api.Services.Plugins.Sources;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Api.Services.Vault;
using MediaEngine.Domain.Contracts;
using MediaEngine.Plugin.CommercialSkip;
using MediaEngine.Plugin.FandomLore;
using MediaEngine.Plugin.ListenBrainz;
using MediaEngine.Plugin.MediaSegments;
using MediaEngine.Plugins;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MediaEngine.Api.DependencyInjection;

public static class TuvimaPluginServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaPlugins(this IServiceCollection services)
    {
        // Registered in name order, ahead of the assembly scan, so the plugin list is deterministic.
        services.AddSingleton<ITuvimaPlugin, DescriptionInsightsPlugin>();
        services.AddSingleton<ITuvimaPlugin, SeriesOrderPlugin>();
        services.AddSingleton<ITuvimaPlugin, SmartFilingPlugin>();
        services.AddSingleton<ITuvimaPlugin, TasteSummaryPlugin>();
        services.AddSingletonImplementations<ITuvimaPlugin>(
            expectedCount: 4,
            typeof(CommercialSkipPlugin).Assembly,
            typeof(FandomLorePlugin).Assembly,
            typeof(ListenBrainzPlugin).Assembly,
            typeof(SkipMarkersPlugin).Assembly);
        services.AddSingleton<AiFeatureRetractionService>();
        services.AddSingleton<DeclarativeAiStepRunner>();
        services.AddSingleton<PluginSettingsService>();
        services.AddSingleton<IPluginApprovalCheck, PluginApprovalCheck>();
        services.AddSingleton<PluginBlocklist>();
        services.AddSingleton<PluginPageService>();
        services.AddSingleton<PluginMetadataSourceFiles>();
        services.AddSingleton<PluginCatalog>();
        services.AddSingleton<MediaEngine.Api.Services.Settings.LibraryKindListService>();
        // The one merged list of kinds (built-in plus switched-on plugins) that ingestion, the lookup guard, text tracks and artwork read.
        services.AddSingleton<ILibraryKindSource>(sp => sp.GetRequiredService<MediaEngine.Api.Services.Settings.LibraryKindListService>());
        services.AddSingleton<ProviderCapabilityCatalogue>();
        services.AddSingleton<MediaEngine.Api.Services.Settings.PipelineFlowEditor>();
        services.AddSingleton<PluginDirectoryService>();
        services.AddSingleton<PluginSignatureVerifier>();
        services.AddSingleton<PluginPackageInstaller>();
        services.AddSingleton<ILibraryUniverseQidGate, LibraryUniverseQidGate>();
        services.AddSingleton<PluginUniverseLoreService>();
        services.AddSingleton<PluginScheduledSegmentService>();
        services.AddSingleton<PluginToolRuntime>();
        services.AddSingleton<PluginAiClient>();
        services.AddSingleton<PluginAiGateway>();
        services.AddSingleton<IPluginPermissionGate, PluginPermissionGate>();
        services.AddSingleton<IPluginExecutionContextFactory, PluginExecutionContextFactory>();
        services.AddSingleton<PluginSegmentDetectionService>();

        // Host services: own records, recurring jobs, Library events and always-on work (developer-tools WP15).
        services.AddSingleton(PluginHostLimits.Default);
        services.AddSingleton<PluginHostStatusRegistry>();
        services.AddSingleton<PluginHttpRateLimiter>();
        services.AddSingleton<PluginHostServicesReader>();

        // Activity, failure reasons, the automatic pause and the People-action log (Plugin architecture spec D10, C10, C17, C25).
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PluginCircuitBreaker>();
        services.AddSingleton<PluginActivityService>();
        services.AddSingleton<PluginLibraryLimitService>();
        services.AddSingleton<IVaultSectionOnlineLookup, VaultSectionOnlineLookup>();
        services.AddSingleton<IPluginAccessGate, PluginAccessGate>();
        services.AddSingleton<PluginRemovalService>();
        services.AddSingleton<PluginCheckNowService>();
        services.AddSingleton<PluginProfileConnectionService>();
        services.AddSingleton<PluginOutboxService>();
        services.AddSingleton<PluginDisplayService>();
        services.AddSingleton<PluginPlayDetailsService>();
        services.AddSingleton<PlayerPluginActionService>();
        services.AddSingleton<PluginOutboxWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<PluginOutboxWorker>());
        services.AddHostedService<PluginOrphanSweepHostedService>();
        services.AddSingleton<PluginPeopleActionLog>();
        services.AddSingleton<PluginJobScheduler>();
        services.AddSingleton<IScheduledTaskDescriptorSource>(sp => sp.GetRequiredService<PluginJobScheduler>());
        services.AddSingleton<PluginEventDispatcher>();
        services.AddSingleton<PluginBackgroundHost>();

        // Plugins that are separate programs (developer-tools WP18): the supervisor, the keys it hands out and the lookup call.
        services.AddSingleton<PluginKeyRegistry>();
        services.AddSingleton(sp => new PluginServiceHost(
            sp.GetRequiredService<PluginCatalog>(),
            sp.GetRequiredService<PluginHostStatusRegistry>(),
            sp.GetRequiredService<PluginKeyRegistry>(),
            sp.GetRequiredService<PluginHostLimits>(),
            sp.GetRequiredService<TimeProvider>(),
            () => LibraryAddress(sp),
            sp.GetRequiredService<ILogger<PluginServiceHost>>()));
        services.AddSingleton<PluginServiceClient>();
        return services;
    }

    /// <summary>The address programs reach the Library on: this machine, on the port the Engine listens on.</summary>
    private static string? LibraryAddress(IServiceProvider services)
    {
        var address = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses
            .FirstOrDefault(candidate => candidate.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        if (address is null)
        {
            return null;
        }

        return Uri.TryCreate(
            address.Replace("://+", "://localhost", StringComparison.Ordinal)
                .Replace("://*", "://localhost", StringComparison.Ordinal)
                .Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal)
                .Replace("://[::]", "://localhost", StringComparison.Ordinal),
            UriKind.Absolute,
            out var parsed)
            ? parsed.GetLeftPart(UriPartial.Authority)
            : null;
    }
}
