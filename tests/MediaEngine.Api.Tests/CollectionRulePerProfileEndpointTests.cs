using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
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
/// Rules that depend on who is looking (Unwatched, On My List and the like) are worked out for the profile making the
/// request. Runs the real preview route against a real data store; only the caller's identity and permissions are faked.
/// </summary>
public sealed class CollectionRulePerProfileEndpointTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_rule_profiles_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly Guid _library = Guid.NewGuid();
    private readonly Guid _account = Guid.NewGuid();
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();

    public CollectionRulePerProfileEndpointTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_database);
        AddProfile(_alice, "Alice");
        AddProfile(_bob, "Bob");
    }

    [Fact]
    public async Task Preview_OfUnwatchedAndRatedAtLeast_AnswersForTheProfileThatAsksAndSaysSo()
    {
        var eight = SeedWork("Eight", "8.0");
        var nine = SeedWork("Nine", "9.0");
        SeedWork("Seven", "7.0");
        Watched(_alice, eight);
        Watched(_bob, nine);
        var request = new CollectionPreviewRequest
        {
            RuleDefinition = new CollectionRuleDefinitionDto
            {
                Groups =
                [
                    new CollectionRuleGroupDto
                    {
                        MatchMode = "all",
                        Conditions =
                        [
                            new CollectionRulePredicateDto { Field = "watch_state", Op = "eq", Value = "unwatched" },
                            new CollectionRulePredicateDto { Field = "provider_rating", Op = "gte", Value = "7.5" },
                        ],
                    },
                ],
            },
        };

        await using var asAlice = await StartAsync(_alice);
        await using var asBob = await StartAsync(_bob);
        var alice = await PreviewAsync(asAlice, request);
        var bob = await PreviewAsync(asBob, request);

        Assert.Equal([nine], alice.Items.Select(item => item.EntityId));
        Assert.Equal([eight], bob.Items.Select(item => item.EntityId));
        Assert.True(alice.UsesPerProfileFields);
        Assert.Equal("Alice", alice.EvaluatedForProfileName);
        Assert.Equal("Bob", bob.EvaluatedForProfileName);
    }

    [Fact]
    public async Task Preview_OfRulesThatDoNotDependOnTheViewer_DoesNotNameAProfile()
    {
        SeedWork("Eight", "8.0");
        var request = new CollectionPreviewRequest
        {
            RuleDefinition = new CollectionRuleDefinitionDto
            {
                Groups =
                [
                    new CollectionRuleGroupDto
                    {
                        Conditions = [new CollectionRulePredicateDto { Field = "provider_rating", Op = "gte", Value = "7.5" }],
                    },
                ],
            },
        };

        await using var app = await StartAsync(_alice);
        var preview = await PreviewAsync(app, request);

        Assert.Equal(1, preview.Count);
        Assert.False(preview.UsesPerProfileFields);
        Assert.Null(preview.EvaluatedForProfileName);
    }

    private static async Task<CollectionPreviewResponse> PreviewAsync(TestApplication app, CollectionPreviewRequest request)
    {
        using var response = await app.Client.PostAsJsonAsync("/collections/preview", request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Preview answered {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<CollectionPreviewResponse>())!;
    }

    private void AddProfile(Guid id, string name)
    {
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO profiles (id, display_name, avatar_color, role, created_at) VALUES (@id, @name, '#7C4DFF', 'StandardUser', CURRENT_TIMESTAMP);",
            new { id, name });
    }

    private Guid SeedWork(string title, string rating)
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
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@assetId, 'title', @title, CURRENT_TIMESTAMP);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@workId, 'rating', @rating, CURRENT_TIMESTAMP);
            """, new
        {
            workId,
            editionId,
            assetId,
            hash = Guid.NewGuid().ToString("N"),
            path = $"C:/library/{assetId:N}.mkv",
            library = _library.ToString("D"),
            title,
            rating,
        });
        return workId;
    }

    private void Watched(Guid profile, Guid workId)
    {
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed)
            SELECT @profile, ma.id, 100, CURRENT_TIMESTAMP
            FROM editions e INNER JOIN media_assets ma ON ma.edition_id = e.id
            WHERE e.work_id = @workId;
            """, new { profile, workId });
    }

    private async Task<TestApplication> StartAsync(Guid profile)
    {
        var authority = new RequestAuthority(
            PrincipalKind.Human,
            true,
            AccountId: _account,
            ActiveProfileId: profile,
            SessionId: Guid.NewGuid(),
            DeviceId: Guid.NewGuid(),
            AccountEnabled: true,
            ActiveProfileInUserHousehold: true,
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
        builder.Services.AddHttpClient();
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton<IDatabaseConnection>(_database);
        builder.Services.AddTuvimaStorage();
        builder.Services.AddTuvimaDisplay();
        builder.Services.AddSingleton<IVaultLibraryRepository>(new FixedVaultLibraries());
        builder.Services.AddSingleton<IHouseholdLibraryAccessRepository, TestLibraryVisibility.NoHouseholdLibraries>();
        builder.Services.AddSingleton<LibraryVisibilityRule>();
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new FixedAuthorityResolver(authority));
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new LibraryAccess(_library));
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
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "rule-profile-test")], Scheme.Name)),
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
