using System.Reflection;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Per-library limits (Plugin architecture spec D6): by default a plugin works on every library; once an administrator chooses
/// libraries it works only on those, a Vault library can never be chosen, and a limited detector is not asked about items elsewhere.
/// </summary>
public sealed class PluginLibraryLimitTests : IDisposable
{
    private const string Movies = "11111111-1111-4111-8111-111111111111";
    private const string Music = "22222222-2222-4222-8222-222222222222";
    private const string Private = "33333333-3333-4333-8333-333333333333";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-plugin-library-limits-{Guid.NewGuid():N}");
    private readonly DatabaseConnection _database;
    private readonly PluginLibraryLimitRepository _repository;
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly List<IDisposable> _disposables = [];

    public PluginLibraryLimitTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_root);
        _database = new DatabaseConnection(Path.Combine(_root, "tuvima.db"));
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _repository = new PluginLibraryLimitRepository(_database);
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(
            Path.Combine(config, "libraries.json"),
            $$"""
            {
              "storage_locations": [{ "id": "media", "label": "Library data", "path": "{{_root.Replace('\\', '/')}}/library", "allow_write": true }],
              "view_storage": { "storage_location_id": "media", "relative_root": "View" },
              "libraries": [
                { "id": "{{Movies}}", "name": "Movies", "category": "Movies" },
                { "id": "{{Music}}", "name": "Music", "category": "Music" },
                { "id": "{{Private}}", "name": "Private", "category": "Books" }
              ]
            }
            """);
        _loader = new ConfigurationDirectoryLoader(config);
    }

    private PluginLibraryLimitService Service(params string[] vaultIds) =>
        new(_repository, _loader, new FixedVaultLibraries(vaultIds));

    [Fact]
    public async Task WithNoLimit_ThePluginMayWorkOnAnyLibrary_EvenOneWithNoLibraryYet()
    {
        var service = Service();

        Assert.True(await service.IsAllowedAsync("tuvima.skip-markers", Movies));
        Assert.True(await service.IsAllowedAsync("tuvima.skip-markers", null));
        Assert.False((await service.GetAsync("tuvima.skip-markers")).Limited);
    }

    [Fact]
    public async Task WithALimit_ThePluginMayWorkOnlyOnTheChosenLibraries()
    {
        var service = Service();

        Assert.Null(await service.SetAsync("tuvima.skip-markers", [Movies]));

        Assert.True(await service.IsAllowedAsync("tuvima.skip-markers", Movies));
        Assert.False(await service.IsAllowedAsync("tuvima.skip-markers", Music));
        Assert.False(await service.IsAllowedAsync("tuvima.skip-markers", null));
        Assert.True(await service.IsAllowedAsync("another.plugin", Music));
    }

    [Fact]
    public async Task ChoosingNoLibraries_RemovesTheLimit()
    {
        var service = Service();
        await service.SetAsync("tuvima.skip-markers", [Movies]);

        await service.SetAsync("tuvima.skip-markers", []);

        Assert.True(await service.IsAllowedAsync("tuvima.skip-markers", Music));
        Assert.False((await service.GetAsync("tuvima.skip-markers")).Limited);
    }

    [Fact]
    public async Task AVaultLibrary_CannotBeChosen_AndIsNeverListed_AndTheAnswerIsTheSameAsForAnUnknownLibrary()
    {
        var service = Service(Private);

        var refusedVault = await service.SetAsync("tuvima.skip-markers", [Movies, Private]);
        var refusedUnknown = await service.SetAsync("tuvima.skip-markers", [Movies, "not-a-library"]);

        Assert.NotNull(refusedVault);
        Assert.Equal(refusedUnknown, refusedVault);
        Assert.False((await service.GetAsync("tuvima.skip-markers")).Limited);
        var listed = (await service.GetAsync("tuvima.skip-markers")).Libraries.Select(library => library.Name);
        Assert.Equal(["Movies", "Music"], listed);
    }

    [Fact]
    public async Task TheChoiceListMarksWhichLibrariesAreChosen()
    {
        var service = Service();
        await service.SetAsync("tuvima.skip-markers", [Music]);

        var response = await service.GetAsync("tuvima.skip-markers");

        Assert.True(response.Limited);
        Assert.Equal([("Movies", false), ("Music", true), ("Private", false)], response.Libraries.Select(library => (library.Name, library.Selected)));
    }

    [Fact]
    public async Task ALimitedDetector_IsNotAskedAboutItemsInOtherLibraries()
    {
        var plugin = new HostServicesTestPlugin(permissions: [PluginPermissionIds.MediaRead]);
        var detector = plugin.AddDetector();
        var host = new HostServicesHarness(null, plugin);
        _disposables.Add(host);
        host.Enable();
        var service = Service();
        await service.SetAsync(plugin.Manifest.Id, [Movies]);
        var asset = new MediaAsset { Id = Guid.NewGuid(), FilePathRoot = Path.Combine(_root, "clip.mkv"), LibraryId = Music };
        var detection = new PluginSegmentDetectionService(
            host.Catalog, new NoSegments(), AssetsReturning(asset), host.Contexts, NullLogger<PluginSegmentDetectionService>.Instance, libraryLimits: service);

        await detection.DetectAsync(asset.Id, plugin.Manifest.Id);
        Assert.Equal(0, detector.Analyzed);

        asset.LibraryId = Movies;
        await detection.DetectAsync(asset.Id, plugin.Manifest.Id);
        Assert.Equal(1, detector.Analyzed);
    }

    [Fact]
    public async Task APluginWithNoLimit_StillWorksOnEveryLibrary()
    {
        var plugin = new HostServicesTestPlugin(permissions: [PluginPermissionIds.MediaRead]);
        var detector = plugin.AddDetector();
        var host = new HostServicesHarness(null, plugin);
        _disposables.Add(host);
        host.Enable();
        var asset = new MediaAsset { Id = Guid.NewGuid(), FilePathRoot = Path.Combine(_root, "clip.mkv"), LibraryId = Music };
        var detection = new PluginSegmentDetectionService(
            host.Catalog, new NoSegments(), AssetsReturning(asset), host.Contexts, NullLogger<PluginSegmentDetectionService>.Instance, libraryLimits: Service());

        await detection.DetectAsync(asset.Id, plugin.Manifest.Id);

        Assert.Equal(1, detector.Analyzed);
    }

    [Fact]
    public async Task ForgettingAPlugin_RemovesItsLimit()
    {
        var service = Service();
        await service.SetAsync("tuvima.skip-markers", [Movies]);

        await service.ForgetAsync("tuvima.skip-markers");

        Assert.True(await service.IsAllowedAsync("tuvima.skip-markers", Music));
    }

    private static IMediaAssetRepository AssetsReturning(MediaAsset asset)
    {
        var proxy = DispatchProxy.Create<IMediaAssetRepository, AssetLookup>();
        ((AssetLookup)(object)proxy).Asset = asset;
        return proxy;
    }

    /// <summary>Answers the one lookup the detection service makes and refuses everything else, so a test never reaches data it did not set up.</summary>
    public class AssetLookup : DispatchProxy
    {
        public MediaAsset? Asset { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IMediaAssetRepository.FindByIdAsync)
                ? Task.FromResult(Asset)
                : throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class NoSegments : IPlaybackSegmentRepository
    {
        public Task<IReadOnlyList<PlaybackSegment>> ListByAssetAsync(Guid assetId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PlaybackSegment>>([]);

        public Task<PlaybackSegment?> FindByIdAsync(Guid segmentId, CancellationToken ct = default) => Task.FromResult<PlaybackSegment?>(null);
        public Task UpsertBatchAsync(Guid assetId, IReadOnlyList<PlaybackSegment> segments, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateAsync(PlaybackSegment segment, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid segmentId, CancellationToken ct = default) => Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _loader.Dispose();
        _database.Dispose();
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* temp data cleanup is best effort */ }
        }
    }
}
