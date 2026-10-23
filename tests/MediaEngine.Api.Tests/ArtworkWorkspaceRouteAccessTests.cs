using System.Security.Claims;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The artwork workspace routes under <c>/api/v1/display/artwork</c> browse raw artwork over every Work and Collection with no
/// profile or content-limit filter. A paired app (Kids or adult) holds <c>artwork.read</c> by default, so that scope alone must not open them:
/// they need an administrator or a service Application. The one profile-checked route (<c>effective-cover</c>) stays an app route.
/// The real route table and the real authorization handlers run; only the identity behind each paired device is a fixed authority.
/// </summary>
public sealed class ArtworkWorkspaceRouteAccessTests
{
    private const string PrincipalClaim = "test-principal";
    private const string EffectiveCover = "/api/v1/display/artwork/media-assets/{assetId:guid}/effective-cover";

    private static readonly Guid Account = Guid.NewGuid();
    private static readonly Guid KidsProfile = Guid.NewGuid();
    private static readonly Guid AdultProfile = Guid.NewGuid();
    private static readonly Guid NativeApp = BuiltInApplicationIds.NativeClient;

    private static readonly IReadOnlyDictionary<string, RequestAuthority> Principals = new Dictionary<string, RequestAuthority>
    {
        // A paired Kids TV and a paired adult phone: delegated device tokens, never an administrator.
        ["kids-app"] = new(PrincipalKind.DelegatedUserClient, true, Account, KidsProfile, NativeApp, null, Guid.NewGuid(),
            AccountEnabled: true, ActiveProfileInUserHousehold: true, ApplicationEnabled: true, ActiveProfileIsRestricted: true),
        ["adult-app"] = new(PrincipalKind.DelegatedUserClient, true, Account, AdultProfile, NativeApp, null, Guid.NewGuid(),
            AccountEnabled: true, ActiveProfileInUserHousehold: true, ApplicationEnabled: true),
        // The Dashboard: a signed-in administrator, a signed-in non-administrator, and a service Application.
        ["admin"] = new(PrincipalKind.Human, true, Account, AdultProfile, null, Guid.NewGuid(), null,
            AccountEnabled: true, ActiveProfileInUserHousehold: true, ActiveProfileIsServerAdministrator: true),
        ["member"] = new(PrincipalKind.Human, true, Account, AdultProfile, null, Guid.NewGuid(), null,
            AccountEnabled: true, ActiveProfileInUserHousehold: true),
        ["service"] = new(PrincipalKind.ServiceApplication, true, null, null, Guid.NewGuid(),
            ApplicationEnabled: true),
    };

    [Fact]
    public async Task EveryArtworkWorkspaceRoute_IsRefusedToPairedKidsAndAdultTokens_EvenWithArtworkRead()
    {
        await using var app = BuildApplication();
        var workspace = WorkspaceRoutes(app);

        // The six reads of the workspace; none of them is the profile-checked effective-cover route.
        Assert.Equal(
            [
                "/api/v1/display/artwork",
                "/api/v1/display/artwork/assets",
                "/api/v1/display/artwork/assets/{assetId:guid}/content",
                "/api/v1/display/artwork/entities/{entityType}/{entityId:guid}",
                "/api/v1/display/artwork/universes/{collectionId:guid}/entities",
                "/api/v1/display/artwork/works/{workId:guid}/effective",
            ],
            workspace.Select(route => route.RoutePattern.RawText!).Order(StringComparer.Ordinal));

        foreach (var route in workspace)
        {
            foreach (var principal in new[] { "kids-app", "adult-app", "member" })
            {
                Assert.False(await PassesAsync(app, route, principal),
                    $"{principal} must not reach {route.RoutePattern.RawText}.");
            }
        }
    }

    [Fact]
    public async Task EveryArtworkWorkspaceRoute_StillServesTheDashboard_AdministratorAndServiceApplication()
    {
        await using var app = BuildApplication();

        foreach (var route in WorkspaceRoutes(app))
        {
            foreach (var principal in new[] { "admin", "service" })
            {
                Assert.True(await PassesAsync(app, route, principal),
                    $"{principal} must still reach {route.RoutePattern.RawText}.");
            }
        }
    }

    [Fact]
    public async Task EffectiveCover_RemainsAnAppRoute_BecauseItChecksTheProfileAgainstTheFile()
    {
        await using var app = BuildApplication();
        var route = Routes(app).Single(candidate => candidate.RoutePattern.RawText == EffectiveCover);

        foreach (var principal in new[] { "kids-app", "adult-app", "admin" })
        {
            Assert.True(await PassesAsync(app, route, principal), $"{principal} must reach the effective-cover route.");
        }

        Assert.Contains(route.Metadata.OfType<ClientScopeRequirementMetadata>(),
            metadata => metadata.Scope == MediaEngine.Contracts.Authentication.ClientApiScopes.ArtworkRead);
    }

    [Fact]
    public async Task TheBrowseRoutes_KeepTheirClientScopeRule()
    {
        await using var app = BuildApplication();
        var browse = Routes(app).Where(candidate =>
                !(candidate.RoutePattern.RawText ?? "").StartsWith("/api/v1/display/artwork", StringComparison.Ordinal))
            .ToArray();

        // Home, browse, continue, item lookup, recent, contributor shelves, search, shelves, grids and groups: a paired app is served (the profile limit is applied inside).
        Assert.Equal(10, browse.Length);
        foreach (var route in browse)
        {
            Assert.Contains(route.Metadata.OfType<ClientScopeRequirementMetadata>(),
                metadata => metadata.Scope is "library.read" or "progress.read");
        }
    }

    private static IEnumerable<RouteEndpoint> Routes(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>();

    // Everything readable under /display/artwork except the one effective-cover route that authorizes the owned file itself.
    private static RouteEndpoint[] WorkspaceRoutes(WebApplication app) => Routes(app)
        .Where(route => (route.RoutePattern.RawText ?? "").StartsWith("/api/v1/display/artwork", StringComparison.Ordinal)
            && route.RoutePattern.RawText != EffectiveCover
            && route.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("GET"))
        .ToArray();

    // What the authorization middleware does for an endpoint: combine its policies, then evaluate them for the principal.
    private static async Task<bool> PassesAsync(WebApplication app, RouteEndpoint endpoint, string principal)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(PrincipalClaim, principal)], "test"));
        var context = new DefaultHttpContext { RequestServices = app.Services, User = user };
        var provider = app.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await AuthorizationPolicy.CombineAsync(
            provider,
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
        Assert.NotNull(policy);
        var result = await app.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, context, policy);
        return result.Succeeded;
    }

    private static WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthPolicies.Authenticated, policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new PrincipalClaimResolver());
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAccountAccessDecisionService>(new AdministratorDecisions());
        // An app holds artwork.read by default: the scope check allows it, so only the route's own policy can refuse.
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator>(new TestAllowAuthorizationEvaluator());
        builder.Services.AddScoped<IAuthorizationHandler, AdministratorOrApplicationHandler>();
        // The actions in the group take these. They are never reached here, but route building must be able to infer them.
        builder.Services.AddSingleton<DisplayComposerService>(_ => null!);
        builder.Services.AddSingleton<DisplayRecentComposerService>(_ => null!);
        builder.Services.AddSingleton<ContributorShelfReadService>(_ => null!);
        builder.Services.AddSingleton<IUniversalSearchReadService>(_ => null!);
        builder.Services.AddSingleton<ArtworkLibraryReadService>(_ => null!);
        builder.Services.AddSingleton<ArtworkAssetService>(_ => null!);
        builder.Services.AddSingleton<CatalogueResourceAuthorizationService>(_ => null!);
        builder.Services.AddSingleton<MediaEditorEditionArtworkRepository>(_ => null!);
        var app = builder.Build();
        app.MapDisplayEndpoints();
        return app;
    }

    private sealed class PrincipalClaimResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(Principals[context.User.FindFirstValue(PrincipalClaim)!]);
    }
}
