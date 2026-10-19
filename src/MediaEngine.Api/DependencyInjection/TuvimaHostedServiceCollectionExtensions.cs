using MediaEngine.Api.Services;
using MediaEngine.Api.Services.EngineLogs;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.LocalAssets.Filing;
using MediaEngine.Api.Services.LocalAssets.Ocr;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Providers.Services;
using MediaEngine.Storage.Services;

namespace MediaEngine.Api.DependencyInjection;

public static class TuvimaHostedServiceCollectionExtensions
{
    public static IServiceCollection AddTuvimaHostedServices(this IServiceCollection services)
    {
        services.AddSingleton<EnrichmentPipelineExecutionGate>();

        // Logs page and log/activity retention (recurring runs are recorded through the ScheduledTaskRunner).
        services.AddSingleton(EngineLogLocation.FromEnvironment());
        services.AddSingleton<ITechnicalLogService, TechnicalLogService>();
        services.AddSingleton<ILogExportService, LogExportService>();
        services.AddSingleton<ActivityRetentionPruner>();
        services.AddHostedService<LogFileRetentionService>();

        // IHostedService.StartAsync methods are awaited sequentially before Kestrel
        // accepts requests. Keep the two startup data repairs first.
        services.AddHostedService<UISettingsCacheWarmupHostedService>();
        services.AddHostedService<OrphanReviewQueuePurgeHostedService>();
        services.AddHostedService<AiUnavailableQuarantineRecoveryHostedService>();
        services.AddHostedService<ScheduledTaskRunRecoveryHostedService>();

        services.AddHostedService<EncodeQueueService>();
        services.AddHostedService(provider => provider.GetRequiredService<AdaptiveHlsCleanupService>());
        services.AddHostedService<FolderHealthService>();
        services.AddHostedService(sp => sp.GetRequiredService<MetadataHarvestingService>());
        services.AddHostedService(sp => sp.GetRequiredService<DeferredEnrichmentService>());
        services.AddHostedService<ProviderActivityBroadcastService>();
        services.AddHostedService(sp => sp.GetRequiredService<ProviderHealthMonitorService>());
        services.AddHostedService<ProviderStartupCheckService>();
        services.AddHostedService<ActivityPruningService>();
        services.AddHostedService<MediaOperationRecoveryHostedService>();
        services.AddHostedService<FolderOrganiseJobWorker>();
        services.AddHostedService<MediaFileWriteIntentDispatcher>();
        services.AddHostedService<IngestionBatchProgressHostedService>();
        services.AddSingleton<IngestionQueueProgressService>();
        services.AddSingleton<IIngestionQueueProgressService>(sp => sp.GetRequiredService<IngestionQueueProgressService>());
        services.AddHostedService(sp => sp.GetRequiredService<IngestionQueueProgressService>());
        services.AddHostedService<TextTrackRefreshOperationWorker>();
        services.AddSingleton<DocumentTextReadService>();
        services.AddHostedService<DocumentTextReadOperationWorker>();
        services.AddSingleton<DocumentFilingService>();
        services.AddHostedService<DocumentApplyRulesOperationWorker>();
        services.AddSingleton<IOcrProcessRunner, OcrProcessRunner>();
        services.AddSingleton<IOcrService, OcrService>();
        services.AddSingleton<DocumentOcrQueue>();
        services.AddTuvimaHttpClient(OcrLanguageDownloader.HttpClientName, TimeSpan.FromMinutes(10), addStandardResilience: false);
        services.AddSingleton<OcrLanguageDownloader>();
        services.AddSingleton<DocumentOcrService>();
        services.AddHostedService<DocumentOcrOperationWorker>();
        services.AddHostedService<ArtworkRenditionRepairStartupService>();
        services.AddHostedService<RejectedFileCleanupService>();
        services.AddSingleton<ViewSourceIndexingOptions>();
        services.AddSingleton<IViewSourceWatcherFactory, FileSystemViewSourceWatcherFactory>();
        services.AddSingleton<ViewSourceIndexingHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<ViewSourceIndexingHostedService>());
        services.AddHostedService<RetagSweepWorker>();
        services.AddSingleton<ArtworkWritebackService>();
        services.AddHostedService<ArtworkWritebackWorker>();
        services.AddHostedService<MissingUniverseSweepService>();

        // Recovery must finish before any identity worker can lease jobs.
        services.AddHostedService<HydrationStartupSweepService>();
        services.AddHostedService(sp => sp.GetRequiredService<InitialSweepCommandService>());
        services.AddHostedService(sp => sp.GetRequiredService<LibraryReconciliationService>());
        services.AddHostedService<StorageMaintenanceHostedService>();

        services.AddHostedService<ModelAutoDownloadService>();
        services.AddHostedService<VibeBatchService>();
        services.AddHostedService<SeriesAlignmentBackgroundService>();
        services.AddHostedService<DescriptionIntelligenceBatchService>();
        services.AddHostedService(sp => sp.GetRequiredService<UniverseEnrichmentService>());
        services.AddHostedService<EnrichmentRefreshScheduleWorker>();
        services.AddHostedService<PeopleEnrichmentHostedService>();
        services.AddHostedService<RetailMatchHostedService>();
        services.AddHostedService<WikidataBridgeHostedService>();
        services.AddHostedService<QuickHydrationHostedService>();
        services.AddSingleton<IIdentityBypassLibraries, ConfiguredIdentityBypassLibraries>();
        services.AddHostedService<WorkReadinessService>();
        services.AddHostedService<MusicBrainzEnrichmentHostedService>();
        services.AddHostedService<MusicAlbumManifestHostedService>();
        services.AddHostedService<HardwareBenchmarkBackgroundService>();
        services.AddHostedService(sp => sp.GetRequiredService<PluginScheduledSegmentService>());
        return services;
    }
}
