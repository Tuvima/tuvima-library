using MediaEngine.AI.EngineRoom;
using MediaEngine.Api.DependencyInjection;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>The Engine's own service registration builds the plugin host services and wires them into Scheduled tasks.</summary>
public sealed class PluginHostServicesCompositionTests
{
    [Fact]
    public void TheEnginesRegistration_BuildsTheHostServices_AndListsPluginJobsInTheScheduledTasksRegistry()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tuvima-plugin-host-di-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "tuvima.db");
        var database = new DatabaseConnection(databasePath);
        try
        {
            DapperConfiguration.Configure();
            database.InitializeSchema();
            database.RunStartupChecks();
            var configDirectory = Path.Combine(root, "config");
            Directory.CreateDirectory(configDirectory);
            var loader = new ConfigurationDirectoryLoader(configDirectory);
            loader.SaveCore(new MediaEngine.Domain.Configuration.CoreConfiguration { LibraryRoot = Path.Combine(root, "library") });

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationManager());
            services.AddSingleton<IConfigurationLoader>(loader);
            services.AddSingleton<IDatabaseConnection>(database);
            services.AddHttpClient();
            services.AddDataProtection();
            services.AddSingleton<ISecretStore, MediaEngine.Api.Services.DataProtectionSecretStore>();
            services.AddSingleton<IAiEngineRoom, NeverCalledEngineRoom>();
            services.AddSingleton<IConfigSecretsStore>(new MediaEngine.Storage.Secrets.ConfigFolderSecretsStore(configDirectory));
            services.AddTuvimaStorage();
            services.AddSingleton<MediaEngine.Domain.Events.IApplicationEventRepository, ApplicationEventRepository>();
            services.AddTuvimaPlugins();
            services.AddSingleton<ScheduledTaskRunner>();
            services.AddSingleton<IScheduledTaskRegistry>(sp =>
            {
                var registry = new ScheduledTaskRegistry();
                foreach (var source in sp.GetServices<IScheduledTaskDescriptorSource>())
                {
                    registry.AddSource(source);
                }

                return registry;
            });
            using var provider = services.BuildServiceProvider();

            Assert.NotNull(provider.GetRequiredService<PluginJobScheduler>());
            Assert.NotNull(provider.GetRequiredService<PluginEventDispatcher>());
            Assert.NotNull(provider.GetRequiredService<PluginBackgroundHost>());
            Assert.NotNull(provider.GetRequiredService<PluginHostServicesReader>());
            Assert.NotNull(provider.GetRequiredService<IPluginExecutionContextFactory>());
            Assert.Same(provider.GetRequiredService<PluginJobScheduler>(), Assert.Single(provider.GetServices<IScheduledTaskDescriptorSource>()));
            Assert.Empty(provider.GetRequiredService<IScheduledTaskRegistry>().List());
        }
        finally
        {
            database.Dispose();
            TestTemp.DeleteDatabase(databasePath);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // The plugin AI client needs an engine room to be built; nothing here asks it for anything.
    private sealed class NeverCalledEngineRoom : IAiEngineRoom
    {
        public Task<AiTextResult> WriteTextAsync(
            AiCaller caller, AiAbility ability, string prompt, string? grammar = null, AiRequestOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AiJsonResult<T>> ExtractJsonAsync<T>(
            AiCaller caller, AiAbility ability, string prompt, string grammar, AiRequestOptions? options = null, CancellationToken ct = default) where T : class =>
            throw new NotSupportedException();

        public Task<AiImageTextResult> ReadTextFromImageAsync(AiCaller caller, AiImageTextRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<AiAvailability> GetAvailabilityAsync(AiAbility ability, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
