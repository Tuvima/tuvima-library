using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A collection lists each root once: a movie, a TV show (not one row per episode), a comic run (not one row per issue)
/// and an album (not one row per track). Both <c>/collections/{id}/items</c> (Dashboard) and
/// <c>/api/v1/collections/{id}/items</c> (paired apps) go through the real route table, the real authorised display
/// projection (which lists episodes, issues and tracks, never their roots) and a real data store.
/// </summary>
public sealed class CollectionItemRootsTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_collection_roots_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _library = Guid.NewGuid();
    private readonly Guid _account = Guid.NewGuid();
    private readonly Guid _universe = Guid.NewGuid();
    private readonly Guid _seriesOne = Guid.NewGuid();
    private readonly Guid _seriesTwo = Guid.NewGuid();
    private readonly Guid _mixedUniverse = Guid.NewGuid();
    private readonly Guid _mixedSeriesOne = Guid.NewGuid();
    private readonly Guid _mixedSeriesTwo = Guid.NewGuid();
    private readonly Guid _episodeList = Guid.NewGuid();

    private Guid _movie;
    private Guid _show;
    private Guid _comic;
    private Guid _album;
    private Guid _otherMovie;
    private Guid _mixedMovie;
    private List<Guid> _episodes = [];
    private List<Guid> _issues = [];
    private List<Guid> _tracks = [];

    public CollectionItemRootsTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        CreateAccount();
        SeedLibrary();
    }

    [Fact]
    public async Task DashboardRoute_ListsOneRowPerRoot_InTheCollectionsOrder()
    {
        await using var app = await StartAsync();

        var rows = await GetArrayAsync(app, $"/collections/{_universe:D}/items?limit=50");

        Assert.Equal(
            [_movie, _show, _comic, _album],
            rows.Select(row => row!["work_id"]!.GetValue<Guid>()));
        Assert.Equal(
            ["Zulu Movie", "Mike Show", "Delta Comic", "Alpha Album"],
            rows.Select(row => row!["title"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ClientRoute_ListsOneRowPerRoot_InTheCollectionsOrder_AndPagesWithoutRepeats()
    {
        await using var app = await StartAsync();

        var all = await GetJsonAsync(app, $"/api/v1/collections/{_universe:D}/items?limit=10");
        var first = await GetJsonAsync(app, $"/api/v1/collections/{_universe:D}/items?limit=3");
        var second = await GetJsonAsync(app, $"/api/v1/collections/{_universe:D}/items?limit=3&cursor={first["next_cursor"]!.GetValue<string>()}");

        Assert.Equal(
            [_movie, _show, _comic, _album],
            all["items"]!.AsArray().Select(row => row!["work_id"]!.GetValue<Guid>()));
        Assert.Equal(4, all["total_count"]!.GetValue<int>());
        Assert.Equal(3, first["items"]!.AsArray().Count);
        Assert.True(first["has_more"]!.GetValue<bool>());
        Assert.Equal(_album, Assert.Single(second["items"]!.AsArray())!["work_id"]!.GetValue<Guid>());
        Assert.False(second["has_more"]!.GetValue<bool>());
    }

    [Fact]
    public async Task TheSummaryCount_AgreesWithTheRowsListedOnBothRoutes()
    {
        await using var app = await StartAsync();

        var clientSummary = await GetJsonAsync(app, $"/api/v1/collections/{_universe:D}");
        var dashboardSummary = await GetJsonAsync(app, $"/collections/{_universe:D}/summary");
        var clientRows = await GetJsonAsync(app, $"/api/v1/collections/{_universe:D}/items?limit=50");
        var dashboardRows = await GetArrayAsync(app, $"/collections/{_universe:D}/items?limit=50");

        Assert.Equal(4, clientSummary["item_count"]!.GetValue<int>());
        Assert.Equal(4, dashboardSummary["item_count"]!.GetValue<int>());
        Assert.Equal(4, clientRows["items"]!.AsArray().Count);
        Assert.Equal(4, dashboardRows.Count);
    }

    [Fact]
    public async Task ACollectionHoldingTheShowAndOneOfItsEpisodes_ListsTheShowOnce_OnBothRoutes()
    {
        await using var app = await StartAsync();

        var dashboard = await GetArrayAsync(app, $"/collections/{_mixedUniverse:D}/items?limit=50");
        var client = await GetJsonAsync(app, $"/api/v1/collections/{_mixedUniverse:D}/items?limit=50");
        var clientSummary = await GetJsonAsync(app, $"/api/v1/collections/{_mixedUniverse:D}");

        Assert.Equal([_show, _otherMovie, _mixedMovie], dashboard.Select(row => row!["work_id"]!.GetValue<Guid>()));
        Assert.Equal(
            [_show, _otherMovie, _mixedMovie],
            client["items"]!.AsArray().Select(row => row!["work_id"]!.GetValue<Guid>()));
        Assert.Equal(3, client["total_count"]!.GetValue<int>());
        Assert.Equal(3, clientSummary["item_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task AHandPickedListOfEpisodes_ListsTheShowOnce_AndPagesWithoutRepeats()
    {
        await using var app = await StartAsync();

        var dashboard = await GetArrayAsync(app, $"/collections/{_episodeList:D}/items?limit=50");
        var first = await GetJsonAsync(app, $"/api/v1/collections/{_episodeList:D}/items?limit=1");
        var second = await GetJsonAsync(app, $"/api/v1/collections/{_episodeList:D}/items?limit=1&cursor={first["next_cursor"]!.GetValue<string>()}");

        Assert.Equal([_show, _otherMovie], dashboard.Select(row => row!["work_id"]!.GetValue<Guid>()));
        Assert.Equal(_show, Assert.Single(first["items"]!.AsArray())!["work_id"]!.GetValue<Guid>());
        Assert.Equal(_otherMovie, Assert.Single(second["items"]!.AsArray())!["work_id"]!.GetValue<Guid>());
        Assert.Equal(2, first["total_count"]!.GetValue<int>());
        Assert.False(second["has_more"]!.GetValue<bool>());
    }

    // Seeds ------------------------------------------------------------------------------------------------------

    private void SeedLibrary()
    {
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, wikidata_qid, parent_collection_id)
                VALUES (@universe, 'Roots Universe', 'Universe', 'library', 'materialized', 1, 'Q7001', NULL),
                       (@seriesOne, 'Roots Series One', 'Series', 'library', 'query', 1, 'Q7002', @universe),
                       (@seriesTwo, 'Roots Series Two', 'Series', 'library', 'query', 1, NULL, @universe),
                       (@mixedUniverse, 'Mixed Universe', 'Universe', 'library', 'materialized', 1, 'Q7003', NULL),
                       (@mixedSeriesOne, 'Mixed Series One', 'Series', 'library', 'query', 1, 'Q7004', @mixedUniverse),
                       (@mixedSeriesTwo, 'Mixed Series Two', 'Series', 'library', 'query', 1, NULL, @mixedUniverse);
                INSERT INTO collections (id, display_name, collection_type, scope, resolution, is_enabled, membership_mode, owner_kind, audience)
                VALUES (@episodeList, 'Episodes list', 'Custom', 'library', 'materialized', 1, 'Manual', 'Library', 'Everyone');
                """,
                new
                {
                    universe = _universe,
                    seriesOne = _seriesOne,
                    seriesTwo = _seriesTwo,
                    mixedUniverse = _mixedUniverse,
                    mixedSeriesOne = _mixedSeriesOne,
                    mixedSeriesTwo = _mixedSeriesTwo,
                    episodeList = _episodeList,
                });
        }

        _movie = AddStandalone("Movies", "Zulu Movie", ".mkv", _seriesOne);
        _otherMovie = AddStandalone("Movies", "Other Movie", ".mkv", _mixedSeriesTwo);
        _mixedMovie = AddStandalone("Movies", "Mixed Movie", ".mkv", _mixedSeriesOne);

        // A show with two seasons of two episodes each: shows are parents, seasons are parents, episodes are children.
        _show = AddRoot("TV", "Mike Show");
        var seasonOne = AddWork("TV", "parent", _show, null, "Season One", null, null);
        var seasonTwo = AddWork("TV", "parent", _show, null, "Season Two", null, null);
        _episodes =
        [
            AddWork("TV", "child", seasonOne, _seriesOne, "Episode 1x1", ".mkv", 1),
            AddWork("TV", "child", seasonOne, _seriesOne, "Episode 1x2", ".mkv", 2),
            AddWork("TV", "child", seasonTwo, _seriesTwo, "Episode 2x1", ".mkv", 1),
            AddWork("TV", "child", seasonTwo, _seriesTwo, "Episode 2x2", ".mkv", 2),
        ];

        _comic = AddRoot("Comics", "Delta Comic");
        _issues =
        [
            AddWork("Comics", "child", _comic, _seriesTwo, "Issue 1", ".cbz", 1),
            AddWork("Comics", "child", _comic, _seriesTwo, "Issue 2", ".cbz", 2),
            AddWork("Comics", "child", _comic, _seriesTwo, "Issue 3", ".cbz", 3),
        ];

        _album = AddRoot("Music", "Alpha Album");
        _tracks =
        [
            AddWork("Music", "child", _album, _seriesTwo, "Track 1", ".flac", 1),
            AddWork("Music", "child", _album, _seriesTwo, "Track 2", ".flac", 2),
            AddWork("Music", "child", _album, _seriesTwo, "Track 3", ".flac", 3),
        ];

        // The Universe's own members, in the order the collection holds them, interleaving the leaves of each root.
        var members = new List<Guid> { _movie, _episodes[0], _issues[0], _tracks[0], _episodes[1], _issues[1], _tracks[1], _episodes[2], _issues[2], _tracks[2], _episodes[3] };
        AddItems(_universe, members);

        // A second collection that holds the show root itself and also one of its episodes.
        AddItems(_mixedUniverse, [_show, _episodes[1], _otherMovie, _mixedMovie]);
        AddItems(_episodeList, [_episodes[0], _episodes[1], _episodes[2], _otherMovie]);
    }

    private void AddItems(Guid collection, IReadOnlyList<Guid> works)
    {
        using var connection = _database.CreateConnection();
        for (var index = 0; index < works.Count; index++)
        {
            connection.Execute(
                "INSERT INTO collection_items (id, collection_id, work_id, sort_order) VALUES (@id, @collection, @work, @order);",
                new { id = Guid.NewGuid(), collection, work = works[index], order = index });
        }
    }

    private Guid AddRoot(string mediaType, string title) => AddWork(mediaType, "parent", null, null, title, null, null);

    private Guid AddStandalone(string mediaType, string title, string extension, Guid collection) =>
        AddWork(mediaType, "standalone", null, collection, title, extension, null);

    private Guid AddWork(string mediaType, string kind, Guid? parent, Guid? collection, string title, string? extension, int? ordinal)
    {
        var work = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, parent_work_id, curator_state, collection_id, ordinal)
            VALUES (@work, @mediaType, @kind, @parent, 'accepted', @collection, @ordinal);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@work, 'title', @title, CURRENT_TIMESTAMP),
                   (@work, 'content_rating', 'G', CURRENT_TIMESTAMP);
            """,
            new { work, mediaType, kind, parent, collection, title, ordinal });

        if (extension is not null)
        {
            var edition = Guid.NewGuid();
            var asset = Guid.NewGuid();
            connection.Execute(
                """
                INSERT INTO editions (id, work_id) VALUES (@edition, @work);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
                VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP, @library);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@asset, 'title', @title, CURRENT_TIMESTAMP);
                """,
                new
                {
                    work,
                    edition,
                    asset,
                    title,
                    hash = asset.ToString("N"),
                    path = $"C:/library/{asset:N}{extension}",
                    library = _library.ToString("D"),
                });
        }

        return work;
    }

    private void CreateAccount()
    {
        var accounts = new AccountRepository(_database);
        accounts.CreateAccountAsync(
            new Account
            {
                Id = _account,
                Email = $"{_account:N}@example.com",
                NormalizedEmail = $"{_account:N}@EXAMPLE.COM",
                IsEnabled = true,
                AuthorizationVersion = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
            new AccountProfileGrant
            {
                AccountId = _account,
                ProfileId = MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
                IsDefault = true,
                IsEnabled = true,
                AuthorizationVersion = 1,
                GrantedAt = DateTimeOffset.UtcNow,
            },
            new HashSet<AccountFeatureId> { AccountFeatureId.Read, AccountFeatureId.Watch, AccountFeatureId.Listen },
            new HashSet<Guid> { _library }).GetAwaiter().GetResult();
    }

    // Requests ---------------------------------------------------------------------------------------------------

    private static async Task<JsonNode> GetNodeAsync(TestApplication app, string path)
    {
        using var response = await app.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} returned {(int)response.StatusCode}: {body}");
        return JsonNode.Parse(body)!;
    }

    private static async Task<JsonObject> GetJsonAsync(TestApplication app, string path) =>
        (await GetNodeAsync(app, path)).AsObject();

    private static async Task<JsonArray> GetArrayAsync(TestApplication app, string path) =>
        (await GetNodeAsync(app, path)).AsArray();

    // Host -------------------------------------------------------------------------------------------------------

    private async Task<TestApplication> StartAsync()
    {
        var authority = new RequestAuthority(
            PrincipalKind.DelegatedUserClient,
            true,
            AccountId: _account,
            ActiveProfileId: MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            ApplicationId: Guid.NewGuid(),
            DeviceId: Guid.NewGuid(),
            AccountEnabled: true,
            GrantEnabled: true,
            ApplicationEnabled: true,
            AccountAuthorizationVersion: 1,
            GrantAuthorizationVersion: 1,
            ApplicationAuthorizationVersion: 1);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        RegisterUnusedRouteServices(builder.Services);
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddLogging();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(authority));
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new LibraryAccess(_library));
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowAuthorizationEvaluator>();
        builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
        builder.Services.AddSingleton<IAccountRepository, AccountRepository>();
        builder.Services.AddSingleton<IProfileRepository, ProfileRepository>();
        builder.Services.AddSingleton<ICollectionRepository, CollectionRepository>();
        builder.Services.AddSingleton<ISeriesManifestRepository, SeriesManifestRepository>();
        builder.Services.AddSingleton<IPersonRepository, PersonRepository>();
        builder.Services.AddSingleton<IArtworkPaletteService, ArtworkPaletteService>();
        builder.Services.AddSingleton<ICollectionMediaLookupReadService, CollectionMediaLookupReadService>();
        builder.Services.AddSingleton<CollectionCatalogReadService>();
        builder.Services.AddSingleton<DisplayWorkProjectionReader>();
        builder.Services.AddSingleton<DisplayCardBuilder>();
        builder.Services.AddSingleton<IRawDisplayProjectionReadService, LiveProjection>();
        builder.Services.AddScoped<AuthorizedDisplayProjectionReadService>();
        builder.Services.AddScoped<IDisplayProjectionReadService>(provider =>
            provider.GetRequiredService<AuthorizedDisplayProjectionReadService>());
        builder.Services.AddScoped<UniverseClientReadService>();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCollectionClientEndpoints();
        app.MapCollectionEndpoints();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new TestApplication(app, new Uri(addresses!.Addresses.Single()));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }

    /// <summary>Reads the projection fresh every time so a new row shows on the next request.</summary>
    private sealed class LiveProjection(DisplayWorkProjectionReader reader) : IRawDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => reader.LoadAsync(ct);
        public Task<IReadOnlyList<DisplayWorkRow>> LoadHomeWorksAsync(CancellationToken ct) => reader.LoadAsync(ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }

    // The Dashboard collection routes share one group with routes this test never calls. Their parameters only need to
    // be known services for the route table to build; they are never resolved. Registered before the real services so
    // those win for the routes under test.
    private static void RegisterUnusedRouteServices(IServiceCollection services)
    {
        var assemblies = new[]
        {
            typeof(CollectionEndpoints).Assembly,
            typeof(IProfileRepository).Assembly,
            typeof(MediaEngine.Application.Services.IIngestionBatchReadService).Assembly,
            typeof(MediaEngine.Contracts.Paging.PagedRequest).Assembly,
        };
        foreach (var type in assemblies.SelectMany(assembly => assembly.GetExportedTypes()))
        {
            if (type.IsGenericTypeDefinition || (type.IsClass && type.IsAbstract) || type.IsEnum || type.IsValueType)
            {
                continue;
            }

            services.AddSingleton(type, _ => null!);
        }
    }


    private sealed class FixedAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(authority);
    }

    private sealed class LibraryAccess(Guid libraryId) : IAccountAccessDecisionService
    {
        public ValueTask<AuthorizationDecision> EvaluateFeatureAsync(
            RequestAuthority authority, AccountFeatureId feature, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Allow());

        public ValueTask<AuthorizationDecision> EvaluateLibraryAsync(
            RequestAuthority authority, Guid requestedLibraryId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(requestedLibraryId == libraryId
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.MissingLibraryGrant));

        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(
            RequestAuthority authority, bool requireSurfaceUnlock, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));
    }

    private sealed class AllowAuthorizationEvaluator : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(
            RequestAuthority authority,
            AuthorizationRequirement requirement,
            ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Allow());
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "collection-roots-test")], Scheme.Name)),
                Scheme.Name)));
    }

    private sealed class TestApplication(WebApplication app, Uri address) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new() { BaseAddress = address };

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
