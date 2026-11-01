using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Dapper;
using MediaEngine.Api.DependencyInjection;
using MediaEngine.Api.Endpoints;
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
/// Everyone makes their own, administrators push to everyone (Collections and lists D19). Runs the real collection routes and the
/// real route policy against a real data store; only who is calling (and whether the Dashboard has them unlocked as an
/// administrator) is faked.
/// </summary>
public sealed class CollectionAuthoringByProfileTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_collection_authoring_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _library = Guid.NewGuid();
    private readonly Guid _account = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _someoneElse = Guid.NewGuid();
    private readonly FixedVaultLibraries _vault = new();

    public CollectionAuthoringByProfileTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_database);
        SeedProfile(_me, "Me");
        SeedProfile(_someoneElse, "Someone else");
    }

    // ── Creating ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ANonAdministrator_CreatesACollectionTheyOwnAndOnlyTheySee()
    {
        var work = SeedWork("Arrival");
        await using var app = await StartAsync(Caller.Adult);

        using var response = await app.Client.PostAsJsonAsync("/collections", Create("Mine", "Custom", "Profile", "Private", work));

        await AssertStatusAsync(HttpStatusCode.Created, response);
        var id = (await ReadAsync(response))["id"]!.GetValue<Guid>();
        var row = Row(id);
        Assert.Equal("Profile", row.OwnerKind);
        Assert.Equal("Private", row.Audience);
        Assert.Equal("user", row.Scope);
        Assert.Equal(_me, row.ProfileId);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM collection_items WHERE collection_id = @id", new { id }));
    }

    [Fact]
    public async Task ANonAdministrator_CreatesAPlaylistTheyOwn()
    {
        await using var app = await StartAsync(Caller.Adult);

        using var response = await app.Client.PostAsJsonAsync("/collections", Create("Road trip", "Playlist", "Profile", "Private"));

        await AssertStatusAsync(HttpStatusCode.Created, response);
    }

    [Fact]
    public async Task ANonAdministrator_CannotMakeALibraryCollection_AndNothingIsSaved()
    {
        var work = SeedWork("Arrival");
        await using var app = await StartAsync(Caller.Adult);

        using var response = await app.Client.PostAsJsonAsync("/collections", Create("For everyone", "Custom", "Library", "Everyone", work));

        await AssertStatusAsync(HttpStatusCode.Forbidden, response);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM collections WHERE display_name = 'For everyone'"));
    }

    [Theory]
    [InlineData("Profile", "SelectedProfiles")]
    [InlineData("Profile", "Everyone")]
    [InlineData("Library", "Private")]
    public async Task ANonAdministrator_CannotShareWithSelectedProfilesOrEveryone(string owner, string audience)
    {
        var work = SeedWork("Arrival");
        await using var app = await StartAsync(Caller.Adult);
        var request = CreateWith("Shared", "Custom", owner, audience, [_someoneElse], "private", work);

        using var response = await app.Client.PostAsJsonAsync("/collections", request);

        await AssertStatusAsync(HttpStatusCode.Forbidden, response);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM collections WHERE display_name = 'Shared'"));
    }

    [Fact]
    public async Task ANonAdministrator_CannotAskForASharedVisibility()
    {
        await using var app = await StartAsync(Caller.Adult);
        var request = CreateWith("Pushed", "Playlist", "Profile", "Private", [], "shared");

        using var response = await app.Client.PostAsJsonAsync("/collections", request);

        await AssertStatusAsync(HttpStatusCode.Forbidden, response);
    }

    [Fact]
    public async Task AnAdministrator_MakesALibraryCollectionForEveryone()
    {
        var work = SeedWork("Arrival");
        await using var app = await StartAsync(Caller.Administrator);

        using var response = await app.Client.PostAsJsonAsync("/collections", Create("For everyone", "Custom", "Library", "Everyone", work));

        await AssertStatusAsync(HttpStatusCode.Created, response);
        var row = Row((await ReadAsync(response))["id"]!.GetValue<Guid>());
        Assert.Equal("Library", row.OwnerKind);
        Assert.Equal("Everyone", row.Audience);
    }

    [Fact]
    public async Task AnAdministrator_SharesWithSelectedProfiles()
    {
        var work = SeedWork("Arrival");
        await using var app = await StartAsync(Caller.Administrator);
        var request = CreateWith("For two", "Custom", "Profile", "SelectedProfiles", [_someoneElse], "private", work);

        using var response = await app.Client.PostAsJsonAsync("/collections", request);

        await AssertStatusAsync(HttpStatusCode.Created, response);
        var id = (await ReadAsync(response))["id"]!.GetValue<Guid>();
        Assert.Equal(1, Count("SELECT COUNT(*) FROM collection_profile_audience WHERE collection_id = @id", new { id }));
    }

    [Fact]
    public async Task AKidsProfile_CannotCreateACollection()
    {
        var work = SeedWork("Arrival");
        await using var app = await StartAsync(Caller.Kid);

        using var response = await app.Client.PostAsJsonAsync("/collections", Create("Mine", "Custom", "Profile", "Private", work));

        await AssertStatusAsync(HttpStatusCode.Forbidden, response);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM collections WHERE display_name = 'Mine'"));
    }

    [Fact]
    public async Task AnAdministratorWhoHasNotUnlockedTheirPin_IsTreatedAsAnOrdinaryProfile()
    {
        var work = SeedWork("Arrival");
        await using var app = await StartAsync(Caller.AdministratorLocked);

        using var shared = await app.Client.PostAsJsonAsync("/collections", Create("For everyone", "Custom", "Library", "Everyone", work));
        using var own = await app.Client.PostAsJsonAsync("/collections", Create("Mine", "Custom", "Profile", "Private", work));

        await AssertStatusAsync(HttpStatusCode.Forbidden, shared);
        await AssertStatusAsync(HttpStatusCode.Created, own);
    }

    // ── Editing and filling ──────────────────────────────────────────────────

    [Fact]
    public async Task ANonAdministrator_RenamesAndFillsTheirOwnCollection()
    {
        var first = SeedWork("Arrival");
        var second = SeedWork("Dune");
        var mine = SeedCollection("Mine", "Custom", ownerProfile: _me, first);
        await using var app = await StartAsync(Caller.Adult);

        using var rename = await app.Client.PutAsJsonAsync($"/collections/{mine}", new CollectionUpdateRequest { Name = "Renamed" });
        using var add = await app.Client.PostAsJsonAsync($"/collections/{mine}/items", new CollectionItemAddRequest { WorkId = second });

        await AssertStatusAsync(HttpStatusCode.OK, rename);
        await AssertStatusAsync(HttpStatusCode.OK, add);
        Assert.Equal(2, Count("SELECT COUNT(*) FROM collection_items WHERE collection_id = @mine", new { mine }));
        Assert.Equal("Renamed", Scalar<string>("SELECT display_name FROM collections WHERE id = @mine", new { mine }));
    }

    [Fact]
    public async Task ANonAdministrator_CannotMoveTheirCollectionToTheLibraryOrShareIt()
    {
        var work = SeedWork("Arrival");
        var mine = SeedCollection("Mine", "Custom", ownerProfile: _me, work);
        await using var app = await StartAsync(Caller.Adult);

        using var library = await app.Client.PutAsJsonAsync($"/collections/{mine}", new CollectionUpdateRequest { OwnerKind = "Library" });
        using var selected = await app.Client.PutAsJsonAsync(
            $"/collections/{mine}",
            new CollectionUpdateRequest { Audience = "SelectedProfiles", SelectedProfileIds = [_someoneElse] });
        using var shared = await app.Client.PutAsJsonAsync($"/collections/{mine}", new CollectionUpdateRequest { Visibility = "shared" });

        await AssertStatusAsync(HttpStatusCode.Forbidden, library);
        await AssertStatusAsync(HttpStatusCode.Forbidden, selected);
        await AssertStatusAsync(HttpStatusCode.Forbidden, shared);
        var row = Row(mine);
        Assert.Equal("Profile", row.OwnerKind);
        Assert.Equal("Private", row.Audience);
    }

    [Fact]
    public async Task ANonAdministrator_CannotEditFillOrPlaceALibraryCollection()
    {
        var first = SeedWork("Arrival");
        var second = SeedWork("Dune");
        var library = SeedCollection("Library picks", "Custom", ownerProfile: null, first);
        await using var app = await StartAsync(Caller.Adult);

        using var rename = await app.Client.PutAsJsonAsync($"/collections/{library}", new CollectionUpdateRequest { Name = "Hijacked" });
        using var add = await app.Client.PostAsJsonAsync($"/collections/{library}/items", new CollectionItemAddRequest { WorkId = second });
        using var place = await app.Client.PutAsJsonAsync(
            $"/collections/{library}/placements", new[] { new CollectionPlacementRequest { Location = "home", Position = 0 } });
        using var delete = await app.Client.DeleteAsync($"/collections/{library}");

        await AssertStatusAsync(HttpStatusCode.Forbidden, rename);
        await AssertStatusAsync(HttpStatusCode.Forbidden, add);
        await AssertStatusAsync(HttpStatusCode.Forbidden, place);
        Assert.NotEqual(HttpStatusCode.OK, delete.StatusCode);
        Assert.Equal("Library picks", Scalar<string>("SELECT display_name FROM collections WHERE id = @library", new { library }));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM collection_items WHERE collection_id = @library", new { library }));
    }

    [Fact]
    public async Task ANonAdministrator_CannotEditAnotherProfilesPrivateCollection()
    {
        var work = SeedWork("Arrival");
        var theirs = SeedCollection("Theirs", "Custom", ownerProfile: _someoneElse, work);
        await using var app = await StartAsync(Caller.Adult);

        using var rename = await app.Client.PutAsJsonAsync($"/collections/{theirs}", new CollectionUpdateRequest { Name = "Mine now" });

        await AssertStatusAsync(HttpStatusCode.Forbidden, rename);
    }

    [Fact]
    public async Task ANonAdministrator_PlacesTheirOwnCollectionOnTheirOwnLane()
    {
        var work = SeedWork("Arrival");
        var mine = SeedCollection("Mine", "Custom", ownerProfile: _me, work);
        await using var app = await StartAsync(Caller.Adult);

        using var place = await app.Client.PutAsJsonAsync(
            $"/collections/{mine}/placements", new[] { new CollectionPlacementRequest { Location = "home", Position = 0 } });

        await AssertStatusAsync(HttpStatusCode.OK, place);
    }

    [Fact]
    public async Task AnAdministrator_EditsAndFillsALibraryCollection()
    {
        var first = SeedWork("Arrival");
        var second = SeedWork("Dune");
        var library = SeedCollection("Library picks", "Custom", ownerProfile: null, first);
        await using var app = await StartAsync(Caller.Administrator);

        using var rename = await app.Client.PutAsJsonAsync($"/collections/{library}", new CollectionUpdateRequest { Name = "Staff picks", OwnerKind = "Library", Audience = "Everyone" });
        using var add = await app.Client.PostAsJsonAsync($"/collections/{library}/items", new CollectionItemAddRequest { WorkId = second });

        await AssertStatusAsync(HttpStatusCode.OK, rename);
        await AssertStatusAsync(HttpStatusCode.OK, add);
        Assert.Equal(2, Count("SELECT COUNT(*) FROM collection_items WHERE collection_id = @library", new { library }));
    }

    // ── Vault ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AVaultTitle_CannotBeAddedToACollectionOthersCanSee()
    {
        var vaultWork = SeedWork("Hidden");
        _vault.VaultEntities.Add(vaultWork);
        var library = SeedCollection("Library picks", "Custom", ownerProfile: null, SeedWork("Arrival"));
        await using var app = await StartAsync(Caller.Administrator);

        using var create = await app.Client.PostAsJsonAsync("/collections", Create("For everyone", "Custom", "Library", "Everyone", vaultWork));
        using var add = await app.Client.PostAsJsonAsync($"/collections/{library}/items", new CollectionItemAddRequest { WorkId = vaultWork });

        await AssertStatusAsync(HttpStatusCode.Forbidden, create);
        await AssertStatusAsync(HttpStatusCode.Forbidden, add);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM collection_items WHERE work_id = @vaultWork", new { vaultWork }));
    }

    [Fact]
    public async Task AVaultTitle_CanBeAddedToACollectionOnlyTheOwnerSees()
    {
        var vaultWork = SeedWork("Hidden");
        _vault.VaultEntities.Add(vaultWork);
        await using var app = await StartAsync(Caller.Adult);

        using var create = await app.Client.PostAsJsonAsync("/collections", Create("Mine", "Custom", "Profile", "Private", vaultWork));

        await AssertStatusAsync(HttpStatusCode.Created, create);
    }

    // ── What the "Add to collection" list is built from ─────────────────────

    [Fact]
    public async Task TheManagedList_SaysANonAdministratorCanEditOnlyTheirOwn()
    {
        var work = SeedWork("Arrival");
        var mine = SeedCollection("Mine", "Custom", ownerProfile: _me, work);
        var theirs = SeedCollection("Theirs", "Custom", ownerProfile: _someoneElse, work);
        var library = SeedCollection("Library picks", "Custom", ownerProfile: null, work);
        await using var app = await StartAsync(Caller.Adult);

        var managed = await ManagedAsync(app);

        Assert.True(managed[mine]);
        Assert.False(managed[library]);
        Assert.DoesNotContain(theirs, managed.Keys);
    }

    [Fact]
    public async Task TheManagedList_SaysAnAdministratorCanEditEverythingTheyManage()
    {
        var work = SeedWork("Arrival");
        var mine = SeedCollection("Mine", "Custom", ownerProfile: _me, work);
        var library = SeedCollection("Library picks", "Custom", ownerProfile: null, work);
        await using var app = await StartAsync(Caller.Administrator);

        var managed = await ManagedAsync(app);

        Assert.True(managed[mine]);
        Assert.True(managed[library]);
    }

    private static async Task<Dictionary<Guid, bool>> ManagedAsync(TestApplication app)
    {
        using var response = await app.Client.GetAsync("/collections/managed");
        await AssertStatusAsync(HttpStatusCode.OK, response);
        var items = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        return items.ToDictionary(item => item!["id"]!.GetValue<Guid>(), item => item!["can_edit"]!.GetValue<bool>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private enum Caller { Adult, Administrator, AdministratorLocked, Kid }

    private static async Task AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response) =>
        Assert.True(response.StatusCode == expected, $"Expected {expected} but got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private static CollectionCreateRequest Create(string name, string type, string owner, string audience, params Guid[] workIds) => new()
    {
        Name = name,
        CollectionType = type,
        OwnerKind = owner,
        Audience = audience,
        WorkIds = workIds.ToList(),
    };

    private static CollectionCreateRequest CreateWith(
        string name, string type, string owner, string audience, Guid[] selected, string visibility, params Guid[] workIds) => new()
        {
            Name = name,
            CollectionType = type,
            OwnerKind = owner,
            Audience = audience,
            Visibility = visibility,
            SelectedProfileIds = selected.ToList(),
            WorkIds = workIds.ToList(),
        };

    private OwnershipRow Row(Guid id)
    {
        using var connection = _database.CreateConnection();
        return connection.QuerySingle<OwnershipRow>(
            "SELECT owner_kind AS OwnerKind, audience AS Audience, scope AS Scope, profile_id AS ProfileId FROM collections WHERE id = @id",
            new { id });
    }

    private sealed record OwnershipRow(string OwnerKind, string Audience, string Scope, Guid? ProfileId);

    private void SeedProfile(Guid id, string name)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
            VALUES (@id, @name, '#7C4DFF', 'StandardUser', CURRENT_TIMESTAMP);
            """, new { id, name });
    }

    private Guid SeedWork(string title)
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
        return workId;
    }

    /// <summary>A hand-picked collection owned by a profile (private), or a Library collection when no profile is named.</summary>
    private Guid SeedCollection(string name, string type, Guid? ownerProfile, params Guid[] workIds)
    {
        var collection = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO collections (id, display_name, collection_type, scope, profile_id, resolution, membership_mode, owner_kind, audience)
            VALUES (@collection, @name, @type, @scope, @profile, 'materialized', 'Manual', @owner, @audience);
            """, new
        {
            collection,
            name,
            type,
            scope = ownerProfile is null ? "library" : "user",
            profile = ownerProfile,
            owner = ownerProfile is null ? "Library" : "Profile",
            audience = ownerProfile is null ? "Everyone" : "Private",
        });
        var order = 0;
        foreach (var workId in workIds)
        {
            connection.Execute(
                "INSERT INTO collection_items (id, collection_id, work_id, sort_order) VALUES (@id, @collection, @workId, @order);",
                new { id = Guid.NewGuid(), collection, workId, order = ++order });
        }

        return collection;
    }

    private int Count(string sql, object? parameters = null) => Scalar<int>(sql, parameters);

    private T Scalar<T>(string sql, object? parameters = null)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<T>(sql, parameters)!;
    }

    private async Task<TestApplication> StartAsync(Caller caller)
    {
        var authority = new RequestAuthority(
            PrincipalKind.Human,
            true,
            AccountId: _account,
            ActiveProfileId: _me,
            SessionId: Guid.NewGuid(),
            AccountEnabled: true,
            ActiveProfileInUserHousehold: true,
            ActiveProfileIsServerAdministrator: caller is Caller.Administrator or Caller.AdministratorLocked,
            ActiveProfileIsRestricted: caller == Caller.Kid);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        // The real route policy runs; who is calling and whether the administrator PIN is unlocked is faked.
        builder.Services.AddScoped<IAuthorizationHandler, AdministratorOrApplicationHandler>();
        builder.Services.AddLogging();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddHttpClient();
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddTuvimaStorage();
        builder.Services.AddTuvimaDisplay();
        builder.Services.AddSingleton<IVaultLibraryRepository>(_vault);
        builder.Services.AddSingleton<IHouseholdLibraryAccessRepository, TestLibraryVisibility.NoHouseholdLibraries>();
        builder.Services.AddSingleton<LibraryVisibilityRule>();
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(authority));
        builder.Services.AddSingleton<IAccountAccessDecisionService>(
            new LibraryAccess(_library, administratorUnlocked: caller == Caller.Administrator));
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowAuthorizationEvaluator>();
        builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
        builder.Services.AddSingleton<IArtworkPaletteService, MediaEngine.Providers.Services.ArtworkPaletteService>();
        foreach (var worker in builder.Services
                     .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                         && descriptor.ImplementationType?.Name != "GenericWebHostService")
                     .ToList())
        {
            builder.Services.Remove(worker);
        }

        var app = builder.Build();
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

    private sealed class FixedAuthorityResolver(RequestAuthority authority) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(authority);
    }

    private sealed class LibraryAccess(Guid libraryId, bool administratorUnlocked) : IAccountAccessDecisionService
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
            ValueTask.FromResult(administratorUnlocked
                ? AuthorizationDecision.Allow()
                : AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));
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
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "collection-authoring-test")], Scheme.Name)),
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
