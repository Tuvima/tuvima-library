using System.Reflection;
using MediaEngine.Api.DependencyInjection;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion;
using MediaEngine.Ingestion.DependencyInjection;
using MediaEngine.Storage;
using MediaEngine.Storage.Configuration;
using MediaEngine.Storage.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Engine's own registration gives the organiser the durable filing list (Library setup D62), so a newly identified file is
/// recorded as waiting for the next run in the running Engine and not only in tests that wire it by hand.
/// </summary>
public sealed class BatchedFilingCompositionTests
{
    [Fact]
    public void TheOrganiserTheEngineRegisters_FilesThroughTheDurableListAndAskTheFilingHold()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_di_filing_{Guid.NewGuid():N}.db");
        var database = new DatabaseConnection(databasePath);
        try
        {
            DapperConfiguration.Configure();
            database.InitializeSchema();
            database.RunStartupChecks();
            var configuration = new ConfigurationManager();
            var configLoader = new ConfigurationDirectoryLoader(Path.Combine(FindRepoRoot(), "config"));
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<IConfigurationLoader>(configLoader);
            services.AddSingleton<IDatabaseConnection>(database);
            services.AddSingleton<IEventPublisher, NoEvents>();
            services.AddTuvimaStorage();
            services.AddMediaEngineIngestion(configuration, configLoader);
            services.AddTuvimaIntelligence();
            using var provider = services.BuildServiceProvider();

            var organiser = provider.GetRequiredService<AutoOrganizeService>();

            Assert.Same(provider.GetRequiredService<MediaFileWriteIntentRepository>(), Field(organiser, "_filingQueue"));
            Assert.Same(provider.GetRequiredService<IFilingQueue>(), Field(organiser, "_filingQueue"));
            // The Engine answers the hold from the first check of a newly added folder; NoFilingHold is only for hosts with no checks.
            Assert.IsType<FolderFirstCheckFilingHold>(Field(organiser, "_filingHold"));
            Assert.Same(provider.GetRequiredService<IFilingHold>(), Field(organiser, "_filingHold"));
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

    private sealed class NoEvents : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull => Task.CompletedTask;
    }

    private static object? Field(AutoOrganizeService organiser, string name) =>
        typeof(AutoOrganizeService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(organiser);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
