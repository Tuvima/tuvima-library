using System.Reflection;
using System.Text;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Api.Services.LocalAssets;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Plugins;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// How many libraries a kind holds is the kinds list's rule alone, whichever door a save comes through: Settings saving the whole
/// list, first-run setup saving it, and the single-library mutation. The real endpoints run over a real config folder; the services
/// that touch the file system beyond it or the data store are stand-ins that answer "nothing there".
/// </summary>
public sealed class LibrarySaveKindRulesEndpointTests : IDisposable
{
    private const string SettingsSave = "settings";
    private const string SetupSave = "setup";
    private const string Mutation = "mutation";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-save-kinds", Guid.NewGuid().ToString("N"));
    private readonly string _libraryRoot;
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly PluginCatalog _catalog;
    private readonly LibraryKindListService _kinds;
    private WebApplication? _app;
    private bool _asAdministrator;

    public LibrarySaveKindRulesEndpointTests()
    {
        _libraryRoot = Path.Combine(_root, "library");
        Directory.CreateDirectory(Path.Combine(_libraryRoot, ".data", "plugins"));
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(config, "providers"));
        var shipped = Path.Combine(FindRepoRoot(), "config");
        File.Copy(Path.Combine(shipped, "core.json"), Path.Combine(config, "core.json"));
        File.Copy(Path.Combine(shipped, "library_kinds.json"), Path.Combine(config, "library_kinds.json"));
        foreach (var provider in Directory.EnumerateFiles(Path.Combine(shipped, "providers"), "*.json"))
        {
            File.Copy(provider, Path.Combine(config, "providers", Path.GetFileName(provider)));
        }

        _loader = new ConfigurationDirectoryLoader(config);
        var core = _loader.LoadCore();
        core.LibraryRoot = _libraryRoot;
        _loader.SaveCore(core);
        _loader.SaveLibraries(new LibrariesConfiguration
        {
            StorageLocations = [new ServerStorageLocationConfig { Id = "media", Label = "Library data", Path = _libraryRoot, AllowWrite = true }],
            ViewStorage = new ViewStorageConfig { StorageLocationId = "media", RelativeRoot = "View" },
        });
        _catalog = new PluginCatalog([], new PluginSettingsService(_loader), _loader, AllowAllPluginApprovals.Instance, NullLogger<PluginCatalog>.Instance);
        _kinds = new LibraryKindListService(_loader, _catalog);
    }

    public void Dispose()
    {
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _loader.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds is left for the OS to clean up.
        }
    }

    [Fact]
    public async Task TheLibrariesList_GivesAnAdministratorEachLibrarysUnmatchedCount_AndAnyoneElseNone()
    {
        var baseline = _loader.LoadLibraries();
        var movies = Standard("movies", "Movies", LibraryAreas.Watch, "Movies");
        movies.Id = "dddddddd-0000-4000-8000-000000000001";
        baseline.Libraries = [movies];
        _loader.SaveLibraries(baseline);

        _asAdministrator = true;
        var asAdministrator = await ReadLibrariesAsync();
        _asAdministrator = false;
        var asSomeoneElse = await ReadLibrariesAsync();

        Assert.Equal(4, Assert.Single(asAdministrator.Libraries).UnmatchedCount);
        Assert.Equal(0, Assert.Single(asSomeoneElse.Libraries).UnmatchedCount);
    }

    [Theory]
    [InlineData(SettingsSave)]
    [InlineData(SetupSave)]
    [InlineData(Mutation)]
    public async Task TwoNamedOtherVideosLibrariesAreAccepted(string door)
    {
        var saved = await SaveAsync(door, [Other("Videos")], Other("Lectures"));

        Assert.Equal(200, saved.Status);
        Assert.Equal(["Videos", "Lectures"], _loader.LoadLibraries().Libraries.Select(library => library.Name).ToArray());
    }

    [Theory]
    [InlineData(SettingsSave)]
    [InlineData(SetupSave)]
    [InlineData(Mutation)]
    public async Task ASixthOtherVideosLibraryIsRefusedWithASentenceSayingHowManyAreAllowed(string door)
    {
        var five = Enumerable.Range(1, 5).Select(number => Other($"Collection {number}")).ToList();

        var saved = await SaveAsync(door, five, Other("Collection 6"));

        Assert.Equal(400, saved.Status);
        Assert.Contains("allows at most 5", saved.Text, StringComparison.Ordinal);
        Assert.Contains("other_videos", saved.Text, StringComparison.Ordinal);
        Assert.Equal(5, _loader.LoadLibraries().Libraries.Count);
    }

    [Theory]
    [InlineData(SettingsSave)]
    [InlineData(SetupSave)]
    [InlineData(Mutation)]
    public async Task ASecondOtherVideosLibraryWithTheSameNameIgnoringCaseIsRefused(string door)
    {
        var saved = await SaveAsync(door, [Other("Lectures")], Other("LECTURES"));

        Assert.Equal(400, saved.Status);
        Assert.Contains("already used", saved.Text, StringComparison.Ordinal);
        Assert.Single(_loader.LoadLibraries().Libraries);
    }

    [Theory]
    [InlineData(SettingsSave)]
    [InlineData(SetupSave)]
    [InlineData(Mutation)]
    public async Task AnOtherVideosLibrarySavedWithTheYoutubeLookupOnIsSaved_WithTheKindsValuesInTheFile(string door)
    {
        var lectures = Other("Lectures");
        lectures.YoutubeLookup = true;
        lectures.MetadataPolicy = LibraryMetadataPolicies.Enriched;

        var saved = await SaveAsync(door, [], lectures);

        Assert.Equal(200, saved.Status);
        var library = Assert.Single(_loader.LoadLibraries().Libraries);
        Assert.Equal("other_videos", library.KindKey);
        Assert.False(library.YoutubeLookup);
        Assert.Equal(LibraryMetadataPolicies.LocalOnly, library.MetadataPolicy);
    }

    [Theory]
    [InlineData(SettingsSave)]
    [InlineData(SetupSave)]
    [InlineData(Mutation)]
    public async Task AStaleOldVideosLibraryNeverBlocksASave_AndIsMadeConsistentByIt(string door)
    {
        var old = Other("Old videos");
        old.YoutubeLookup = true;

        var saved = await SaveAsync(door, [old], Other("Lectures"));

        Assert.Equal(200, saved.Status);
        Assert.All(_loader.LoadLibraries().Libraries, library => Assert.False(library.YoutubeLookup));
    }

    [Theory]
    [InlineData(SettingsSave)]
    [InlineData(SetupSave)]
    [InlineData(Mutation)]
    public async Task ASecondMoviesLibraryIsRefusedBecauseALookupKindHoldsOne(string door)
    {
        var saved = await SaveAsync(door, [Standard("movies", "Movies", LibraryAreas.Watch, "Movies")], Standard("movies", "Films", LibraryAreas.Watch, "Movies"));

        Assert.Equal(400, saved.Status);
        Assert.Contains("holds one library", saved.Text, StringComparison.Ordinal);
        Assert.Single(_loader.LoadLibraries().Libraries);
    }

    [Theory]
    [InlineData(SettingsSave)]
    [InlineData(SetupSave)]
    [InlineData(Mutation)]
    public async Task AFormatSharedWithAnotherKindDoesNotStopThatKindHavingItsOwnLibrary(string door)
    {
        AddPodcastsPlugin();
        var music = Standard("music", "Music", LibraryAreas.Listen, "Music");

        var saved = await SaveAsync(door, [music], Standard("podcasts", "Podcasts", LibraryAreas.Listen, "Music"));

        Assert.Equal(200, saved.Status);
        Assert.Equal(["music", "podcasts"], _loader.LoadLibraries().Libraries.Select(library => library.KindKey).ToArray());
    }

    [Theory]
    [InlineData(SettingsSave)]
    [InlineData(SetupSave)]
    [InlineData(Mutation)]
    public async Task APluginKindsLibraryIsRefusedWhileItsPluginIsSwitchedOff(string door)
    {
        AddPodcastsPlugin(enabled: false);

        var saved = await SaveAsync(door, [Standard("music", "Music", LibraryAreas.Listen, "Music")], Standard("podcasts", "Podcasts", LibraryAreas.Listen, "Music"));

        Assert.Equal(400, saved.Status);
        Assert.Contains("podcasts", saved.Text, StringComparison.Ordinal);
        Assert.Contains("is not a library kind", saved.Text, StringComparison.Ordinal);
        Assert.Single(_loader.LoadLibraries().Libraries);
    }

    // ---- Harness ----

    private static LibraryFolderConfig Other(string name) => Standard("other_videos", name, LibraryAreas.Watch, "Videos");

    private static LibraryFolderConfig Standard(string kindKey, string name, string area, string mediaType) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Name = name,
        Category = mediaType,
        Kind = LibraryKinds.Catalogued,
        KindKey = kindKey,
        Area = area,
        MediaTypes = [mediaType],
        // Videos libraries are always local only (see the libraries file check).
        // Videos, and the plain kinds (Other videos, and the Podcasts kind a plugin adds with no lookup), are local only.
        MetadataPolicy = mediaType == "Videos" || kindKey == "podcasts" ? LibraryMetadataPolicies.LocalOnly : LibraryMetadataPolicies.Enriched,
        // A plain kind never asks YouTube, and the file check says so when it is left on.
        YoutubeLookup = false,
    };

    private void AddPodcastsPlugin(bool enabled = true)
    {
        var directory = Path.Combine(_libraryRoot, ".data", "plugins", "json.podcasts");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), """
            {
              "id": "json.podcasts", "name": "Podcasts", "version": "1.0.0", "kind": "declarative",
              "declarative": { "library_kinds": [{
                "key": "podcasts", "area": "listen", "label": "Podcasts", "icon": "Podcasts", "format": "Music", "lookup": "none",
                "grouping": "folder_and_tags", "naming_template": "default", "misfit": "keep", "tooltip": "Episodes of shows you follow."
              }] }
            }
            """);
        _catalog.Reload();
        if (enabled)
        {
            _catalog.SetEnabled("json.podcasts", true);
        }
    }

    /// <summary>
    /// Saves <paramref name="existing"/> straight to the config folder, then sends <paramref name="added"/> through the chosen door:
    /// the PUT endpoints get the whole list, the mutation endpoint gets the one new library.
    /// </summary>
    private async Task<(int Status, string Text)> SaveAsync(string door, IReadOnlyList<LibraryFolderConfig> existing, LibraryFolderConfig added)
    {
        var baseline = _loader.LoadLibraries();
        baseline.Libraries = [.. existing];
        _loader.SaveLibraries(baseline);

        var everything = SettingsContractMapper.ToContract(new LibrariesConfiguration
        {
            StorageLocations = baseline.StorageLocations,
            ViewStorage = baseline.ViewStorage,
            Libraries = [.. existing, added],
        });
        return door switch
        {
            SettingsSave => await SendAsync("PUT", "/settings/libraries", ToUpdate(everything)),
            SetupSave => await SendAsync("PUT", "/setup/v1/libraries", ToUpdate(everything)),
            _ => await SendAsync("POST", "/settings/libraries/mutations", new LibraryMutationRequest { Library = everything.Libraries[^1] }),
        };
    }

    private async Task<LibrariesConfigurationDto> ReadLibrariesAsync()
    {
        var read = await SendAsync("GET", "/settings/libraries", new object());
        Assert.Equal(200, read.Status);
        return JsonSerializer.Deserialize<LibrariesConfigurationDto>(read.Text)!;
    }

    private static UpdateLibrariesRequest ToUpdate(LibrariesConfigurationDto dto) => new()
    {
        SchemaVersion = dto.SchemaVersion,
        StorageLocations = dto.StorageLocations,
        ViewStorage = dto.ViewStorage,
        Libraries = dto.Libraries,
        PersonalLibraryPolicy = dto.PersonalLibraryPolicy,
        FileHandling = dto.FileHandling,
    };

    private async Task<(int Status, string Text)> SendAsync(string method, string route, object body)
    {
        _app ??= Build();
        var endpoint = Assert.Single(((IEndpointRouteBuilder)_app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(), candidate =>
                candidate.RoutePattern.RawText == route
                && candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType());
        var responseBody = new MemoryStream();
        using var scope = _app.Services.CreateScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            Request = { Method = method, Path = route, ContentType = "application/json", ContentLength = bytes.Length, Body = new MemoryStream(bytes) },
            Response = { Body = responseBody },
        };
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());
        context.SetEndpoint(endpoint);

        await endpoint.RequestDelegate!(context);

        return (context.Response.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray()));
    }

    private WebApplication Build()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Host.UseServiceProviderFactory(new ServicesAreEverythingButBodiesFactory());
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddSingleton<IConfigurationLoader>(_loader);
        builder.Services.AddSingleton(_kinds);
        // No Vault libraries: the saves under test hold ordinary libraries only.
        builder.Services.AddSingleton<IVaultLibraryRepository>(new FixedVaultLibraries());
        builder.Services.AddSingleton(new ServerFolderBrowserService(_loader, NullLogger<ServerFolderBrowserService>.Instance));
        builder.Services.AddSingleton(sp => new FolderJobService(
            _loader,
            NothingThere.Create<IMediaOperationRepository>(),
            NothingThere.Create<IMediaOperationTracker>(),
            NothingThere.Create<IFolderOrganiseLogRepository>(),
            null!,
            null!,
            sp.GetRequiredService<ServerFolderBrowserService>(),
            NullLogger<FolderJobService>.Instance,
            sp.GetRequiredService<IVaultLibraryRepository>()));
        builder.Services.AddSingleton(new ImportFolderApplier(NothingThere.Create<IImportFolderService>(), NothingThere.Create<IIngestionEngine>()));
        builder.Services.AddSingleton(NothingThere.Create<IProfileRepository>());
        // Only used for the profiles a save would set up a personal space for, and there are none.
        builder.Services.AddSingleton(NothingThere.Create<IViewProfileRepository>());
        builder.Services.AddSingleton(new ViewStorageService(_loader, NothingThere.Create<IViewPersonalSpaceRepository>(), NothingThere.Create<IViewSharedLibraryRepository>()));
        // Only used to check a custom file naming template, which these libraries do not have.
        builder.Services.AddSingleton(NothingThere.Create<IFileOrganizer>());
        builder.Services.AddSingleton(new SetupSessionService(null!, null!, TimeProvider.System));
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthority(() => _asAdministrator));
        builder.Services.AddSingleton(sp => new MediaEngine.Api.Services.Unmatched.UnmatchedCountService(
            new UnmatchedCountsSpy(), sp.GetRequiredService<IVaultLibraryRepository>()));
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new AllowEverything());
        var app = builder.Build();
        app.MapSettingsEndpoints();
        app.MapSetupEndpoints();
        return app;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find repository root.");
    }

    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class FixedAuthority(Func<bool> asAdministrator) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) => ValueTask.FromResult(asAdministrator()
            ? new RequestAuthority(
                PrincipalKind.Human,
                true,
                AccountId: Guid.NewGuid(),
                ActiveProfileId: MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
                AccountEnabled: true,
                GrantEnabled: true,
                AccountIsAdministrator: true,
                GrantAdminEnabled: true)
            : new RequestAuthority(
            PrincipalKind.DelegatedUserClient,
            true,
            AccountId: Guid.NewGuid(),
            ActiveProfileId: MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            ApplicationId: Guid.NewGuid(),
            DeviceId: Guid.NewGuid(),
            AccountEnabled: true,
            GrantEnabled: true,
            ApplicationEnabled: true,
            AccountAuthorizationVersion: 1,
            GrantAuthorizationVersion: 1,
            ApplicationAuthorizationVersion: 1));
    }

    /// <summary>Two libraries with Unmatched files, as the data store would count them.</summary>
    private sealed class UnmatchedCountsSpy : IUnmatchedAssetRepository
    {
        public Task<IReadOnlyDictionary<string, int>> CountByLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int> { ["dddddddd-0000-4000-8000-000000000001"] = 4, ["dddddddd-0000-4000-8000-000000000002"] = 9 });

        public Task MarkUnmatchedAsync(Guid assetId, string libraryId, string reason, DateTimeOffset now, DateTimeOffset nextRetryAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MediaEngine.Domain.Models.UnmatchedAsset>> ListByAssetsAsync(IReadOnlyCollection<Guid> assetIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MediaEngine.Domain.Models.UnmatchedAsset?> GetAsync(Guid assetId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> ClearAsync(Guid assetId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> KeepAsIsAsync(Guid assetId, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> KeepAllAsIsAsync(string libraryId, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> LookUpAgainAsync(Guid assetId, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> MakeDueAsync(Guid assetId, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> MakeAllDueAsync(string? libraryId, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MediaEngine.Domain.Models.UnmatchedAsset>> ListDueAsync(DateTimeOffset now, int limit, string? libraryId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecordTriedAsync(Guid assetId, DateTimeOffset now, DateTimeOffset nextRetryAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MediaEngine.Domain.Models.UnmatchedAssetPage> ListByLibraryAsync(string libraryId, bool includeKept, int skip, int take, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class AllowEverything : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(RequestAuthority authority, AccountFeatureId feature, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(RequestAuthority authority, Guid libraryId, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(RequestAuthority authority, bool requireSurfaceUnlock, CancellationToken cancellationToken = default) => ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    /// <summary>A stand-in that answers every question with "nothing there": a finished task, an empty list or the type's default.</summary>
    public class NothingThere : DispatchProxy
    {
        public static T Create<T>() where T : class => Create<T, NothingThere>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returns = targetMethod!.ReturnType;
            if (returns == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returns == typeof(ValueTask))
            {
                return default(ValueTask);
            }

            if (returns.IsGenericType && returns.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var result = returns.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [Empty(result)]);
            }

            return Empty(returns);
        }

        private static object? Empty(Type type)
        {
            if (type == typeof(void))
            {
                return null;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() is var definition
                && (definition == typeof(IReadOnlyList<>) || definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyCollection<>)))
            {
                return Array.CreateInstance(type.GetGenericArguments()[0], 0);
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    /// <summary>
    /// The Settings routes are mapped as a whole, and building them needs to know which handler arguments come from the services and
    /// which from the request body. Everything that is not a request or response shape counts as a service here, so the routes
    /// under test can be built without registering the dozens of services the other Settings routes use; those services are only
    /// looked up if one of those routes is called, which these tests never do.
    /// </summary>
    private sealed class ServicesAreEverythingButBodiesFactory : IServiceProviderFactory<IServiceCollection>
    {
        public IServiceCollection CreateBuilder(IServiceCollection services) => services;

        public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder)
        {
            var inner = containerBuilder.BuildServiceProvider();
            return new Wrapper(inner);
        }
    }

    private sealed class Wrapper(ServiceProvider inner) : IServiceProvider, IDisposable, IAsyncDisposable
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IServiceProviderIsService)
            ? new AssumeServices(inner.GetRequiredService<IServiceProviderIsService>())
            : inner.GetService(serviceType);

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class AssumeServices(IServiceProviderIsService known) : IServiceProviderIsService
    {
        public bool IsService(Type serviceType) => known.IsService(serviceType) || LooksLikeAService(serviceType);

        private static bool LooksLikeAService(Type type)
        {
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type.IsValueType || type.IsArray)
            {
                return false;
            }

            var space = type.Namespace ?? string.Empty;
            return !(space.StartsWith("System", StringComparison.Ordinal)
                || space.StartsWith("MediaEngine.Contracts", StringComparison.Ordinal)
                || space.StartsWith("MediaEngine.Domain.Configuration", StringComparison.Ordinal));
        }
    }
}
