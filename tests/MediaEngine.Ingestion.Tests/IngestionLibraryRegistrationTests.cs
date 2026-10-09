using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Ingestion.DependencyInjection;
using MediaEngine.Ingestion.Models;
using MediaEngine.Ingestion.Pipeline;
using MediaEngine.Ingestion.Tests.Helpers;
using MediaEngine.Processors;
using MediaEngine.Processors.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MediaEngine.Ingestion.Tests;

public sealed class IngestionLibraryRegistrationTests
{
    [Fact]
    public void IngestionProject_IsInternalLibraryWithoutStandaloneHostFiles()
    {
        string projectPath = GetRepoFilePath(@"src/MediaEngine.Ingestion/MediaEngine.Ingestion.csproj");
        string project = File.ReadAllText(projectPath);

        Assert.Contains("Sdk=\"Microsoft.NET.Sdk\"", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.NET.Sdk.Worker", project, StringComparison.Ordinal);
        Assert.False(File.Exists(GetRepoFilePath(@"src/MediaEngine.Ingestion/Program.cs")));
        Assert.False(File.Exists(GetRepoFilePath(@"src/MediaEngine.Ingestion/appsettings.json")));
    }

    [Fact]
    public async Task AddMediaEngineIngestion_RegistersCorePipelineServicesForEngineHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IVideoMetadataExtractor, StubVideoMetadataExtractor>();

        services.AddMediaEngineIngestion(
            new ConfigurationBuilder().Build(),
            new StubConfigurationLoader(),
            options =>
            {
                options.ConfigureOptions = false;
                options.CreateConfiguredDirectories = false;
            });

        Assert.Contains(services, d => d.ServiceType == typeof(IIngestionEngine));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
        Assert.Contains(services, d => d.ServiceType == typeof(IFileWatcher));
        Assert.Contains(services, d => d.ServiceType == typeof(IBackgroundWorker));
        Assert.Contains(services, d => d.ServiceType == typeof(IProcessorRouter));
        Assert.Contains(services, d => d.ServiceType == typeof(HashDedupeStageDependencies));
        Assert.Contains(services, d => d.ServiceType == typeof(ScoreIdentifyStageDependencies));
        Assert.Contains(services, d => d.ServiceType == typeof(OrganizeStageDependencies));
        Assert.Contains(services, d => d.ServiceType == typeof(WriteBackStageDependencies));
        Assert.Contains(services, d => d.ServiceType == typeof(IdentityJobStageDependencies));

        await using ServiceProvider provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IFileWatcher>());
        Assert.NotNull(provider.GetRequiredService<IBackgroundWorker>());
        Assert.NotNull(provider.GetRequiredService<IProcessorRouter>());
    }

    [Fact]
    public void AddMediaEngineIngestion_CreatesConfiguredSourcePathsWithoutNestedCategoryScaffolding()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"tuvima_ingestion_dirs_{Guid.NewGuid():N}");
        string booksPath = Path.Combine(tempRoot, "watch", "books");
        string moviesPath = Path.Combine(tempRoot, "watch", "movies");
        string libraryRoot = Path.Combine(tempRoot, "library");

        try
        {
            var services = new ServiceCollection();
            services.AddLogging();

            var loader = new StubConfigurationLoader
            {
                Core = new CoreConfiguration
                {
                    LibraryRoot = libraryRoot,
                },
                Libraries = new LibrariesConfiguration
                {
                    Libraries =
                    [
                        new LibraryFolderConfig
                        {
                            Id = Guid.NewGuid().ToString(),
                            Name = "Books",
                            Category = "Books",
                            Kind = LibraryKinds.Catalogued,
                            Area = LibraryAreas.Read,
                            Presentation = LibraryPresentations.Catalogue,
                            MetadataPolicy = LibraryMetadataPolicies.Enriched,
                            MediaTypes = ["Books", "Audiobooks"],
                            Sources = [ManagedSource(booksPath)],
                        },
                        new LibraryFolderConfig
                        {
                            Id = Guid.NewGuid().ToString(),
                            Name = "Movies",
                            Category = "Movies",
                            Kind = LibraryKinds.Catalogued,
                            Area = LibraryAreas.Watch,
                            Presentation = LibraryPresentations.Catalogue,
                            MetadataPolicy = LibraryMetadataPolicies.Enriched,
                            MediaTypes = ["Movies"],
                            Sources = [ManagedSource(moviesPath)],
                        },
                    ],
                },
            };

            services.AddMediaEngineIngestion(
                new ConfigurationBuilder().Build(),
                loader,
                options =>
                {
                    options.ConfigureOptions = false;
                    options.RegisterHostedService = false;
                });

            Assert.True(Directory.Exists(booksPath));
            Assert.True(Directory.Exists(moviesPath));
            Assert.True(Directory.Exists(Path.Combine(libraryRoot, ".data", "staging")));

            Assert.False(Directory.Exists(Path.Combine(booksPath, "Books")));
            Assert.False(Directory.Exists(Path.Combine(booksPath, "Movies")));
            Assert.False(Directory.Exists(Path.Combine(moviesPath, "Books")));
            Assert.False(Directory.Exists(Path.Combine(moviesPath, "Movies")));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void AddMediaEngineIngestion_WatchesCatalogueButNotPersonalViewSources()
    {
        string cataloguePath = Path.Combine(Path.GetTempPath(), "tuvima-catalogue");
        string personalPath = Path.Combine(Path.GetTempPath(), "tuvima-personal");
        var loader = new StubConfigurationLoader
        {
            Libraries = new LibrariesConfiguration
            {
                Libraries =
                [
                    new LibraryFolderConfig
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = "Books",
                        Kind = LibraryKinds.Catalogued,
                        Area = LibraryAreas.Read,
                        Presentation = LibraryPresentations.Catalogue,
                        MetadataPolicy = LibraryMetadataPolicies.Enriched,
                        Sources = [ManagedSource(cataloguePath)],
                    },
                    new LibraryFolderConfig
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = "Personal archive",
                        Kind = LibraryKinds.Personal,
                        Area = LibraryAreas.View,
                        Presentation = LibraryPresentations.MixedGallery,
                        MetadataPolicy = LibraryMetadataPolicies.LocalOnly,
                        Sources = [ManagedSource(personalPath)],
                    },
                ],
            },
        };
        var services = new ServiceCollection();

        services.AddMediaEngineIngestion(
            new ConfigurationBuilder().Build(),
            loader,
            options =>
            {
                options.CreateConfiguredDirectories = false;
                options.RegisterHostedService = false;
            });

        using ServiceProvider provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<IngestionOptions>>().Value;

        Assert.Contains(cataloguePath, options.EffectiveWatchDirectories, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(personalPath, options.EffectiveWatchDirectories, StringComparer.OrdinalIgnoreCase);
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));

    private static LibrarySourceConfig ManagedSource(string path) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Path = path,
        Role = LibrarySourceRoles.Secondary,
        ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
        AccessMode = LibrarySourceAccessModes.Writable,
    };
}
