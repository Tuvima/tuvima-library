using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Dapper;
using MediaEngine.Api.DependencyInjection;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The playlist actions of the Listen playlists spec (WP1) through the real collection routes and a real data store: add with
/// "added / already there" counts, move by position, Duplicate, the per-profile pin, an ordered create (Save queue as
/// playlist) and the description, cover choice, count and length that the playlist list carries. Only the caller's identity and
/// permissions are faked.
/// </summary>
public sealed class PlaylistActionEndpointTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_playlist_endpoints_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _library = Guid.NewGuid();
    private readonly Guid _account = Guid.NewGuid();

    public PlaylistActionEndpointTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_database);
    }

    [Fact]
    public async Task AddingSeveralSongsInOneCall_SaysHowManyWereAddedAndHowManyWereAlreadyThere()
    {
        var one = SeedSong("One");
        var two = SeedSong("Two");
        var three = SeedSong("Three");
        var playlist = SeedPlaylist("Road trip", one);
        await using var app = await StartAsync();

        // The same song listed twice in the call counts once.
        using var response = await app.Client.PostAsJsonAsync(
            $"/collections/{playlist:D}/items",
            new CollectionItemAddRequest { WorkIds = [one, two, three, two] });

        await AssertStatusAsync(HttpStatusCode.OK, response);
        var result = await response.Content.ReadFromJsonAsync<CollectionItemsAddedResponse>();
        Assert.Equal(new CollectionItemsAddedResponse(2, 1), result);
        Assert.Equal([one, two, three], WorkIdsInOrder(playlist));
    }

    [Fact]
    public async Task AddingOneSongThatIsAlreadyThere_AddsNothingAndSaysAlreadyThere()
    {
        var one = SeedSong("One");
        var playlist = SeedPlaylist("Road trip", one);
        await using var app = await StartAsync();

        using var response = await app.Client.PostAsJsonAsync(
            $"/collections/{playlist:D}/items",
            new CollectionItemAddRequest { WorkId = one });

        await AssertStatusAsync(HttpStatusCode.OK, response);
        Assert.Equal(new CollectionItemsAddedResponse(0, 1), await response.Content.ReadFromJsonAsync<CollectionItemsAddedResponse>());
        Assert.Equal(1, Count("SELECT COUNT(*) FROM collection_items WHERE collection_id = @playlist", new { playlist }));
    }

    [Fact]
    public async Task AddingNoSongs_IsRefused()
    {
        var playlist = SeedPlaylist("Not empty", SeedSong("A"));
        await using var app = await StartAsync();

        using var response = await app.Client.PostAsJsonAsync($"/collections/{playlist:D}/items", new CollectionItemAddRequest());

        await AssertStatusAsync(HttpStatusCode.BadRequest, response);
    }

    [Fact]
    public async Task MovingAnItemToAPosition_ToTheTopAndToTheBottom_ReordersThePlaylist()
    {
        var songs = Enumerable.Range(1, 5).Select(number => SeedSong($"Song {number}")).ToArray();
        var playlist = SeedPlaylist("Order", songs);
        var items = ItemIds(playlist);
        await using var app = await StartAsync();

        using var toPosition = await app.Client.PutAsJsonAsync(
            $"/collections/{playlist:D}/items/{items[4]:D}/move", new CollectionItemMoveRequest { Position = 2 });
        await AssertStatusAsync(HttpStatusCode.OK, toPosition);
        Assert.Equal([songs[0], songs[4], songs[1], songs[2], songs[3]], WorkIdsInOrder(playlist));

        using var toTop = await app.Client.PutAsJsonAsync(
            $"/collections/{playlist:D}/items/{items[3]:D}/move", new CollectionItemMoveRequest { ToTop = true });
        await AssertStatusAsync(HttpStatusCode.OK, toTop);
        Assert.Equal(songs[3], WorkIdsInOrder(playlist)[0]);

        using var toBottom = await app.Client.PutAsJsonAsync(
            $"/collections/{playlist:D}/items/{items[3]:D}/move", new CollectionItemMoveRequest { ToBottom = true });
        await AssertStatusAsync(HttpStatusCode.OK, toBottom);
        Assert.Equal(songs[3], WorkIdsInOrder(playlist)[^1]);
        Assert.Equal(Enumerable.Range(1, 5), SortOrders(playlist));
    }

    [Fact]
    public async Task MovingAnItem_NeedsExactlyOneWayToSayWhere_AndAKnownItem()
    {
        var playlist = SeedPlaylist("Order", SeedSong("A"), SeedSong("B"));
        var item = ItemIds(playlist)[0];
        await using var app = await StartAsync();

        using var none = await app.Client.PutAsJsonAsync($"/collections/{playlist:D}/items/{item:D}/move", new CollectionItemMoveRequest());
        using var two = await app.Client.PutAsJsonAsync(
            $"/collections/{playlist:D}/items/{item:D}/move", new CollectionItemMoveRequest { Position = 1, ToTop = true });
        using var zero = await app.Client.PutAsJsonAsync(
            $"/collections/{playlist:D}/items/{item:D}/move", new CollectionItemMoveRequest { Position = 0 });
        using var unknown = await app.Client.PutAsJsonAsync(
            $"/collections/{playlist:D}/items/{Guid.NewGuid():D}/move", new CollectionItemMoveRequest { ToTop = true });

        await AssertStatusAsync(HttpStatusCode.BadRequest, none);
        await AssertStatusAsync(HttpStatusCode.BadRequest, two);
        await AssertStatusAsync(HttpStatusCode.BadRequest, zero);
        await AssertStatusAsync(HttpStatusCode.NotFound, unknown);
    }

    [Fact]
    public async Task TheOldSendEveryItemReorder_IsGone()
    {
        var playlist = SeedPlaylist("Order", SeedSong("A"));
        await using var app = await StartAsync();

        using var response = await app.Client.PutAsJsonAsync($"/collections/{playlist:D}/items/reorder", new { item_ids = ItemIds(playlist) });

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.BadRequest,
            $"Expected the old reorder route to be gone but got {response.StatusCode}.");
    }

    [Fact]
    public async Task DuplicatingAPlaylist_CopiesTheSongsInOrderUnderANewName_AndLeavesTheOriginalAlone()
    {
        var songs = Enumerable.Range(1, 6).Select(number => SeedSong($"Song {number}")).ToArray();
        var original = SeedPlaylist("Road trip", songs);
        var items = ItemIds(original);
        SetDescription(original, "Long drives");
        await using var app = await StartAsync();

        // Put the original in a non-insertion order first, so a copy made in insertion order would be wrong.
        using var move = await app.Client.PutAsJsonAsync(
            $"/collections/{original:D}/items/{items[5]:D}/move", new CollectionItemMoveRequest { Position = 1 });
        await AssertStatusAsync(HttpStatusCode.OK, move);
        var originalOrder = WorkIdsInOrder(original);

        using var response = await app.Client.PostAsJsonAsync($"/collections/{original:D}/duplicate", new CollectionDuplicateRequest());

        await AssertStatusAsync(HttpStatusCode.Created, response);
        var copy = await response.Content.ReadFromJsonAsync<ManagedCollectionDto>();
        Assert.NotNull(copy);
        Assert.NotEqual(original, copy.Id);
        Assert.Equal("Road trip copy", copy.Name);
        Assert.Equal("Long drives", copy.Description);
        Assert.Equal(6, copy.ItemCount);
        Assert.Equal(originalOrder, WorkIdsInOrder(copy.Id));
        Assert.Equal(Enumerable.Range(1, 6), SortOrders(copy.Id));
        Assert.Equal(originalOrder, WorkIdsInOrder(original));
        Assert.Equal("Playlist", Scalar<string>("SELECT collection_type FROM collections WHERE id = @id", new { id = copy.Id }));
    }

    [Fact]
    public async Task DuplicatingAPlaylistWithAGivenName_UsesThatName()
    {
        var original = SeedPlaylist("Road trip", SeedSong("A"));
        await using var app = await StartAsync();

        using var response = await app.Client.PostAsJsonAsync(
            $"/collections/{original:D}/duplicate", new CollectionDuplicateRequest { Name = "Road trip 2" });

        await AssertStatusAsync(HttpStatusCode.Created, response);
        Assert.Equal("Road trip 2", (await response.Content.ReadFromJsonAsync<ManagedCollectionDto>())!.Name);
    }

    [Fact]
    public async Task SavingAnOrderedListAsAPlaylist_KeepsTheSongsInTheOrderSent()
    {
        var songs = Enumerable.Range(1, 12).Select(number => SeedSong($"Queue {number}")).ToArray();
        var queue = songs.Reverse().ToArray();
        await using var app = await StartAsync();

        using var response = await app.Client.PostAsJsonAsync("/collections", new CollectionCreateRequest
        {
            Name = "From the queue",
            CollectionType = "Playlist",
            OwnerKind = "Library",
            Audience = "Everyone",
            Visibility = "shared",
            WorkIds = queue.ToList(),
        });

        await AssertStatusAsync(HttpStatusCode.Created, response);
        var created = (await response.Content.ReadFromJsonAsync<CollectionCreatedResponse>())!;
        Assert.Equal(queue, WorkIdsInOrder(created.id));
        Assert.Equal(Enumerable.Range(1, 12), SortOrders(created.id));
    }

    [Fact]
    public async Task APlaylistsDescriptionAndCover_ComeBackOnTheListAndFollowTheChoice()
    {
        var playlist = SeedPlaylist("Road trip", SeedSong("A"));
        await using var app = await StartAsync();

        using var update = await app.Client.PutAsJsonAsync(
            $"/collections/{playlist:D}",
            new CollectionUpdateRequest { Description = "Long drives", RuleDefinition = new CollectionRuleDefinitionDto() });
        await AssertStatusAsync(HttpStatusCode.OK, update);

        var mosaic = await ReadPlaylistAsync(app, playlist);
        Assert.Equal("Long drives", mosaic.Description);
        Assert.Equal("mosaic", mosaic.CoverMode);
        Assert.Null(mosaic.CoverArtworkUrl);

        var repository = new CollectionRepository(_database);
        await repository.UpdateCollectionArtworkAsync(playlist, "poster", "C:/data/covers/road-trip.jpg", "image/jpeg");
        var picture = await ReadPlaylistAsync(app, playlist);
        Assert.Equal("picture", picture.CoverMode);
        Assert.NotNull(picture.CoverArtworkUrl);

        await repository.UpdateCollectionArtworkAsync(playlist, "poster", null, null);
        Assert.Equal("mosaic", (await ReadPlaylistAsync(app, playlist)).CoverMode);
    }

    [Fact]
    public async Task PinningAPlaylist_ShowsOnTheListForTheActiveProfile_AndUnpinningClearsIt()
    {
        var playlist = SeedPlaylist("Road trip", SeedSong("A"));
        await using var app = await StartAsync();
        Assert.False((await ReadPlaylistAsync(app, playlist)).IsPinned);

        using var pin = await app.Client.PutAsJsonAsync($"/collections/{playlist:D}/pinned", new CollectionPinRequest(true));
        await AssertStatusAsync(HttpStatusCode.OK, pin);
        Assert.True((await ReadPlaylistAsync(app, playlist)).IsPinned);
        Assert.Equal(1, Count(
            "SELECT COUNT(*) FROM profile_pinned_playlists WHERE collection_id = @playlist AND profile_id = @profile",
            new { playlist, profile = MediaEngine.Domain.Aggregates.Profile.SeedProfileId }));

        using var unpin = await app.Client.PutAsJsonAsync($"/collections/{playlist:D}/pinned", new CollectionPinRequest(false));
        await AssertStatusAsync(HttpStatusCode.OK, unpin);
        Assert.False((await ReadPlaylistAsync(app, playlist)).IsPinned);
    }

    [Fact]
    public async Task ThePlaylistList_ShowsTheCountAndTheTotalLengthOfTheSongsWithAKnownLength()
    {
        var timed = SeedSong("Timed", durationSeconds: 125.5);
        var other = SeedSong("Other", durationSeconds: 60);
        var unknown = SeedSong("No length");
        var playlist = SeedPlaylist("Lengths", timed, other, unknown);
        var plain = SeedPlaylist("No lengths", SeedSong("Plain"));
        await using var app = await StartAsync();

        var withLengths = await ReadPlaylistAsync(app, playlist);
        var without = await ReadPlaylistAsync(app, plain);

        Assert.Equal(3, withLengths.ItemCount);
        Assert.Equal(185.5, withLengths.TotalDurationSeconds!.Value, 3);
        Assert.Equal(1, without.ItemCount);
        Assert.Null(without.TotalDurationSeconds);
    }

    private static async Task<ManagedCollectionDto> ReadPlaylistAsync(TestApplication app, Guid playlist)
    {
        var all = await app.Client.GetFromJsonAsync<List<ManagedCollectionDto>>("/collections/managed");
        return all!.Single(collection => collection.Id == playlist);
    }

    private static async Task AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        // Show the Engine's answer when the status is not the expected one, so a failure explains itself.
        Assert.True(response.StatusCode == expected, $"Expected {expected} but got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private Guid SeedSong(string title, double? durationSeconds = null)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@workId, 'Movies', 'standalone', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, library_id)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, @library);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@assetId, 'title', @title, CURRENT_TIMESTAMP);
            """, new
        {
            workId,
            editionId,
            assetId,
            hash = Guid.NewGuid().ToString("N"),
            path = $"C:/library/{assetId:N}.mkv",
            library = _library.ToString("D"),
            title,
        });
        if (durationSeconds is { } seconds)
        {
            connection.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@assetId, 'duration_seconds', @value, CURRENT_TIMESTAMP)",
                new { assetId, value = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        return workId;
    }

    private Guid SeedPlaylist(string name, params Guid[] workIds)
    {
        var collection = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, membership_mode, primary_area, owner_kind, audience)
            VALUES (@collection, @name, 'Playlist', 'library', 'materialized', 'Manual', 'Listen', 'Library', 'Everyone');
            """, new { collection, name });
        var order = 0;
        foreach (var workId in workIds)
        {
            connection.Execute(
                "INSERT INTO collection_items (id, collection_id, work_id, sort_order) VALUES (@id, @collection, @workId, @order);",
                new { id = Guid.NewGuid(), collection, workId, order = ++order });
        }

        return collection;
    }

    private void SetDescription(Guid collection, string description)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("UPDATE collections SET description = @description WHERE id = @collection", new { collection, description });
    }

    private List<Guid> ItemIds(Guid collection)
    {
        using var connection = _database.CreateConnection();
        return connection.Query<Guid>(
            "SELECT id FROM collection_items WHERE collection_id = @collection ORDER BY sort_order", new { collection }).ToList();
    }

    private List<Guid> WorkIdsInOrder(Guid collection)
    {
        using var connection = _database.CreateConnection();
        return connection.Query<Guid>(
            "SELECT work_id FROM collection_items WHERE collection_id = @collection ORDER BY sort_order", new { collection }).ToList();
    }

    private List<int> SortOrders(Guid collection)
    {
        using var connection = _database.CreateConnection();
        return connection.Query<int>(
            "SELECT sort_order FROM collection_items WHERE collection_id = @collection ORDER BY sort_order", new { collection }).ToList();
    }

    private int Count(string sql, object? parameters = null) => Scalar<int>(sql, parameters);

    private T Scalar<T>(string sql, object? parameters = null)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<T>(sql, parameters)!;
    }

    private async Task<TestApplication> StartAsync()
    {
        var authority = new RequestAuthority(
            PrincipalKind.ServiceApplication,
            true,
            AccountId: _account,
            ActiveProfileId: MediaEngine.Domain.Aggregates.Profile.SeedProfileId,
            ApplicationId: Guid.NewGuid(),
            DeviceId: Guid.NewGuid(),
            AccountEnabled: true,
            ActiveProfileInUserHousehold: true,
            ApplicationEnabled: true,
            AccountAuthorizationVersion: 1,
            ProfileAuthorizationVersion: 1,
            ApplicationAuthorizationVersion: 1);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton<IAuthorizationHandler, AllowAdministratorOrApplicationHandler>();
        builder.Services.AddLogging();
        builder.Services.AddHttpContextAccessor();
        // The shipped service modules register far more than these routes use; Production builds them lazily, so only
        // what a request needs is resolved.
        builder.Services.AddHttpClient();
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddTuvimaStorage();
        builder.Services.AddTuvimaDisplay();
        builder.Services.AddSingleton<IVaultLibraryRepository>(new FixedVaultLibraries());
        builder.Services.AddSingleton<IHouseholdLibraryAccessRepository, TestLibraryVisibility.NoHouseholdLibraries>();
        builder.Services.AddSingleton<LibraryVisibilityRule>();
        // Who is calling, and what they may do, is faked; the playlist rules under test are real.
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(authority));
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new LibraryAccess(_library));
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowAuthorizationEvaluator>();
        builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
        builder.Services.AddSingleton<IArtworkPaletteService, MediaEngine.Providers.Services.ArtworkPaletteService>();
        // The shipped modules also start background workers; only the web server is needed here.
        foreach (var worker in builder.Services
                     .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                         && descriptor.ImplementationType?.Name != "GenericWebHostService")
                     .ToList())
        {
            builder.Services.Remove(worker);
        }

        var app = builder.Build();
        // Report an unexpected error in the answer so a failing test says why.
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsync(ex.ToString());
            }
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCollectionEndpoints();
        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        return new TestApplication(app, new Uri(addresses!.Addresses.Single()));
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(_databasePath);
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }

    /// <summary>The caller is allowed to write collections; what is under test is the playlist behaviour, not the permission check.</summary>
    private sealed class AllowAdministratorOrApplicationHandler : AuthorizationHandler<AdministratorOrApplicationRequirement>
    {
        protected override Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            AdministratorOrApplicationRequirement requirement)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
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
                : AuthorizationDecision.Deny(AuthorizationDenialReason.LibraryNotVisible));

        public ValueTask<AuthorizationDecision> EvaluateAdministratorAsync(
            RequestAuthority authority, bool requireSurfaceUnlock, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(AuthorizationDecision.Allow());
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
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "playlist-action-test")], Scheme.Name)),
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
