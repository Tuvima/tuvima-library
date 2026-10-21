using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Review;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Identity.Contracts;
using MediaEngine.Providers.Adapters;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Providers;
using MediaEngine.Providers.Services;
using MediaEngine.Providers.Workers;

namespace MediaEngine.Api.DependencyInjection;

public static class TuvimaProviderServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaProviders(
        this IServiceCollection services,
        IConfigurationLoader configLoader)
    {
        AddHttpClientsAndConfiguredProviders(services, configLoader);
        AddWikidataReconciler(services, configLoader);
        AddReconciliationProvider(services, configLoader);

        services.AddSingleton<MetadataHarvestQueue>();
        services.AddSingleton<IMetadataHarvestQueueAdmission>(sp =>
            sp.GetRequiredService<MetadataHarvestQueue>());
        services.AddSingleton<MetadataHarvestingService>();
        services.AddSingleton<IMetadataHarvestingService>(sp =>
            sp.GetRequiredService<MetadataHarvestingService>());
        services.AddSingleton<IRecursiveIdentityService, RecursiveIdentityService>();
        services.AddSingleton<PipelineExecutionSnapshotProvider>();
        services.AddSingleton<IPipelineExecutionSnapshotProvider>(sp =>
            sp.GetRequiredService<PipelineExecutionSnapshotProvider>());
        services.AddSingleton<IPersonReconciliationService, PersonReconciliationService>();
        services.AddSingleton<ICanonDiscrepancyService, CanonDiscrepancyService>();

        services.AddSingleton<INarrativeRootResolver, NarrativeRootResolver>();
        services.AddSingleton<IRecursiveFictionalEntityService, RecursiveFictionalEntityService>();
        services.AddSingleton<IRelationshipPopulationService, RelationshipPopulationService>();
        services.AddSingleton<IWorkAdaptationEdgeService, WorkAdaptationEdgeService>();
        services.AddSingleton<IUniverseGraphQueryService, UniverseGraphQueryService>();
        services.AddSingleton<IArtworkPaletteService, ArtworkPaletteService>();
        services.AddSingleton<ILoreDeltaService, LoreDeltaService>();
        services.AddSingleton<IEraActorResolverService, EraActorResolverService>();
        services.AddSingleton<ImageDownloadCoordinator>();
        services.AddSingleton<ImageEnrichmentService>();
        services.AddSingleton<IImageEnrichmentService>(sp => sp.GetRequiredService<ImageEnrichmentService>());
        services.AddSingleton<IHydrationPipelineService, SynchronousIdentityPipelineService>();
        services.AddSingleton<IReviewMoveToTvService, ReviewMoveToTvService>();
        services.AddSingleton<DeferredEnrichmentService>();
        services.AddSingleton<IDeferredEnrichmentService>(sp =>
            sp.GetRequiredService<DeferredEnrichmentService>());

        services.AddSingleton<BridgeIdHelper>();
        services.AddSingleton<StageOutcomeFactory>();
        services.AddSingleton<TimelineRecorder>();
        services.AddSingleton<CoverArtWorker>();
        services.AddSingleton<PersonImageEnrichmentWorker>();
        services.AddSingleton<PersonEnrichmentWorker>();
        services.AddSingleton<ChildEntityWorker>();
        services.AddSingleton<FictionalEntityWorker>();
        services.AddSingleton<DescriptionEnrichmentWorker>();
        services.AddSingleton<TextTrackEnrichmentWorker>();
        services.AddSingleton<IEnrichmentService, EnrichmentService>();
        services.AddSingleton<IUniverseEnrichmentScheduler>(sp =>
            sp.GetRequiredService<UniverseEnrichmentService>());
        services.AddSingleton<SeriesAmbiguityReviewer>();
        services.AddSingleton<CollectionAssignmentService>();
        services.AddSingleton<CollectionFinalizationService>();
        services.AddSingleton<WikidataSeriesManifestHydrationService>();
        services.AddSingleton<RetailRequestBuilder>();
        services.AddSingleton<IProviderRateLimiterCoordinator, ProviderRateLimiterCoordinator>();
        services.AddSingleton<AppleRetailClient>();
        services.AddSingleton<MusicBrainzReleaseClient>();
        // Retail-match services that still name TMDB; they move with the Plugins work (WP20).
        services.AddSingleton<TmdbRetailClient>();
        services.AddSingleton<TmdbTvSuggestionService>();
        services.AddSingleton<ITmdbSeasonEpisodeCatalogue, TmdbSeasonEpisodeCatalogue>();
        services.AddSingleton<ITvEpisodeCrosswalk, TvEpisodeCrosswalk>();
        services.AddSingleton<ProviderApiClient>();
        AddOrderedProviders(services, configLoader);
        services.AddSingleton<RetailCandidateScorer>();
        services.AddSingleton<RetailMatchWorker>();
        services.AddSingleton<WikidataBridgeWorker>();
        services.AddSingleton<WikidataMatchPreviewService>();
        services.AddSingleton<QuickHydrationWorker>();
        services.AddSingleton<IIdentityPipelineSignal, IdentityPipelineSignal>();
        services.AddSingleton<PostPipelineService>();

        services.AddSingleton<ProviderHealthMonitorService>();
        services.AddSingleton<IProviderHealthMonitor>(sp =>
            sp.GetRequiredService<ProviderHealthMonitorService>());
        // Shared by the Settings/Setup endpoints and the startup provider connection checks.
        services.AddSingleton<ProviderCredentialService>();
        services.AddSingleton<InitialSweepCommandService>();
        services.AddSingleton<IInitialSweepCommandService>(sp =>
            sp.GetRequiredService<InitialSweepCommandService>());
        services.AddSingleton<UniverseEnrichmentService>();
        services.AddSingleton<EnrichmentRefreshScheduleService>();
        return services;
    }

    private static void AddHttpClientsAndConfiguredProviders(
        IServiceCollection services,
        IConfigurationLoader configLoader)
    {
        services.AddTuvimaHttpClient(
            "wikidata_reconciliation",
            TimeSpan.FromSeconds(30));
        services.AddTuvimaHttpClient("cover_download", TimeSpan.FromSeconds(20));
        services.AddTuvimaHttpClient("headshot_download", TimeSpan.FromSeconds(20));
        // Plugin tools can legitimately run for several minutes. The standard
        // resilience pipeline's much shorter attempt timeout would silently
        // change that contract, so retain the original long-running client.
        services.AddTuvimaHttpClient(
            "plugin_tools",
            TimeSpan.FromMinutes(10),
            addStandardResilience: false);
        services.AddTuvimaHttpClient("plugin_catalog", TimeSpan.FromSeconds(15));

        var providerConfigurations = configLoader.LoadAllProviders();
        foreach (var providerConfig in ConfigDrivenProviders(providerConfigurations).Where(config => config.RegistrationOrder <= 0))
        {
            RegisterConfigDrivenProvider(services, providerConfig);
        }

        foreach (var providerConfig in providerConfigurations
                     .Where(config => string.Equals(
                         config.AdapterType,
                         "text_track",
                         StringComparison.OrdinalIgnoreCase)))
        {
            var clientRegistration = services.AddTuvimaHttpClient(
                providerConfig.Name,
                TimeSpan.FromSeconds(providerConfig.HttpClient?.TimeoutSeconds ?? 15),
                providerConfig.HttpClient?.UserAgent);
            var http = providerConfig.TextTrack?.Http;
            if (http is { AllowAutoRedirect: false })
            {
                // Credentials must never follow an unverified download redirect.
                clientRegistration.ConfigurePrimaryHttpMessageHandler(() =>
                    new HttpClientHandler { AllowAutoRedirect = false });
            }

            if (http is { MaxResponseBufferBytes: > 0 })
            {
                clientRegistration.ConfigureHttpClient(client =>
                    client.MaxResponseContentBufferSize = http.MaxResponseBufferBytes);
            }

            var capturedConfig = providerConfig;
            services.AddSingleton<ITextTrackProvider>(sp =>
                new TextTrackProviderAdapter(
                    capturedConfig,
                    sp.GetRequiredService<IHttpClientFactory>(),
                    sp.GetRequiredService<IProviderHealthMonitor>(),
                    sp.GetRequiredService<ILogger<TextTrackProviderAdapter>>(),
                    sp.GetRequiredService<IProviderRateLimiterCoordinator>(),
                    sp.GetRequiredService<IProviderResponseCacheRepository>()));
        }
    }

    private static IEnumerable<ProviderConfiguration> ConfigDrivenProviders(IEnumerable<ProviderConfiguration> configurations) =>
        configurations.Where(config => string.Equals(config.AdapterType, "config_driven", StringComparison.OrdinalIgnoreCase));

    private static void RegisterConfigDrivenProvider(IServiceCollection services, ProviderConfiguration providerConfig)
    {
        services.AddTuvimaHttpClient(
            providerConfig.Name,
            TimeSpan.FromSeconds(providerConfig.HttpClient?.TimeoutSeconds ?? 10),
            providerConfig.HttpClient?.UserAgent);

        var capturedConfig = providerConfig;
        services.AddSingleton<IExternalMetadataProvider>(sp =>
            new ConfigDrivenAdapter(
                capturedConfig,
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILogger<ConfigDrivenAdapter>>(),
                sp.GetRequiredService<IProviderHealthMonitor>(),
                sp.GetRequiredService<IProviderResponseCacheRepository>(),
                sp.GetRequiredService<IProviderRateLimiterCoordinator>(),
                sp.GetRequiredService<ProviderApiClient>()));
    }

    /// <summary>
    /// Providers whose file sets <c>registration_order</c> above 0: they register after the ordinary
    /// file-based providers and the built-in reconciliation provider, lowest number first.
    /// Registration order decides enumeration order, which decides which provider answers first.
    /// </summary>
    private static void AddOrderedProviders(IServiceCollection services, IConfigurationLoader configLoader)
    {
        foreach (var providerConfig in ConfigDrivenProviders(configLoader.LoadAllProviders())
                     .Where(config => config.RegistrationOrder > 0)
                     .OrderBy(config => config.RegistrationOrder))
        {
            RegisterConfigDrivenProvider(services, providerConfig);
        }
    }

    private static void AddWikidataReconciler(
        IServiceCollection services,
        IConfigurationLoader configLoader)
    {
        var coreConfig = configLoader.LoadCore();
        var options = new Tuvima.Wikidata.WikidataReconcilerOptions
        {
            UserAgent = TuvimaHttpClientRegistration.CanonicalUserAgent,
            Language = coreConfig.Language.Metadata,
            MaxLag = 0,
            TypeHierarchyDepth = 3,
            WikidataRateLimit = new Tuvima.Wikidata.ProviderRateLimitOptions
            {
                MaxConcurrentRequests = 3,
                RequestsPerSecond = 3,
                MaxBatchSize = 50,
            },
            WikipediaRateLimit = new Tuvima.Wikidata.ProviderRateLimitOptions
            {
                MaxConcurrentRequests = 3,
                RequestsPerSecond = 3,
                MaxBatchSize = 50,
            },
            CommonsRateLimit = new Tuvima.Wikidata.ProviderRateLimitOptions
            {
                MaxConcurrentRequests = 2,
                RequestsPerSecond = 2,
                MaxBatchSize = 50,
            },
            IncludeSitelinkLabels = true,
            UniqueIdProperties = new HashSet<string>
            {
                "P213", "P214", "P227", "P244", "P268", "P269", "P349",
                "P496", "P906", "P1006", "P1015", "P1566", "P2427",
                "P212", "P957", "P345", "P4947", "P5749", "P434", "P436",
            },
        };

        // Tuvima.Wikidata owns retries and concurrency, so this one intentionally
        // skips the app-level resilience handler.
        services.AddTuvimaHttpClient(
            "WikidataReconciliation",
            options.Timeout,
            options.UserAgent,
            addStandardResilience: false);
        services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<IHttpClientFactory>()
                .CreateClient("WikidataReconciliation");
            return new Tuvima.Wikidata.WikidataReconciler(client, options);
        });
        services.AddSingleton(sp => sp.GetRequiredService<Tuvima.Wikidata.WikidataReconciler>().Reconcile);
        services.AddSingleton(sp => sp.GetRequiredService<Tuvima.Wikidata.WikidataReconciler>().Entities);
        services.AddSingleton(sp => sp.GetRequiredService<Tuvima.Wikidata.WikidataReconciler>().Wikipedia);
        services.AddSingleton(sp => sp.GetRequiredService<Tuvima.Wikidata.WikidataReconciler>().Editions);
        services.AddSingleton(sp => sp.GetRequiredService<Tuvima.Wikidata.WikidataReconciler>().Children);
        services.AddSingleton(sp => sp.GetRequiredService<Tuvima.Wikidata.WikidataReconciler>().Authors);
        services.AddSingleton(sp => sp.GetRequiredService<Tuvima.Wikidata.WikidataReconciler>().Labels);
        services.AddSingleton(sp => sp.GetRequiredService<Tuvima.Wikidata.WikidataReconciler>().Persons);
        services.AddSingleton(sp => sp.GetRequiredService<Tuvima.Wikidata.WikidataReconciler>().Bridge);
    }

    private static void AddReconciliationProvider(
        IServiceCollection services,
        IConfigurationLoader configLoader)
    {
        var reconciliationConfig =
            configLoader.LoadConfig<ReconciliationProviderConfig>(
                "providers",
                "wikidata_reconciliation");
        if (reconciliationConfig is null)
        {
            Console.Error.WriteLine(
                "[WARN] config/providers/wikidata_reconciliation.json not found - " +
                "ReconciliationAdapter will not be registered.");
            return;
        }

        services.AddSingleton(sp =>
            new CommonsImageResolver(
                reconciliationConfig,
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILogger<CommonsImageResolver>>()));
        services.AddSingleton(sp =>
            new ReconciliationAdapter(
                reconciliationConfig,
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILogger<ReconciliationAdapter>>(),
                sp.GetRequiredService<IFuzzyMatchingService>(),
                sp.GetRequiredService<IProviderResponseCacheRepository>(),
                sp.GetRequiredService<IConfigurationLoader>(),
                sp.GetService<Tuvima.Wikidata.WikidataReconciler>(),
                sp.GetRequiredService<CommonsImageResolver>()));
        services.AddSingleton<IExternalMetadataProvider>(sp =>
            sp.GetRequiredService<ReconciliationAdapter>());
    }
}
