using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Dapper;
using MediaEngine.Api.DependencyInjection;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Api.Security;
using MediaEngine.Contracts.Collections;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
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
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A manually curated collection (Custom, manual membership) is never empty: the Engine refuses to create one without
/// items, refuses to take out the last item and refuses an update that would leave one empty. Playlists are untouched.
/// Runs the real collection routes against a real data store; only the caller's identity and permissions are faked.
/// </summary>
public sealed class CuratedCollectionEndpointTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_curated_endpoints_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _library = Guid.NewGuid();
    private readonly Guid _account = Guid.NewGuid();

    public CuratedCollectionEndpointTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_database);
    }

    [Fact]
    public async Task CreatingACuratedCollectionWithoutItems_IsRefusedAndNothingIsSaved()
    {
        await using var app = await StartAsync();

        using var response = await app.Client.PostAsJsonAsync("/collections", Create("Empty", "Custom"));

        await AssertStatusAsync(HttpStatusCode.BadRequest, response);
        Assert.Contains("A collection needs at least one item.", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, Count("SELECT COUNT(*) FROM collections WHERE display_name = 'Empty'"));
    }

    [Fact]
    public async Task CreatingACuratedCollectionWithItems_SavesTheCollectionAndItsItemsTogether()
    {
        var first = SeedWork("Arrival");
        var second = SeedWork("Dune");
        await using var app = await StartAsync();

        using var response = await app.Client.PostAsJsonAsync("/collections", Create("Weekend", "Custom", first, second));

        await AssertStatusAsync(HttpStatusCode.Created, response);
        var created = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var id = created["id"]!.GetValue<Guid>();
        Assert.Equal(2, Count("SELECT COUNT(*) FROM collection_items WHERE collection_id = @id", new { id }));
    }

    [Fact]
    public async Task CreatingAPlaylistWithoutItems_IsStillAllowed()
    {
        await using var app = await StartAsync();

        using var response = await app.Client.PostAsJsonAsync("/collections", Create("Road trip", "Playlist"));

        await AssertStatusAsync(HttpStatusCode.Created, response);
    }

    [Fact]
    public async Task TakingOutTheLastItemOfACuratedCollection_IsRefusedWithAClearMessage()
    {
        var only = SeedWork("Arrival");
        var collection = SeedCollection("Just one", "Custom", only);
        var itemId = ItemIds(collection).Single();
        await using var app = await StartAsync();

        using var response = await app.Client.DeleteAsync($"/collections/{collection:D}/items/{itemId:D}");

        await AssertStatusAsync(HttpStatusCode.Conflict, response);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("A collection needs at least one item. Delete the collection instead.", problem["detail"]!.GetValue<string>());
        Assert.Equal(1, Count("SELECT COUNT(*) FROM collection_items WHERE collection_id = @collection", new { collection }));
    }

    [Fact]
    public async Task TakingOutTheLastWorkItem_IsAllowedWhenAPersonalMediaSourceRemains()
    {
        var collection = SeedCollection("Film and photos", "Custom", SeedWork("Arrival"));
        SeedViewSource(collection);
        var itemId = ItemIds(collection).Single();
        await using var app = await StartAsync();

        using var response = await app.Client.DeleteAsync($"/collections/{collection:D}/items/{itemId:D}");

        await AssertStatusAsync(HttpStatusCode.OK, response);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM collection_items WHERE collection_id = @collection", new { collection }));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM collections WHERE id = @collection", new { collection }));
    }

    [Fact]
    public async Task TakingOutAnItemWhileOthersRemain_Works()
    {
        var collection = SeedCollection("Two films", "Custom", SeedWork("Arrival"), SeedWork("Dune"));
        var itemId = ItemIds(collection).First();
        await using var app = await StartAsync();

        using var response = await app.Client.DeleteAsync($"/collections/{collection:D}/items/{itemId:D}");

        await AssertStatusAsync(HttpStatusCode.OK, response);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM collection_items WHERE collection_id = @collection", new { collection }));
    }

    [Fact]
    public async Task TakingOutTheLastItemOfAPlaylist_IsStillAllowed()
    {
        var playlist = SeedCollection("Road trip", "Playlist", SeedWork("Song"));
        var itemId = ItemIds(playlist).Single();
        await using var app = await StartAsync();

        using var response = await app.Client.DeleteAsync($"/collections/{playlist:D}/items/{itemId:D}");

        await AssertStatusAsync(HttpStatusCode.OK, response);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM collection_items WHERE collection_id = @playlist", new { playlist }));
    }

    [Fact]
    public async Task SwitchingARuleDrivenCollectionToManualMembershipWithNoItems_IsRefused()
    {
        SeedWork("Arrival");
        var ruleDriven = SeedCollection("Every movie", "Custom", resolution: "query", membership: "Smart");
        SetMovieRule(ruleDriven);

        await using var app = await StartAsync();

        using var response = await app.Client.PutAsJsonAsync($"/collections/{ruleDriven:D}", new CollectionUpdateRequest
        {
            MembershipMode = "Manual",
            RuleDefinition = new CollectionRuleDefinitionDto(),
        });

        await AssertStatusAsync(HttpStatusCode.Conflict, response);
        Assert.Contains("A collection needs at least one item.", await response.Content.ReadAsStringAsync());
        Assert.Equal("query", Scalar<string>("SELECT resolution FROM collections WHERE id = @ruleDriven", new { ruleDriven }));
    }

    [Fact]
    public async Task SwitchingARuleDrivenCollectionToManualMembership_SavesTheStagedItemsInTheSameCall()
    {
        var arrival = SeedWork("Arrival");
        var dune = SeedWork("Dune");
        var ruleDriven = SeedCollection("Every movie", "Custom", resolution: "query", membership: "Smart");
        SetMovieRule(ruleDriven);
        await using var app = await StartAsync();

        using var response = await app.Client.PutAsJsonAsync($"/collections/{ruleDriven:D}", new CollectionUpdateRequest
        {
            MembershipMode = "Manual",
            RuleDefinition = new CollectionRuleDefinitionDto(),
            WorkIds = [arrival, dune],
        });

        await AssertStatusAsync(HttpStatusCode.OK, response);
        Assert.Equal("materialized", Scalar<string>("SELECT resolution FROM collections WHERE id = @ruleDriven", new { ruleDriven }));
        Assert.Equal("Manual", Scalar<string>("SELECT membership_mode FROM collections WHERE id = @ruleDriven", new { ruleDriven }));
        Assert.Equal(2, Count("SELECT COUNT(*) FROM collection_items WHERE collection_id = @ruleDriven", new { ruleDriven }));
    }

    [Fact]
    public async Task SwitchingARuleDrivenCollectionToManualMembership_IsAllowedWhenAPersonalMediaSourceIsAttached()
    {
        SeedWork("Arrival");
        var ruleDriven = SeedCollection("Every movie", "Custom", resolution: "query", membership: "Smart");
        SetMovieRule(ruleDriven);
        SeedViewSource(ruleDriven);
        await using var app = await StartAsync();

        using var response = await app.Client.PutAsJsonAsync($"/collections/{ruleDriven:D}", new CollectionUpdateRequest
        {
            MembershipMode = "Manual",
            RuleDefinition = new CollectionRuleDefinitionDto(),
        });

        await AssertStatusAsync(HttpStatusCode.OK, response);
        Assert.Equal("materialized", Scalar<string>("SELECT resolution FROM collections WHERE id = @ruleDriven", new { ruleDriven }));
    }

    [Fact]
    public async Task StagedItemsSentToACollectionThatStaysRuleDriven_AreRefusedAndNothingIsSaved()
    {
        var arrival = SeedWork("Arrival");
        var ruleDriven = SeedCollection("Every movie", "Custom", resolution: "query", membership: "Smart");
        SetMovieRule(ruleDriven);
        await using var app = await StartAsync();

        using var response = await app.Client.PutAsJsonAsync($"/collections/{ruleDriven:D}", new CollectionUpdateRequest
        {
            Name = "Renamed",
            WorkIds = [arrival],
        });

        await AssertStatusAsync(HttpStatusCode.BadRequest, response);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM collection_items WHERE collection_id = @ruleDriven", new { ruleDriven }));
        Assert.Equal("Every movie", Scalar<string>("SELECT display_name FROM collections WHERE id = @ruleDriven", new { ruleDriven }));
    }

    [Fact]
    public async Task RenamingACuratedCollectionThatHasItems_Works()
    {
        var collection = SeedCollection("Old name", "Custom", SeedWork("Arrival"));
        await using var app = await StartAsync();

        using var response = await app.Client.PutAsJsonAsync($"/collections/{collection:D}", new CollectionUpdateRequest
        {
            Name = "New name",
            RuleDefinition = new CollectionRuleDefinitionDto(),
        });

        await AssertStatusAsync(HttpStatusCode.OK, response);
        Assert.Equal("New name", Scalar<string>("SELECT display_name FROM collections WHERE id = @collection", new { collection }));
    }

    private static async Task AssertStatusAsync(HttpStatusCode expected, HttpResponseMessage response)
    {
        // Show the Engine's answer when the status is not the expected one, so a failure explains itself.
        Assert.True(response.StatusCode == expected, $"Expected {expected} but got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static CollectionCreateRequest Create(string name, string type, params Guid[] workIds) => new()
    {
        Name = name,
        CollectionType = type,
        OwnerKind = "Library",
        Audience = "Everyone",
        Visibility = "shared",
        WorkIds = workIds.ToList(),
    };

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

    private Guid SeedCollection(string name, string type, params Guid[] workIds) =>
        SeedCollection(name, type, resolution: "materialized", membership: "Manual", workIds);

    private Guid SeedCollection(string name, string type, string resolution, string membership, params Guid[] workIds)
    {
        var collection = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, membership_mode, owner_kind, audience)
            VALUES (@collection, @name, @type, 'library', @resolution, @membership, 'Library', 'Everyone');
            """, new { collection, name, type, resolution, membership });
        var order = 0;
        foreach (var workId in workIds)
        {
            connection.Execute(
                "INSERT INTO collection_items (id, collection_id, work_id, sort_order) VALUES (@id, @collection, @workId, @order);",
                new { id = Guid.NewGuid(), collection, workId, order = ++order });
        }

        return collection;
    }

    /// <summary>The rule matches the seeded movies, so the caller is allowed to see (and edit) the collection.</summary>
    private void SetMovieRule(Guid collection)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "UPDATE collections SET rule_json = @ruleJson WHERE id = @collection;",
            new
            {
                collection,
                ruleJson = System.Text.Json.JsonSerializer.Serialize(CollectionRuleDefinition.SingleGroup(
                    [new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "Movies" }])),
            });
    }

    private void SeedViewSource(Guid collection)
    {
        var profile = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
            VALUES (@profile, 'Owner', '#7C4DFF', 'StandardUser', CURRENT_TIMESTAMP);
            INSERT INTO collection_view_sources
                (id, collection_id, owner_profile_id, source_kind, rule_version, rule_json, position, created_at, updated_at)
            VALUES (@id, @collection, @profile, 'smart_rule', 1, '{}', 0, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            """, new { id = Guid.NewGuid(), collection, profile });
    }

    private List<Guid> ItemIds(Guid collection)
    {
        using var connection = _database.CreateConnection();
        return connection.Query<Guid>(
            "SELECT id FROM collection_items WHERE collection_id = @collection ORDER BY sort_order", new { collection }).ToList();
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
            GrantEnabled: true,
            ApplicationEnabled: true,
            AccountAuthorizationVersion: 1,
            GrantAuthorizationVersion: 1,
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
        // Who is calling, and what they may do, is faked; the collection rules under test are real.
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(authority));
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new LibraryAccess(_library));
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, AllowAuthorizationEvaluator>();
        builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IArtworkPaletteService, MediaEngine.Providers.Services.ArtworkPaletteService>();
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

    /// <summary>The caller is allowed to write collections; what is under test is the collection rule, not the permission check.</summary>
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
                : AuthorizationDecision.Deny(AuthorizationDenialReason.MissingLibraryGrant));

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
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "curated-collection-test")], Scheme.Name)),
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
