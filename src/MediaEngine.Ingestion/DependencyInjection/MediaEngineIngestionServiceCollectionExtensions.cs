using MediaEngine.Domain.Capabilities;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Organise;
using MediaEngine.Ingestion.Pipeline;
using MediaEngine.Ingestion.Services;
using MediaEngine.Processors;
using MediaEngine.Processors.Contracts;
using MediaEngine.Processors.Processors;
using MediaEngine.Providers.Contracts;
using MediaEngine.Providers.Helpers;
using MediaEngine.Providers.Services;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Ingestion.DependencyInjection;

/// <summary>
/// Internal Tuvima Library registration surface for the ingestion pipeline.
/// The Engine remains the host; this library only contributes services.
/// </summary>
public static class MediaEngineIngestionServiceCollectionExtensions
{
    public static IServiceCollection AddMediaEngineIngestion(
        this IServiceCollection services,
        IConfiguration configuration,
        IConfigurationLoader configLoader,
        Action<MediaEngineIngestionRegistrationOptions>? configure = null)
    {
        var options = new MediaEngineIngestionRegistrationOptions();
        configure?.Invoke(options);

        if (options.ConfigureOptions)
        {
            ConfigureIngestionOptions(services, configuration, configLoader);
        }

        if (options.CreateConfiguredDirectories)
        {
            EnsureConfiguredDirectories(configLoader);
        }

        services.TryAddSingleton<IMediaTypeExtensionCatalog, MediaTypeExtensionCatalog>();
        services.TryAddSingleton<ILibraryFolderResolver, LibraryFolderResolver>();
        services.TryAddSingleton<IInitialSweepService, InitialSweepService>();
        services.TryAddSingleton<IAssetHasher, AssetHasher>();
        services.TryAddSingleton<IFileWatcher, FileWatcher>();
        services.TryAddSingleton<DebounceQueue>();
        services.TryAddSingleton<IFileOrganizer, FileOrganizer>();
        services.TryAddSingleton<ISourceMutationPolicyGate, SourceMutationPolicyGate>();
        services.TryAddSingleton<IReorganizationPlanner, ReorganizationPlanner>();
        services.TryAddSingleton<IReorganizationFileSystem, SystemReorganizationFileSystem>();
        services.TryAddSingleton<IReorganizationExecutor, ReorganizationExecutor>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMetadataTagger, EpubMetadataTagger>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMetadataTagger, AudioMetadataTagger>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMetadataTagger, VideoMetadataTagger>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMetadataTagger, ComicMetadataTagger>());
        services.TryAddSingleton<WritebackConfigState>();
        services.TryAddSingleton<WriteBackService>();
        services.TryAddSingleton<IWriteBackService>(sp => sp.GetRequiredService<WriteBackService>());
        services.TryAddSingleton<IWriteBackOutcomeService>(sp => sp.GetRequiredService<WriteBackService>());
        services.TryAddSingleton<IBackgroundWorker, BackgroundWorker>();
        services.TryAddSingleton<IOrganizationGate, OrganizationGate>();
        services.TryAddSingleton<IImportFolderService, ImportFolderService>();
        services.TryAddSingleton<ImportFileMover>();
        services.TryAddSingleton<ImportFilingJournal>();
        services.TryAddSingleton<IImportFolderFiler, ImportFolderFiler>();
        services.TryAddSingleton<IImportFolderWaitingReader, ImportFolderWaitingReader>();
        services.TryAddSingleton<IOrganiseFileMover, SystemOrganiseFileMover>();
        services.TryAddSingleton<SidecarMover>();
        services.TryAddSingleton<AutoOrganizeService>();
        services.TryAddSingleton<IAutoOrganizeService>(sp => sp.GetRequiredService<AutoOrganizeService>());
        services.TryAddSingleton<IFileIntake, IngestionEngineFileIntake>();
        services.TryAddSingleton<FolderConversionOrganiser>();
        services.TryAddSingleton<FolderOrganiseRunner>();
        services.TryAddSingleton<FolderConversionPreviewer>();
        services.TryAddSingleton<ILibraryScanner, LibraryScanner>();
        services.TryAddSingleton<IMediaTypeResolver, MediaTypeResolver>();
        services.TryAddSingleton<IDuplicateResolver, DuplicateResolver>();
        services.TryAddSingleton<IIngestionLogScribe, IngestionLogScribe>();
        services.TryAddSingleton(sp => new HashDedupeStageDependencies(
            sp.GetService<IFileHashCacheRepository>()));
        services.TryAddSingleton(sp => new ScoreIdentifyStageDependencies(
            sp.GetService<CapabilityPlanner>()));
        services.TryAddSingleton(sp => new OrganizeStageDependencies(
            sp.GetService<StageOutcomeFactory>()));
        services.TryAddSingleton(sp => new WriteBackStageDependencies(
            sp.GetService<IEntityAssetRepository>(),
            sp.GetService<AssetPathService>(),
            sp.GetService<IWorkRepository>(),
            sp.GetService<IAssetExportService>()));
        services.TryAddSingleton(sp => new IdentityJobStageDependencies(
            sp.GetService<IIdentityPipelineSignal>()));
        services.TryAddSingleton<IEnrichmentConcurrencyLimiter>(sp =>
            new EnrichmentConcurrencyLimiter(
                sp.GetRequiredService<IConfigurationLoader>().LoadHydration(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<EnrichmentConcurrencyLimiter>>()));

        services.TryAddSingleton<IProcessorRouter>(sp =>
        {
            var router = new MediaProcessorRouter();
            router.Register(new EpubProcessor());
            router.Register(new AzW3Processor());
            router.Register(new PdfProcessor());
            router.Register(new DiscImageProcessor());
            router.Register(new AudioProcessor());
            router.Register(new VideoProcessor(sp.GetRequiredService<IVideoMetadataExtractor>()));
            router.Register(new ComicProcessor());
            router.Register(new GenericFileProcessor(sp.GetRequiredService<IMediaTypeExtensionCatalog>()));
            return router;
        });

        services.TryAddSingleton<MediaEngine.Storage.Playback.PlaybackStateRepository>();
        services.TryAddSingleton<PlaybackInspectionWriter>();
        services.TryAddSingleton<IngestionEngine>();
        services.TryAddSingleton<IIngestionEngine>(sp => sp.GetRequiredService<IngestionEngine>());

        if (options.RegisterHostedService)
        {
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<IngestionEngine>());
        }

        return services;
    }

    private static void ConfigureIngestionOptions(
        IServiceCollection services,
        IConfiguration configuration,
        IConfigurationLoader configLoader)
    {
        services.Configure<IngestionOptions>(configuration.GetSection(IngestionOptions.SectionName));

        // Saving libraries.json refreshes the folder list the file-source policy reads, with no restart.
        var libraryChanges = new LibrariesOptionsChangeTokenSource(configLoader);
        services.AddSingleton(libraryChanges);
        services.AddSingleton<Microsoft.Extensions.Options.IOptionsChangeTokenSource<IngestionOptions>>(libraryChanges);

        services.PostConfigure<IngestionOptions>(opts =>
        {
            opts.WatchDirectories = [];

            try
            {
                CoreConfiguration core = configLoader.LoadCore();
                if (!string.IsNullOrWhiteSpace(core.LibraryRoot))
                {
                    opts.LibraryRoot = core.LibraryRoot;
                    opts.AutoOrganize = true;
                }
                if (!string.IsNullOrWhiteSpace(core.OrganizationTemplate))
                {
                    opts.OrganizationTemplate = core.OrganizationTemplate;
                }

                if (core.OrganizationTemplates.Count > 0)
                {
                    opts.OrganizationTemplates = new Dictionary<string, string>(core.OrganizationTemplates, StringComparer.OrdinalIgnoreCase);
                }

                opts.ConfiguredLanguage = core.Language.Metadata;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARN] Failed to load core configuration for ingestion options - using defaults: {ex.Message}");
            }

            try
            {
                var disambiguation = configLoader.LoadDisambiguation();
                opts.MediaTypeAutoAssignThreshold = disambiguation.MediaTypeAutoAssignThreshold;
                opts.MediaTypeReviewThreshold = disambiguation.MediaTypeReviewThreshold;
            }
            catch
            {
                // First run - defaults from IngestionOptions stand.
            }

            try
            {
                var libraries = configLoader.LoadLibraries();
                opts.DuplicatePolicy = libraries.FileHandling.DuplicatePolicy;
                opts.LibraryFolders = libraries.Libraries
                    .Select(LibraryFolderEntryFactory.Create)
                    .Where(e => e.EffectiveSourcePaths.Count > 0)
                    .ToList();

                LibraryFolderResolver.ValidateNoOverlap(opts.LibraryFolders);

                var sourcePaths = opts.LibraryFolders
                    .Where(folder => string.Equals(
                        folder.Kind,
                        LibraryKinds.Catalogued,
                        StringComparison.OrdinalIgnoreCase))
                    .SelectMany(folder => folder.EffectiveSourcePaths)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var importFolder = ImportFolderRules.Find(libraries);
                opts.ImportFolderPath = importFolder?.Path;
                if (importFolder is not null)
                {
                    sourcePaths.Add(importFolder.Path);
                }

                opts.WatchDirectories = sourcePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"[ERROR] config/libraries.json: {ex.Message}");
                throw;
            }
            catch
            {
                // No libraries.json or parse failure - library folder priors will not be applied.
            }
        });
    }

    private static void EnsureConfiguredDirectories(IConfigurationLoader configLoader)
    {
        try
        {
            var libraries = configLoader.LoadLibraries();

            // Tuvima's own library folder is created here the first time the server runs; nobody is asked about it.
            foreach (var location in libraries.StorageLocations.Where(location =>
                         location.AllowWrite
                         && string.Equals(location.Id, TuvimaOwnFolders.LibraryLocationId, StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(location.Path)))
            {
                Directory.CreateDirectory(location.Path);
            }

            foreach (var lib in libraries.Libraries)
            {
                var allPaths = lib.Sources
                    .Where(source => source.ManagementMode == LibrarySourceManagementModes.ManagedByTuvima)
                    .Select(source => source.Path)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var path in allPaths)
                {
                    Directory.CreateDirectory(path);
                }
            }
        }
        catch
        {
            // No libraries.json or source directory creation failed - non-fatal.
        }

        try
        {
            var coreForDataDir = configLoader.LoadCore();
            if (string.IsNullOrWhiteSpace(coreForDataDir.LibraryRoot))
            {
                return;
            }

            Directory.CreateDirectory(Path.Combine(coreForDataDir.LibraryRoot, ".data", "staging"));
            Directory.CreateDirectory(Path.Combine(coreForDataDir.LibraryRoot, ".data", "assets", "artwork"));
            Directory.CreateDirectory(Path.Combine(coreForDataDir.LibraryRoot, ".data", "assets", "derived"));
            Directory.CreateDirectory(Path.Combine(coreForDataDir.LibraryRoot, ".data", "assets", "metadata"));
            Directory.CreateDirectory(Path.Combine(coreForDataDir.LibraryRoot, ".data", "assets", "transcripts"));
            Directory.CreateDirectory(Path.Combine(coreForDataDir.LibraryRoot, ".data", "assets", "subtitle-cache"));
            Directory.CreateDirectory(Path.Combine(coreForDataDir.LibraryRoot, ".data", "assets", "text-tracks"));
            Directory.CreateDirectory(Path.Combine(coreForDataDir.LibraryRoot, ".data", "assets", "people"));
            Directory.CreateDirectory(Path.Combine(coreForDataDir.LibraryRoot, ".data", "database"));
        }
        catch
        {
            // LibraryRoot not yet configured or directory creation failed - non-fatal.
        }
    }
}

public sealed class MediaEngineIngestionRegistrationOptions
{
    public bool ConfigureOptions { get; set; } = true;
    public bool CreateConfiguredDirectories { get; set; } = true;
    public bool RegisterHostedService { get; set; } = true;
}
