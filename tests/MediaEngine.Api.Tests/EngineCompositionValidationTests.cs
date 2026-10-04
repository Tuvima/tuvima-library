using MediaEngine.Api.DependencyInjection;
using MediaEngine.Api.Realtime;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Display;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.DependencyInjection;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using MediaEngine.Storage.Configuration;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Api.Tests;

public sealed class EngineCompositionValidationTests
{
    [Fact]
    public void FullCompositionGraph_ValidatesWithoutConstructingRuntimeResources()
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

        services.AddTuvimaStorage();
        services.AddSingleton<IPermissionRegistry, PermissionRegistry>();
        services.AddScoped<IRequestAuthorityResolver, RequestAuthorityResolver>();
        services.AddScoped<CatalogueResourceAuthorizationService>();
        services.AddScoped<DashboardAuthorityProjector>();
        services.AddScoped<IAccountAccessMutationService, AccountAccessMutationService>();
        services.AddScoped<ApplicationAdministrationService>();
        services.AddScoped<IGrantAdminUnlockService, GrantAdminUnlockService>();
        services.AddScoped<IAccountAccessDecisionService, AccountAccessDecisionService>();
        services.AddScoped<ISelfServiceAuthorizationService>(provider =>
            (AccountAccessDecisionService)provider.GetRequiredService<IAccountAccessDecisionService>());
        services.AddScoped<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AuthorizationEvaluator>();
        services.AddSingleton<IAuthorizationInvalidationService, AuthorizationInvalidationService>();
        services.AddSingleton<IAuthorizationAuditWriter, AuthorizationAuditWriter>();
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
        services.AddPluginApplicationServices();
        services.AddApplicationEvents();
        services.AddApplicationWebhooks();
        services.AddTuvimaHostedServices();

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
        Assert.Equal(6, plugins.Length);
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
