using MediaEngine.Domain.Capabilities;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.Models;
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
        services.TryAddSingleton<IWriteBackService, WriteBackService>();
        services.TryAddSingleton<IBackgroundWorker, BackgroundWorker>();
        services.TryAddSingleton<IOrganizationGate, OrganizationGate>();
        services.TryAddSingleton<IAutoOrganizeService, AutoOrganizeService>();
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
                opts.LibraryFolders = libraries.Libraries
                    .Select(l =>
                    {
                        return new LibraryFolderEntry
                        {
                            Id = l.Id,
                            Name = l.Name,
                            PreserveOriginals = l.OrganizationPolicy.PreserveOriginals,
                            OrganizationMode = l.OrganizationPolicy.Mode,
                            CustomOrganizationTemplate = l.OrganizationPolicy.CustomTemplate,
                            DuplicatePolicy = l.DuplicatePolicy,
                            Kind = l.Kind,
                            Area = l.Area,
                            Presentation = l.Presentation,
                            MetadataPolicy = l.MetadataPolicy,
                            Sources = l.Sources.Select(source => new LibrarySourceEntry
                            {
                                Id = source.Id,
                                Path = source.Path,
                                Role = source.Role,
                                ManagementMode = source.ManagementMode,
                                AccessMode = source.AccessMode,
                                IncludeSubdirectories = source.IncludeSubdirectories,
                                ParticipatesInOrganization = source.ParticipatesInOrganization,
                                IntakeRole = source.IntakeRole,
                                WritebackOverride = source.WritebackOverride,
                            }).ToList(),
                            PrimaryDestinationSourceId = l.PrimaryDestinationSourceId,
                            MediaTypes = l.MediaTypes
                                .Select(MediaTypeParser.Parse)
                                .Where(mt => mt != MediaType.Unknown)
                                .ToList(),
                            AcceptedIntakeModes = l.AcceptedIntakeModes.ToList(),
                        };
                    })
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
                opts.WatchDirectories = sourcePaths;
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
