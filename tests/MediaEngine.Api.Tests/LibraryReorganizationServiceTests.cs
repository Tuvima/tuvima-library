using MediaEngine.Api.Services.Libraries;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Domain.Configuration;
using MediaEngine.Ingestion;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class LibraryReorganizationServiceTests
{
    [Fact]
    public void AsIsFolder_BlocksReorganizationWithoutTouchingFiles()
    {
        using var fixture = new Fixture();
        var original = fixture.Write("owned.txt", "owned content");
        var destination = Path.Combine(fixture.LibraryRoot, "renamed.txt");
        var configuration = fixture.Configuration.LoadLibraries();
        configuration.Libraries[0].Sources[0].ManagementMode = LibrarySourceManagementModes.ExistingLibrary;
        fixture.Configuration.SaveLibraries(configuration);

        var plan = fixture.Service.CreatePlan(fixture.LibraryId, fixture.Request(original, destination));

        Assert.NotNull(plan);
        Assert.False(plan.CanExecute);
        Assert.Equal("owned content", File.ReadAllText(original));
        Assert.False(File.Exists(destination));
    }
    [Fact]
    public void DryRunDoesNotMutateAndExactFingerprintExecutesOnce()
    {
        using var fixture = new Fixture();
        var current = fixture.Write("incoming/item.txt", "original");
        var proposed = Path.Combine(fixture.LibraryRoot, "organized", "item.txt");

        var plan = fixture.Service.CreatePlan(fixture.LibraryId, fixture.Request(current, proposed));

        Assert.NotNull(plan);
        Assert.True(plan.CanExecute);
        Assert.True(File.Exists(current));
        Assert.False(File.Exists(proposed));
        Assert.Throws<InvalidOperationException>(() => fixture.Service.Execute(
            fixture.LibraryId,
            new ExecuteReorganizationPlanRequest(plan.PlanId, "stale-fingerprint")));
        Assert.True(File.Exists(current));

        var execution = fixture.Service.Execute(
            fixture.LibraryId,
            new ExecuteReorganizationPlanRequest(plan.PlanId, plan.Fingerprint));

        Assert.NotNull(execution);
        Assert.Equal(1, execution.Succeeded);
        Assert.Equal("original", File.ReadAllText(proposed));
        Assert.Null(fixture.Service.Execute(
            fixture.LibraryId,
            new ExecuteReorganizationPlanRequest(plan.PlanId, plan.Fingerprint)));
    }

    [Fact]
    public void ExecuteReportsBlockedWhenSourcePolicyChangesAfterPreview()
    {
        using var fixture = new Fixture();
        var current = fixture.Write("item.txt", "keep");
        var proposed = Path.Combine(fixture.LibraryRoot, "renamed.txt");
        var plan = fixture.Service.CreatePlan(fixture.LibraryId, fixture.Request(current, proposed));
        Assert.NotNull(plan);
        Assert.True(plan.CanExecute);

        var libraries = fixture.Configuration.LoadLibraries();
        var source = Assert.Single(Assert.Single(libraries.Libraries).Sources);
        var changedRoot = Path.Combine(fixture.LibraryRoot, "changed-source-root");
        Directory.CreateDirectory(changedRoot);
        source.Path = changedRoot;
        fixture.Configuration.SaveLibraries(libraries);

        var execution = fixture.Service.Execute(
            fixture.LibraryId,
            new ExecuteReorganizationPlanRequest(plan.PlanId, plan.Fingerprint));

        Assert.NotNull(execution);
        Assert.Equal(1, execution.Blocked);
        Assert.Contains("changed after preview", Assert.Single(execution.Items).Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(current));
        Assert.False(File.Exists(proposed));
    }

    [Fact]
    public void ReorganizationRoutesAreAdministratorOnlyAndRegistered()
    {
        var root = FindRepoRoot();
        var endpoint = File.ReadAllText(Path.Combine(
            root, "src", "MediaEngine.Api", "Endpoints", "LibraryReorganizationEndpoints.cs"));
        var routes = File.ReadAllText(Path.Combine(
            root, "src", "MediaEngine.Api", "DependencyInjection", "ApiEndpointRouteBuilderExtensions.cs"));

        Assert.Contains("/settings/libraries/{libraryId:guid}/reorganization", endpoint, StringComparison.Ordinal);
        Assert.Contains(".RequireAdmin()", endpoint, StringComparison.Ordinal);
        Assert.Contains("group.MapPost(\"/plan\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("group.MapPost(\"/execute\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("app.MapLibraryReorganizationEndpoints();", routes, StringComparison.Ordinal);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "tuvima-reorganization-api", Guid.NewGuid().ToString("N"));

        public Fixture()
        {
            LibraryId = Guid.NewGuid();
            SourceId = Guid.NewGuid();
            LibraryRoot = Path.Combine(_root, "library");
            Directory.CreateDirectory(LibraryRoot);
            Configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
            Configuration.SaveLibraries(new LibrariesConfiguration
            {
                StorageLocations =
                [
                    new ServerStorageLocationConfig
                    {
                        Id = "library",
                        Label = "Library",
                        Path = LibraryRoot,
                        AllowWrite = true,
                    },
                ],
                ViewStorage = new ViewStorageConfig
                {
                    StorageLocationId = "library",
                    RelativeRoot = "View",
                },
                Libraries =
                [
                    new LibraryFolderConfig
                    {
                        Id = LibraryId.ToString("D"),
                        Name = "Managed Movies library",
                        OrganizationPolicy = new LibraryOrganizationPolicyConfig(),
                        Category = "Movies",
                        Kind = LibraryKinds.Catalogued,
                        Area = LibraryAreas.Watch,
                        Presentation = LibraryPresentations.Catalogue,
                        MetadataPolicy = LibraryMetadataPolicies.Enriched,
                        PrimaryDestinationSourceId = SourceId.ToString("D"),
                        Sources =
                        [
                            new LibrarySourceConfig
                            {
                                Id = SourceId.ToString("D"),
                                Path = LibraryRoot,
                                ManagementMode = LibrarySourceManagementModes.ManagedByTuvima,
                                SourceType = LibrarySourceTypes.LocalFolder,
                                AccessMode = LibrarySourceAccessModes.Writable,
                            },
                        ],
                    },
                ],
            });

            var gate = new SourceMutationPolicyGate();
            var fileSystem = new SystemReorganizationFileSystem();
            Service = new LibraryReorganizationService(
                Configuration,
                new ReorganizationPlanner(gate),
                new ReorganizationExecutor(gate, fileSystem),
                fileSystem);
        }

        public Guid LibraryId { get; }
        public Guid SourceId { get; }
        public string LibraryRoot { get; }
        public ConfigurationDirectoryLoader Configuration { get; }
        public LibraryReorganizationService Service { get; }

        public CreateReorganizationPlanRequest Request(string current, string proposed) => new(
            [new ReorganizationCandidateDto(SourceId, SourceId, current, proposed)]);

        public string Write(string relativePath, string content)
        {
            var path = Path.Combine(LibraryRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            Configuration.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
