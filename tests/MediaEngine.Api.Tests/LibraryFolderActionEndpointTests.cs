using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Contracts.Library;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The library page's own actions over HTTP (Libraries spec D7, D8) on a real server with the real route table and authorization: Scan now
/// scans this library only, the folder title counts answer per folder, and Add files is an administrator's job that refuses before it copies.
/// </summary>
public sealed class LibraryFolderActionEndpointTests : IDisposable
{
    private const string PrincipalHeader = "X-Test-Principal";
    private const string MoviesId = "aaaaaaaa-0000-4000-8000-000000000001";
    private const string BooksId = "aaaaaaaa-0000-4000-8000-000000000002";

    private static readonly Guid Account = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<string, RequestAuthority> Principals = new Dictionary<string, RequestAuthority>
    {
        ["admin"] = new(PrincipalKind.Human, true, Account, Guid.NewGuid(), null, Guid.NewGuid(), null,
            AccountEnabled: true, ActiveProfileInUserHousehold: true, ActiveProfileIsServerAdministrator: true),
        ["member"] = new(PrincipalKind.Human, true, Account, Guid.NewGuid(), null, Guid.NewGuid(), null,
            AccountEnabled: true, ActiveProfileInUserHousehold: true),
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-library-actions-{Guid.NewGuid():N}");
    private readonly string _moviesA;
    private readonly string _moviesPaused;
    private readonly string _moviesOffline;
    private readonly string _books;
    private readonly string _incoming;
    private readonly ConfigurationDirectoryLoader _config;
    private readonly RecordingEngine _engine = new();

    public LibraryFolderActionEndpointTests()
    {
        var shared = Path.Combine(_root, "shared");
        _moviesA = Directory.CreateDirectory(Path.Combine(shared, "Movies A")).FullName;
        _moviesPaused = Directory.CreateDirectory(Path.Combine(shared, "Movies paused")).FullName;
        _moviesOffline = Path.Combine(shared, "Movies offline");
        _books = Directory.CreateDirectory(Path.Combine(shared, "Books")).FullName;
        _incoming = Directory.CreateDirectory(Path.Combine(shared, "incoming")).FullName;
        var configDir = Directory.CreateDirectory(Path.Combine(_root, "config")).FullName;
        var libraries = new LibrariesConfiguration
        {
            Libraries =
            [
                Library(MoviesId, "Movies", "movies", "Movies", LibraryAreas.Watch,
                [
                    Source("11111111-1111-4111-8111-000000000001", _moviesA, scan: true),
                    Source("11111111-1111-4111-8111-000000000002", _moviesPaused, scan: false),
                    Source("11111111-1111-4111-8111-000000000003", _moviesOffline, scan: true),
                ]),
                Library(BooksId, "Books", "books", "Books", LibraryAreas.Read, [Source("11111111-1111-4111-8111-000000000004", _books, scan: true)]),
            ],
            StorageLocations =
            [
                new ServerStorageLocationConfig { Id = "media", Label = "Media", Path = Path.Combine(_root, "own-media"), AllowWrite = true },
                new ServerStorageLocationConfig { Id = "shared", Label = "Shared", Path = shared, AllowWrite = true },
            ],
        };
        File.WriteAllText(Path.Combine(configDir, "libraries.json"), JsonSerializer.Serialize(libraries));
        _config = new ConfigurationDirectoryLoader(configDir);
    }

    public void Dispose()
    {
        _config.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a leftover temp folder is harmless */ }
    }

    [Fact]
    public async Task ScanNow_ScansOnlyThisLibrarysFolders_LeavingPausedOfflineAndOtherLibrariesAlone()
    {
        await using var app = await StartAsync();
        using var client = app.CreateClient("admin");

        using var response = await client.PostAsync($"/library/libraries/{MoviesId}/scan", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var scanned = Assert.Single(_engine.ScanBatches);
        Assert.Equal([_moviesA], scanned.Select(target => target.Path));
        Assert.DoesNotContain(scanned, target => target.Path == _books);
    }

    [Fact]
    public async Task ScanNow_ForALibraryWithNothingToScan_AnswersAPlainRefusal_AndStartsNothing()
    {
        Directory.Delete(_books);
        await using var app = await StartAsync();
        using var client = app.CreateClient("admin");

        using var response = await client.PostAsync($"/library/libraries/{BooksId}/scan", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_engine.ScanBatches);
    }

    [Theory]
    [InlineData("scan")]
    [InlineData("add-files")]
    public async Task TheLibraryActions_AreRefusedToAMemberWhoIsNotAnAdministrator(string action)
    {
        await using var app = await StartAsync();
        using var client = app.CreateClient("member");

        using var response = await client.PostAsync(
            $"/library/libraries/{MoviesId}/{action}", new StringContent("""{"paths":[]}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_engine.ScanBatches);
    }

    [Fact]
    public async Task ScanNow_ForALibraryThatDoesNotExist_AnswersNotFound()
    {
        await using var app = await StartAsync();
        using var client = app.CreateClient("admin");

        using var response = await client.PostAsync("/library/libraries/does-not-exist/scan", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FolderTitles_AnswersACountForEveryFolderOfThisLibraryByItsSourceId()
    {
        await using var app = await StartAsync();
        using var client = app.CreateClient("admin");

        var json = await client.GetStringAsync($"/library/libraries/{MoviesId}/folder-titles");

        var titles = JsonSerializer.Deserialize<LibraryFolderTitlesDto>(json)!;
        Assert.Equal(
            ["11111111-1111-4111-8111-000000000001", "11111111-1111-4111-8111-000000000002", "11111111-1111-4111-8111-000000000003"],
            titles.Counts.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(7, titles.Counts["11111111-1111-4111-8111-000000000001"]);
    }

    private static LibrarySourceConfig Source(string id, string path, bool scan) => new()
    {
        Id = id,
        Path = path,
        ManagementMode = LibrarySourceManagementModes.ExistingLibrary,
        AccessMode = LibrarySourceAccessModes.ReadOnly,
        ScanEnabled = scan,
    };

    private static LibraryFolderConfig Library(string id, string name, string kindKey, string mediaType, string area, List<LibrarySourceConfig> sources) => new()
    {
        Id = id,
        Name = name,
        Category = mediaType,
        Kind = LibraryKinds.Catalogued,
        KindKey = kindKey,
        Area = area,
        MediaTypes = [mediaType],
        Sources = sources,
    };

    private async Task<TestApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicies.Administrator, policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new EffectiveAdministratorRequirement(true)));
        });
        builder.Services.AddScoped<IAuthorizationHandler, EffectiveAdministratorHandler>();
        builder.Services.AddSingleton<IRequestAuthorityResolver, PrincipalHeaderResolver>();
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new AdministratorDecisions());
        builder.Services.AddSingleton<IConfigurationLoader>(_config);
        builder.Services.AddSingleton<IIngestionEngine>(_engine.AsEngine());
        builder.Services.AddSingleton(VaultRepository.Create());
        builder.Services.AddSingleton<ILibraryFolderTitleCountRepository>(new FixedTitleCounts(_moviesA, 7));
        builder.Services.AddSingleton(new ServerFolderBrowserService(_config, NullLogger<ServerFolderBrowserService>.Instance));
        builder.Services.AddSingleton<IMediaTypeExtensionCatalog>(new MediaTypeExtensionCatalog(_config));
        builder.Services.AddSingleton<LibraryAddFilesService>(sp => new LibraryAddFilesService(
            _config, sp.GetRequiredService<ServerFolderBrowserService>(), sp.GetRequiredService<IMediaTypeExtensionCatalog>(),
            sp.GetRequiredService<ILogger<LibraryAddFilesService>>()));

        var app = builder.Build();
        app.UseDeveloperExceptionPage();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapLibraryFolderActionEndpoints();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new TestApplication(app, new Uri(addresses!.Addresses.Single()));
    }

    private sealed class FixedTitleCounts(string path, int count) : ILibraryFolderTitleCountRepository
    {
        public Task<IReadOnlyDictionary<string, int>> CountByFolderAsync(IReadOnlyCollection<string> folderPaths, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(folderPaths.ToDictionary(folder => folder, folder => folder == path ? count : 0));
    }

    private sealed class PrincipalHeaderResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(Principals[context.Request.Headers[PrincipalHeader].ToString()]);
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "library-actions-test")], Scheme.Name)), Scheme.Name)));
    }

    private sealed class TestApplication(WebApplication app, Uri address) : IAsyncDisposable
    {
        public HttpClient CreateClient(string principal)
        {
            var client = new HttpClient { BaseAddress = address };
            client.DefaultRequestHeaders.Add(PrincipalHeader, principal);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>Stands in for the Vault list: no library is a Vault library.</summary>
    private class VaultRepository : DispatchProxy
    {
        public static IVaultLibraryRepository Create() => DispatchProxy.Create<IVaultLibraryRepository, VaultRepository>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IVaultLibraryRepository.GetIdsAsync)
                ? Task.FromResult<IReadOnlySet<string>>(new HashSet<string>())
                : throw new NotSupportedException(targetMethod?.Name);
    }

    /// <summary>Records the scans the Engine was asked for; nothing else of the Engine is used by these routes.</summary>
    public sealed class RecordingEngine
    {
        public List<IReadOnlyList<IngestionScanTarget>> ScanBatches { get; } = [];

        public IIngestionEngine AsEngine()
        {
            var proxy = DispatchProxy.Create<IIngestionEngine, EngineProxy>();
            ((EngineProxy)(object)proxy).Owner = this;
            return proxy;
        }
    }

    public class EngineProxy : DispatchProxy
    {
        internal RecordingEngine? Owner { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IIngestionEngine.ScanDirectories):
                    Owner!.ScanBatches.Add((IReadOnlyList<IngestionScanTarget>)args![0]!);
                    return Task.CompletedTask;
                case nameof(IIngestionEngine.ScanDirectory):
                    return Task.CompletedTask;
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }
}
