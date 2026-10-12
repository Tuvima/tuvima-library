using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

public sealed class ServerFolderBrowserServiceTests
{
    [Fact]
    public void ProspectivePersonalRootValidatesWithoutCreatingFolders()
    {
        using var fixture = new Fixture();
        var result = fixture.Service.Validate(new ValidateServerFolderRequest
        {
            StorageLocationId = "media",
            RelativePath = "NewView/profiles",
            SelectionMode = ServerFolderSelectionModes.PersonalSpaceManaged,
        });
        Assert.True(result.CanSelect);
        Assert.False(result.Exists);
        Assert.True(result.HasWrite);
        Assert.False(Directory.Exists(Path.Combine(fixture.AllowedRoot, "NewView")));
        Assert.False(fixture.Service.Validate(new ValidateServerFolderRequest
        {
            StorageLocationId = "media",
            RelativePath = "NewView",
            SelectionMode = ServerFolderSelectionModes.ManagedLibrary,
        }).CanSelect);
    }

    [Fact]
    public void ValidationReportsWhetherTheFolderAlreadyHoldsFiles()
    {
        using var fixture = new Fixture();
        var empty = Directory.CreateDirectory(Path.Combine(fixture.AllowedRoot, "Empty"));
        var full = Directory.CreateDirectory(Path.Combine(fixture.AllowedRoot, "Full", "Season 1"));
        File.WriteAllText(Path.Combine(full.FullName, "episode.mkv"), "x");

        bool Contains(string relative) => fixture.Service.Validate(new ValidateServerFolderRequest
        {
            StorageLocationId = "media",
            RelativePath = relative,
            SelectionMode = ServerFolderSelectionModes.ExistingLibrary,
        }).ContainsFiles;

        Assert.False(Contains("Empty"));
        Assert.True(Contains("Full"));
    }

    [Fact]
    public void BrowseListsOnlyDirectoriesBeneathApprovedRoot()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.AllowedRoot, "Movies"));
        Directory.CreateDirectory(Path.Combine(fixture.AllowedRoot, "Books"));
        File.WriteAllText(Path.Combine(fixture.AllowedRoot, "secret.txt"), "not a directory");

        var result = fixture.Service.Browse(new BrowseServerFoldersRequest
        {
            StorageLocationId = "media",
        });

        Assert.Equal(["Books", "Movies"], result.Directories.Select(item => item.Name));
        Assert.DoesNotContain(result.Directories, item => item.Name == "secret.txt");
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    public void BrowseRejectsTraversalOutsideApprovedRoot(string relativePath)
    {
        using var fixture = new Fixture();

        var exception = Assert.Throws<ServerFolderAccessException>(() => fixture.Service.Browse(
            new BrowseServerFoldersRequest
            {
                StorageLocationId = "media",
                RelativePath = relativePath,
            }));

        Assert.Contains("cannot leave", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManualPathOutsideConfiguredRootsIsFoundThroughTheMachineRoot()
    {
        using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);

        var result = fixture.Service.Validate(new ValidateServerFolderRequest
        {
            ManualPath = outside,
            SelectionMode = ServerFolderSelectionModes.ExistingLibrary,
        });

        Assert.True(result.CanSelect);
        Assert.True(result.HasRead);
    }

    [Fact]
    public void MachineRootsAreListedAlongsideConfiguredLocations()
    {
        using var fixture = new Fixture();

        var roots = fixture.Service.GetStorageLocations();

        Assert.Contains(roots, root => root.Id == "media");
        Assert.Contains(roots, root => root.Id.StartsWith("drive-", StringComparison.Ordinal) || root.Id == "server-root");
    }

    [Fact]
    public void TuvimasOwnConfigurationFolderCannotBeChosenFromAWholeDrive()
    {
        using var fixture = new Fixture();

        var result = fixture.Service.Validate(new ValidateServerFolderRequest
        {
            ManualPath = Path.Combine(fixture.Root, "config"),
            SelectionMode = ServerFolderSelectionModes.ExistingLibrary,
        });

        Assert.False(result.CanSelect);
        Assert.Contains(result.Issues, issue => issue.Code == "protected_path");
    }

    [Fact]
    public void ReadOnlyConfiguredLocationStaysReadOnlyWhenReachedThroughTheWholeDrive()
    {
        using var fixture = new Fixture(allowWrite: false);
        var archive = Directory.CreateDirectory(Path.Combine(fixture.AllowedRoot, "Archive"));

        var result = fixture.Service.Validate(new ValidateServerFolderRequest
        {
            ManualPath = archive.FullName,
            SelectionMode = ServerFolderSelectionModes.ManagedLibrary,
        });

        Assert.False(result.CanSelect);
        Assert.False(result.HasWrite);
    }

    [Fact]
    public void WholeDriveCannotBeManagedAndSystemFoldersCannotBeUsed()
    {
        using var fixture = new Fixture();
        var driveRoot = Path.GetPathRoot(fixture.Root)!;

        var managedDrive = fixture.Service.Validate(new ValidateServerFolderRequest
        {
            ManualPath = driveRoot,
            SelectionMode = ServerFolderSelectionModes.ManagedLibrary,
        });
        Assert.False(managedDrive.CanSelect);
        Assert.Contains(managedDrive.Issues, issue => issue.Code == "drive_root_managed");

        var system = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            : "/etc";
        var systemResult = fixture.Service.Validate(new ValidateServerFolderRequest
        {
            ManualPath = system,
            SelectionMode = ServerFolderSelectionModes.ExistingLibrary,
        });
        Assert.False(systemResult.CanSelect);
        Assert.Contains(systemResult.Issues, issue => issue.Code == "system_path");
    }

    [Fact]
    public void ManagedSelectionRequiresStorageLocationWriteApproval()
    {
        using var fixture = new Fixture(allowWrite: false);
        Directory.CreateDirectory(Path.Combine(fixture.AllowedRoot, "Movies"));

        var result = fixture.Service.Validate(new ValidateServerFolderRequest
        {
            StorageLocationId = "media",
            RelativePath = "Movies",
            SelectionMode = ServerFolderSelectionModes.ManagedLibrary,
        });

        Assert.False(result.CanSelect);
        Assert.True(result.HasRead);
        Assert.False(result.HasWrite);
        Assert.Contains(result.Issues, issue => issue.Code == "storage_location_read_only");
        Assert.Contains(result.Issues, issue => issue.Code == "write_required");
    }

    [Fact]
    public void ExistingLibraryCanUseApprovedReadOnlyStorageLocation()
    {
        using var fixture = new Fixture(allowWrite: false);
        Directory.CreateDirectory(Path.Combine(fixture.AllowedRoot, "Archive"));

        var result = fixture.Service.Validate(new ValidateServerFolderRequest
        {
            StorageLocationId = "media",
            RelativePath = "Archive",
            SelectionMode = ServerFolderSelectionModes.ExistingLibrary,
        });

        Assert.True(result.CanSelect);
        Assert.True(result.HasRead);
        Assert.False(result.HasWrite);
    }

    [Fact]
    public void ValidationBlocksExactAndNestedConfiguredSources()
    {
        using var fixture = new Fixture();
        var configured = Path.Combine(fixture.AllowedRoot, "Configured");
        var nested = Path.Combine(configured, "Nested");
        Directory.CreateDirectory(nested);
        fixture.AddConfiguredSource(configured);

        var exact = fixture.Service.Validate(new ValidateServerFolderRequest
        {
            StorageLocationId = "media",
            RelativePath = "Configured",
            SelectionMode = ServerFolderSelectionModes.ExistingLibrary,
        });
        var child = fixture.Service.Validate(new ValidateServerFolderRequest
        {
            StorageLocationId = "media",
            RelativePath = Path.Combine("Configured", "Nested"),
            SelectionMode = ServerFolderSelectionModes.ExistingLibrary,
        });

        Assert.Contains(exact.Issues, issue => issue.Code == "already_configured");
        Assert.Contains(exact.Issues, issue => issue.LibraryName == "Movies");
        Assert.Contains(child.Issues, issue => issue.Code == "inside_configured_source");
    }

    [Fact]
    public void RoutesAreAdministratorOnlyAndRegistered()
    {
        var root = FindRepoRoot();
        var endpoint = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Api", "Endpoints", "ServerFolderEndpoints.cs"));
        var routes = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Api", "DependencyInjection", "ApiEndpointRouteBuilderExtensions.cs"));

        Assert.Contains("/settings/server-folders", endpoint, StringComparison.Ordinal);
        Assert.Contains(".RequireAdmin()", endpoint, StringComparison.Ordinal);
        Assert.Contains("group.MapGet(\"/roots\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("group.MapPost(\"/browse\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("group.MapPost(\"/validate\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("app.MapServerFolderEndpoints();", routes, StringComparison.Ordinal);
    }

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
            Root = Path.Combine(Path.GetTempPath(), "tuvima-server-folder-tests", Guid.NewGuid().ToString("N"));
            AllowedRoot = Path.Combine(Root, "allowed");
            ViewRoot = Path.Combine(Root, "view");
            Directory.CreateDirectory(AllowedRoot);
            Directory.CreateDirectory(ViewRoot);
            Configuration = new ConfigurationDirectoryLoader(Path.Combine(Root, "config"));
            SaveConfiguration([], allowWrite);
            Service = new ServerFolderBrowserService(Configuration, NullLogger<ServerFolderBrowserService>.Instance);
        }

        public string Root { get; }
        public string AllowedRoot { get; }
        public string ViewRoot { get; }
        public ConfigurationDirectoryLoader Configuration { get; }
        public ServerFolderBrowserService Service { get; }

        public void AddConfiguredSource(string path)
        {
            SaveConfiguration(
            [
                new LibraryFolderConfig
                {
                    Id = Guid.NewGuid().ToString("D"),
                    Name = "Movies",
                    Category = "Movies",
                    Kind = LibraryKinds.Catalogued,
                    Area = LibraryAreas.Watch,
                    Presentation = LibraryPresentations.Catalogue,
                    MetadataPolicy = LibraryMetadataPolicies.Enriched,
                    MediaTypes = ["Movies"],
                    Sources =
                    [
                        new LibrarySourceConfig
                        {
                            Id = Guid.NewGuid().ToString("D"),
                            Path = path,
                            Role = LibrarySourceRoles.Secondary,
                            ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
                            AccessMode = LibrarySourceAccessModes.ReadOnly,
                        },
                    ],
                },
            ],
            allowWrite: true);
        }

        private void SaveConfiguration(List<LibraryFolderConfig> libraries, bool allowWrite)
        {
            Configuration.SaveLibraries(new LibrariesConfiguration
            {
                SchemaVersion = "6.0",
                StorageLocations =
                [
                    new ServerStorageLocationConfig
                    {
                        Id = "media",
                        Label = "Media",
                        Path = AllowedRoot,
                        AllowWrite = allowWrite,
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
                // Test cleanup is best effort; failures do not alter application or user data.
            }
        }
    }
}
