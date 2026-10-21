using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MediaEngine.Api.Tests;

/// <summary>
/// <c>GET /api/v1/companion/context</c> on the real route: it needs both the queue.read and the library.read scopes, answers
/// only for the signed-in profile (a query naming another profile is refused and a caller with no profile is judged at the
/// default protection), and describes only Works the display projection holds.
/// </summary>
public sealed class CompanionContextEndpointTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-companion-context-endpoint-{Guid.NewGuid():N}.db");
    private readonly Actor _actor = new();
    private readonly Guid _filmId = Guid.NewGuid();
    private DatabaseConnection _database = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private string Route(Guid? workId = null, Guid? profileId = null) =>
        $"/api/v1/companion/context?workId={workId ?? _filmId:D}" + (profileId is { } id ? $"&profileId={id:D}" : string.Empty);

    public async Task InitializeAsync()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        var profiles = new ProfileRepository(_database);
        await new UserPlaybackSettingsService(_database, profiles).SetSpoilerLevelAsync(Profile.SeedProfileId, SpoilerLevels.Everything);

        var film = new DisplayWorkRow
        {
            WorkId = _filmId,
            AssetId = Guid.NewGuid(),
            RootWorkId = _filmId,
            EditionId = Guid.NewGuid(),
            MediaType = "Movies",
            IdentityQid = "Q-film",
            Title = "A Quiet Film",
            Description = "A quiet film.",
            Year = "2020",
            CoverUrl = $"/stream/artwork/{_filmId:D}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var display = new DisplayStub([film]);
        var cards = new DisplayCardBuilder();
        var entities = new FictionalEntityRepository(_database);
        var people = new PersonRepository(_database);
        var spoilers = SpoilerTestKit.Create(display, _database);
        var connections = new UniverseConnectionsReadService(
            entities,
            new EntityRelationshipRepository(_database),
            new NarrativeRootRepository(_database),
            people,
            new PluginLoreRepository(_database),
            spoilers);
        var service = new CompanionContextReadService(
            display,
            cards,
            spoilers,
            entities,
            people,
            new LibraryItemRepository(_database),
            new UniverseClientReadService(new CollectionRepository(_database), display, cards, connections),
            new PersonCreditReadService(new CanonicalValueArrayRepository(_database), people, _database),
            new NoChapters());

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton(_actor);
        builder.Services.AddSingleton<IProfileRepository>(profiles);
        builder.Services.AddSingleton(service);
        builder.Services.AddSingleton<IRequestAuthorityResolver, ActorAuthorityResolver>();
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator, ScopeEvaluator>();
        DeclareUnusedEngineServices(builder.Services);
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapCompanionEndpoints();
        await _app.StartAsync();
        var address = new Uri(_app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        _client = new HttpClient { BaseAddress = address };
        _actor.ProfileId = Profile.SeedProfileId;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public async Task ACallerWithBothScopes_GetsTheContextOfTheirOwnProfile()
    {
        var context = await _client.GetFromJsonAsync<CompanionContextDto>(Route());

        Assert.NotNull(context);
        Assert.Equal("A Quiet Film", context.Title);
        Assert.Equal(CompanionKinds.Film, context.Kind);
        // The seed profile saved "Everything"; the level comes from the signed-in profile.
        Assert.Equal(SpoilerLevels.Everything, context.SpoilerLevel);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATokenMissingEitherScope_IsRefusedWithInsufficientScope(bool libraryRead)
    {
        _actor.Denied.Add(libraryRead ? ClientApiScopes.LibraryRead : ClientApiScopes.QueueRead);

        using var response = await _client.GetAsync(Route());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("insufficient_scope", body, StringComparison.Ordinal);
        Assert.DoesNotContain("A Quiet Film", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQueryNamingAnotherProfile_IsRefused_AndNothingOfTheirsIsRead()
    {
        using var response = await _client.GetAsync(Route(profileId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("A Quiet Film", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCallersOwnProfileIdInTheQuery_IsAccepted()
    {
        using var response = await _client.GetAsync(Route(profileId: Profile.SeedProfileId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ACallerWithNoProfileClaim_CannotChooseOne_AndIsJudgedAtTheDefaultProtection()
    {
        _actor.ProfileId = null;

        var context = await _client.GetFromJsonAsync<CompanionContextDto>(Route(profileId: Profile.SeedProfileId));

        // The seed profile's "Everything" is not applied: the query is ignored without a claim.
        Assert.NotNull(context);
        Assert.Equal(SpoilerLevels.UpToProgress, context.SpoilerLevel);
    }

    [Fact]
    public async Task AWorkTheProjectionDoesNotHold_IsNotFound()
    {
        using var response = await _client.GetAsync(Route(workId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static void DeclareUnusedEngineServices(IServiceCollection services)
    {
        var assemblies = new[]
        {
            typeof(ClientAuthorizationEndpoints).Assembly,
            typeof(MediaEngine.Domain.Contracts.IAuthorizationEvaluator).Assembly,
            typeof(MediaEngine.Identity.Contracts.IAccountExternalLoginService).Assembly,
            typeof(DatabaseConnection).Assembly,
        };
        string[] suffixes = ["Service", "Repository", "Loader", "Resolver", "Store", "Provider", "Reader", "Evaluator", "Access", "Gate"];
        foreach (var type in assemblies.SelectMany(assembly => assembly.GetExportedTypes()))
        {
            if (type.IsGenericTypeDefinition || (type.IsAbstract && type.IsSealed) || !suffixes.Any(suffix => type.Name.EndsWith(suffix, StringComparison.Ordinal))
                || (!type.IsInterface && (type.IsAbstract || !type.IsClass))
                || services.Any(descriptor => descriptor.ServiceType == type))
            {
                continue;
            }

            services.AddSingleton(type, _ => throw new NotSupportedException($"{type.Name} is not used by this test."));
        }
    }

    private sealed class Actor
    {
        public Guid? ProfileId { get; set; }
        public HashSet<string> Denied { get; } = [];
    }

    private sealed class ActorAuthorityResolver(Actor actor) : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(new RequestAuthority(
                PrincipalKind.DelegatedUserClient, true, Guid.NewGuid(), actor.ProfileId,
                ApplicationId: BuiltInApplicationIds.NativeClient, DeviceId: Guid.NewGuid()));
    }

    /// <summary>Allows an Application permission unless the test took that scope away from the token.</summary>
    private sealed class ScopeEvaluator(Actor actor) : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken ct = default) =>
            ValueTask.FromResult(requirement.ApplicationPermission is { } permission && actor.Denied.Contains(permission.Value)
                ? AuthorizationDecision.Deny(AuthorizationDenialReason.MissingPermission)
                : AuthorizationDecision.Allow());
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, Actor actor)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "companion-context-test") };
            if (actor.ProfileId is { } profileId)
            {
                claims.Add(new Claim(TuvimaClaimTypes.ActiveProfileId, profileId.ToString("D")));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }

    private sealed class NoChapters : ICompanionChapterSource
    {
        public Task<IReadOnlyList<CompanionChapter>> GetChaptersAsync(DisplayWorkRow row, Guid? profileId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CompanionChapter>>([]);
    }

    private sealed class DisplayStub(IReadOnlyList<DisplayWorkRow> works) : IDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => Task.FromResult(works);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) => LoadJourneyAsync(profileId, lane, ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }
}
