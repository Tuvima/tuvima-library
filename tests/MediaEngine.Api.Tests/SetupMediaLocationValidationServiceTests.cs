using MediaEngine.Api.Services.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class SetupMediaLocationValidationServiceTests
{
    [Fact]
    public void ReadOnlyOnlyLibraryPassesWithoutPrimaryDestination()
    {
        using var fixture = new Fixture(allowWrite: false);
        var archive = fixture.CreateFolder("Archive");
        fixture.SaveLibrary(
        [
            Source(archive, LibrarySourceManagementModes.ExistingLibrary),
        ]);

        var result = fixture.Service.Validate();

        Assert.True(result.Passed);
        Assert.Equal(1, result.Configured);
        Assert.Equal(1, result.Readable);
    }

    [Fact]
    public void ManagedLibraryRequiresWritablePrimaryDestination()
    {
        using var fixture = new Fixture(allowWrite: false);
        var managed = fixture.CreateFolder("Managed");
        var primaryId = Guid.NewGuid().ToString("D");
        fixture.SaveLibrary(
        [
            Source(
                managed,
                LibrarySourceManagementModes.ManagedByTuvima,
                primaryId),
        ], primaryId);

        var result = fixture.Service.Validate();

        Assert.False(result.Passed);
        Assert.Contains(managed, result.Detail, StringComparison.Ordinal);
        Assert.Contains("write", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MixedLibraryPassesWithOneWritableManagedPrimary()
    {
        using var fixture = new Fixture();
        var managed = fixture.CreateFolder("Managed");
        var archive = fixture.CreateFolder("Archive");
        var primaryId = Guid.NewGuid().ToString("D");
        fixture.SaveLibrary(
        [
            Source(
                managed,
                LibrarySourceManagementModes.ManagedByTuvima,
                primaryId),
            Source(archive, LibrarySourceManagementModes.ExistingLibrary),
        ], primaryId);

        var result = fixture.Service.Validate();

        Assert.True(result.Passed);
        Assert.Equal(2, result.Configured);
        Assert.Equal(2, result.Readable);
    }

    [Fact]
    public void ManagedLibraryWithoutAChosenDestinationGetsTheFirstManagedFolder()
    {
        using var fixture = new Fixture();
        var archive = fixture.CreateFolder("Archive");
        var first = fixture.CreateFolder("First");
        var second = fixture.CreateFolder("Second");
        fixture.SaveLibrary(
        [
            Source(archive, LibrarySourceManagementModes.ExistingLibrary),
            Source(first, LibrarySourceManagementModes.ManagedByTuvima),
            Source(second, LibrarySourceManagementModes.ManagedByTuvima),
        ]);

        var result = fixture.Service.Validate();

        Assert.True(result.Passed);
        var saved = fixture.Configuration.LoadLibraries().Libraries.Single();
        Assert.Equal(first, saved.PrimaryDestination?.Path);
        Assert.Contains("destination validation", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupValidationRetainsProtectedPathChecks()
    {
        using var fixture = new Fixture();
        var protectedFolder = fixture.CreateFolder(Path.Combine(".data", "internal"));
        fixture.SaveLibrary(
        [
            Source(protectedFolder, LibrarySourceManagementModes.ExistingLibrary),
        ]);

        var result = fixture.Service.Validate();

        Assert.False(result.Passed);
        Assert.Contains(protectedFolder, result.Detail, StringComparison.Ordinal);
        Assert.Contains("reserved", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetupEndpointUsesCanonicalMediaLocationValidatorAndExposesCredentialTest()
    {
        var endpoint = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "Endpoints", "SetupEndpoints.cs"));

        Assert.Contains("SetupMediaLocationValidationService mediaLocations", endpoint, StringComparison.Ordinal);
        Assert.Contains("var result = mediaLocations.Validate();", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Exists(source.Path)", endpoint, StringComparison.Ordinal);
        Assert.Contains("/providers/{name}/credentials/test", endpoint, StringComparison.Ordinal);
        Assert.Contains("credentials.TestAsync(name, request.Credentials, ct)", endpoint, StringComparison.Ordinal);
    }

    private static LibrarySourceConfig Source(
        string path,
        string managementMode,
        string? id = null) => new()
        {
            Id = id ?? Guid.NewGuid().ToString("D"),
            Path = path,
            ManagementMode = managementMode,
            SourceType = LibrarySourceTypes.LocalFolder,
            AccessMode = managementMode == LibrarySourceManagementModes.ManagedByTuvima
                ? LibrarySourceAccessModes.Writable
                : LibrarySourceAccessModes.ReadOnly,
        };

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(bool allowWrite = true)
        {
            Root = Path.Combine(Path.GetTempPath(), "tuvima-setup-media-tests", Guid.NewGuid().ToString("N"));
            AllowedRoot = Path.Combine(Root, "allowed");
            ViewRoot = Path.Combine(Root, "view");
            Directory.CreateDirectory(AllowedRoot);
            Directory.CreateDirectory(ViewRoot);
            Configuration = new ConfigurationDirectoryLoader(Path.Combine(Root, "config"));
            AllowWrite = allowWrite;
            SaveLibrary([]);
            var browser = new ServerFolderBrowserService(Configuration, NullLogger<ServerFolderBrowserService>.Instance);
            Service = new SetupMediaLocationValidationService(Configuration, browser);
        }

        public string Root { get; }
        public string AllowedRoot { get; }
        public string ViewRoot { get; }
        public bool AllowWrite { get; }
        public ConfigurationDirectoryLoader Configuration { get; }
        public SetupMediaLocationValidationService Service { get; }

        public string CreateFolder(string relativePath)
        {
            var path = Path.Combine(AllowedRoot, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public void SaveLibrary(List<LibrarySourceConfig> sources, string? primaryDestinationSourceId = null)
        {
            var libraries = sources.Count == 0
                ? []
                : new List<LibraryFolderConfig>
                {
                    new()
                    {
                        Id = Guid.NewGuid().ToString("D"),
                        Name = "Media",
                        Category = "Movies",
                        Kind = LibraryKinds.Catalogued,
                        Area = LibraryAreas.Watch,
                        Presentation = LibraryPresentations.Catalogue,
                        MetadataPolicy = LibraryMetadataPolicies.Enriched,
                        MediaTypes = ["Movies"],
                        Sources = sources,
                        PrimaryDestinationSourceId = primaryDestinationSourceId,
                    },
                };
            Configuration.SaveLibraries(new LibrariesConfiguration
            {
                StorageLocations =
                [
                    new ServerStorageLocationConfig
                    {
                        Id = "media",
                        Label = "Media",
                        Path = AllowedRoot,
                        AllowWrite = AllowWrite,
                    },
                    new ServerStorageLocationConfig
                    {
                        Id = "view",
                        Label = "View",
                        Path = ViewRoot,
                        AllowWrite = true,
                    },
                ],
                ViewStorage = new ViewStorageConfig
                {
                    StorageLocationId = "view",
                    RelativeRoot = ".",
                },
                Libraries = libraries,
            });
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Test cleanup is best effort; failures do not alter user data.
            }
        }
    }
}
