using MediaEngine.Api.DependencyInjection;
using MediaEngine.Api.Realtime;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.DependencyInjection;
using MediaEngine.Plugins;
using MediaEngine.Providers.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Configuration;
using MediaEngine.Storage.Contracts;
using MediaEngine.Storage.Secrets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

public sealed class EngineCompositionValidationTests
{
    [Fact]
    public async Task FullCompositionGraph_GivesTheMutationGateTheMergeLedger()
    {
        var repoRoot = FindRepoRoot();
        var configuration = new ConfigurationManager();
        var configLoader = new ConfigurationDirectoryLoader(Path.Combine(repoRoot, "config"));
        var databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_di_gate_{Guid.NewGuid():N}.db");
        var database = new DatabaseConnection(databasePath);
        try
        {
            DapperConfiguration.Configure();
            database.InitializeSchema();
            database.RunStartupChecks();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IConfigurationLoader>(configLoader);
            services.AddSingleton<IDatabaseConnection>(database);
            services.AddTuvimaStorage();
            services.AddMediaEngineIngestion(configuration, configLoader);
            using var provider = services.BuildServiceProvider();

            // The Engine's own gate must read the merge ledger: with it, a merge original is never deleted by ordinary code.
            var root = Path.Combine(Path.GetTempPath(), "tuvima_gate_root");
            var original = Path.Combine(root, "Dune.2021.mkv");
            await provider.GetRequiredService<IFolderOrganiseLogRepository>().UpsertAsync(new FolderOrganiseLogEntry(
                Guid.NewGuid(), Guid.NewGuid(), original, Path.Combine(root, "copy.mkv"), FolderOrganiseOutcomes.Copied, DateTimeOffset.UtcNow));
            var source = new MediaEngine.Domain.Configuration.LibrarySourceConfig
            {
                Id = Guid.NewGuid().ToString("D"),
                Path = root,
                ManagementMode = MediaEngine.Domain.Configuration.LibrarySourceManagementModes.ManagedByTuvima,
                AccessMode = MediaEngine.Domain.Configuration.LibrarySourceAccessModes.Writable,
            };
            var library = new MediaEngine.Domain.Configuration.LibraryFolderConfig { Id = "lib", Sources = [source] };
            var gate = provider.GetRequiredService<MediaEngine.Ingestion.Contracts.ISourceMutationPolicyGate>();

            var decision = gate.Evaluate(new MediaEngine.Ingestion.Models.SourceMutationRequest
            {
                Source = MediaEngine.Ingestion.Services.FileSourceMutationPolicyFactory.Create(library, source, allowDelete: true),
                Mutation = MediaEngine.Ingestion.Models.SourceMutationKind.Delete,
                Path = original,
            });

            Assert.False(decision.Allowed);
        }
        finally
        {
            database.Dispose();
            foreach (var leftover in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                try
                {
                    File.Delete(leftover);
                }
                catch (IOException)
                {
                    // A temporary data store that is still locked is left for the operating system's temp clean-up.
                }
            }
        }
    }

    [Fact]
    public void FullCompositionGraph_ValidatesWithoutConstructingRuntimeResources()
    {
        var services = BuildFullComposition();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    private static ServiceCollection BuildFullComposition()
    {
        var repoRoot = FindRepoRoot();
        var configuration = new ConfigurationManager();
        var configLoader = new ConfigurationDirectoryLoader(
            Path.Combine(repoRoot, "config"));
        var database = new DatabaseConnection(
            Path.Combine(Path.GetTempPath(), $"tuvima_di_{Guid.NewGuid():N}.db"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddDataProtection();
        services.AddSignalR();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IConfigurationLoader>(configLoader);
        services.AddSingleton<IDatabaseConnection>(database);
        services.AddSingleton<IHostApplicationLifetime, TestHostApplicationLifetime>();
        services.AddSingleton<IEventPublisher, SignalREventPublisher>();
        services.AddSingleton<ISecretStore, DataProtectionSecretStore>();
        // Program.cs builds this one before the container (the config loader shares it), so it is registered by hand here too.
        services.AddSingleton<IConfigSecretsStore>(new ConfigFolderSecretsStore(Path.Combine(repoRoot, "config")));

        services.AddTuvimaStorage();
        services.AddSingleton<IPermissionRegistry, PermissionRegistry>();
        services.AddScoped<IRequestAuthorityResolver, RequestAuthorityResolver>();
        services.AddScoped<CatalogueResourceAuthorizationService>();
        services.AddScoped<DashboardAuthorityProjector>();
        services.AddScoped<IAccountAccessMutationService, AccountAccessMutationService>();
        services.AddScoped<ApplicationAdministrationService>();
        services.AddScoped<IntegrationHealthService>();
        services.AddScoped<IGrantAdminUnlockService, GrantAdminUnlockService>();
        services.AddScoped<IAccountAccessDecisionService, AccountAccessDecisionService>();
        services.AddScoped<ISelfServiceAuthorizationService>(provider =>
            (AccountAccessDecisionService)provider.GetRequiredService<IAccountAccessDecisionService>());
        services.AddScoped<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AuthorizationEvaluator>();
        services.AddSingleton<IAuthorizationInvalidationService, AuthorizationInvalidationService>();
        services.AddSingleton<IAuthorizationAuditWriter, AuthorizationAuditWriter>();
        services.AddSingleton<MediaEngine.Identity.Contracts.ITwoStepSecretProtector, DataProtectionTwoStepSecretProtector>();
        services.AddScoped<IAuthorizationHandler, EffectiveAdministratorHandler>();
        services.AddScoped<IAuthorizationHandler, HumanSelfServiceHandler>();
        services.AddScoped<IAuthorizationHandler, ApplicationPermissionHandler>();
        services.AddScoped<IAuthorizationHandler, AdministratorOrApplicationHandler>();
        services.AddScoped<IAuthorizationHandler, HumanOrApplicationPermissionHandler>();
        services.AddTuvimaPlayback();
        services.AddMediaEngineIngestion(configuration, configLoader);
        services.AddTuvimaDisplay();
        services.AddTuvimaIntelligence();
        services.AddTuvimaProviders(configLoader);
        services.AddTuvimaAi(configLoader);
        services.AddTuvimaPlugins();
        services.AddTuvimaScheduledTasks();
        services.AddPluginApplicationServices();
        services.AddApplicationEvents();
        services.AddApplicationWebhooks();
        services.AddTuvimaApprovals();
        services.AddTuvimaHostedServices();
        services.AddTuvimaToolRequirements();
        services.AddTuvimaNotifications();
        services.AddSingleton(sp => new DatabaseBackupService(sp.GetRequiredService<IDatabaseConnection>(), Path.GetTempPath()));
        services.AddTuvimaBackupDestinations();

        return services;
    }

    [Fact]
    public void ProviderRegistrationOrder_IsPinned()
    {
        var services = BuildFullComposition();

        using var provider = services.BuildServiceProvider();

        // Order is behaviour: it decides which provider answers first and how settings list them.
        Assert.Equal(
            string.Join(", ", ExpectedMetadataProviderOrder),
            string.Join(", ", provider.GetServices<IExternalMetadataProvider>().Select(candidate => candidate.Name)));
        Assert.Equal(
            string.Join(", ", ExpectedTextTrackProviderOrder),
            string.Join(", ", provider.GetServices<ITextTrackProvider>().Select(candidate => candidate.Name)));
    }

    [Fact]
    public void ProviderHttpClients_KeepTheirTimeoutAndCanonicalUserAgent()
    {
        var services = BuildFullComposition();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();

        // TVDB keeps the 20 s timeout and the shared user agent whether or not its file names one.
        // The resilience handler later lifts the client-wide timeout, so read the registration's own action.
        using var tvdb = new HttpClient();
        options.Get("tvdb").HttpClientActions[0](tvdb);
        Assert.Equal(TimeSpan.FromSeconds(20), tvdb.Timeout);
        Assert.Equal(
            TuvimaHttpClientRegistration.CanonicalUserAgent,
            tvdb.DefaultRequestHeaders.UserAgent.ToString());

        // SubDL never follows redirects with credentials and caps buffered responses at 1 MiB.
        using var subdl = new HttpClient();
        foreach (var action in options.Get("subdl").HttpClientActions)
        {
            action(subdl);
        }

        Assert.Equal(1024 * 1024, subdl.MaxResponseContentBufferSize);
    }

    private static readonly string[] ExpectedMetadataProviderOrder =
        ["apple_api", "comicvine", "musicbrainz", "tmdb", "wikidata_reconciliation", "tvdb"];
    private static readonly string[] ExpectedTextTrackProviderOrder = ["lrclib", "subdl"];

    [Fact]
    public void ConventionRegisteredStrategiesAndPlugins_ResolveAsDeterministicSingletons()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTuvimaIntelligence();
        services.AddTuvimaPlugins();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        var strategies = provider
            .GetServices<IMediaTypeIdentityStrategy>()
            .ToArray();
        var plugins = provider
            .GetServices<ITuvimaPlugin>()
            .ToArray();

        Assert.Equal(6, strategies.Length);
        Assert.Equal(11, plugins.Length);
        Assert.Equal(
            strategies.Select(strategy => strategy.GetType().FullName).Order(),
            strategies.Select(strategy => strategy.GetType().FullName));
        Assert.Equal(
            plugins.Select(plugin => plugin.GetType().FullName).Order(),
            plugins.Select(plugin => plugin.GetType().FullName));
        Assert.Same(
            strategies[0],
            provider.GetServices<IMediaTypeIdentityStrategy>().First());
        Assert.Same(
            plugins[0],
            provider.GetServices<ITuvimaPlugin>().First());
    }

    [Fact]
    public void CompositionEntryPoints_RegisterExpectedServiceLifetimes()
    {
        var services = new ServiceCollection();
        services.AddTuvimaDisplay();
        services.AddTuvimaPlayback();

        var displayAndDetail = services.Where(descriptor =>
                descriptor.ServiceType.Namespace?.StartsWith(
                    "MediaEngine.Api.Services.Display",
                    StringComparison.Ordinal) == true
                || descriptor.ServiceType.Namespace?.StartsWith(
                    "MediaEngine.Api.Services.Details",
                    StringComparison.Ordinal) == true).ToList();
        var requestScoped = new HashSet<Type>
        {
            typeof(IDisplayProjectionReadService),
            typeof(ContributorShelfReadService),
            typeof(DisplayComposerService),
            // Recent reads reapply the request's profile authority and Mine View scope.
            typeof(RecentCatalogueReadService),
            typeof(DisplayRecentComposerService),
        };
        Assert.All(
            displayAndDetail.Where(descriptor => !requestScoped.Contains(descriptor.ServiceType)),
            descriptor => Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime));
        Assert.All(
            displayAndDetail.Where(descriptor => requestScoped.Contains(descriptor.ServiceType)),
            descriptor => Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime));
        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType.Name == "PlayerService"
                && descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
