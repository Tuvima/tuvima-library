using System.Security.Claims;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Libraries;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Looking at the folders whose first check is running or paused, and choosing Keep going, are an administrator's alone (Library setup
/// spec D35): every profile that is not an effective administrator is refused each action, whatever it asks for.
/// </summary>
public sealed class FolderFirstCheckEndpointTests
{
    private const string PrincipalClaim = "test-principal";

    private static readonly Guid Account = Guid.NewGuid();
    private static readonly Guid Profile = Guid.NewGuid();
    private static readonly Guid Household = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<string, RequestAuthority> Principals = new Dictionary<string, RequestAuthority>
    {
        ["server-admin"] = new(PrincipalKind.Human, true, Account, Profile, null, Guid.NewGuid(), null,
            AccountEnabled: true, ActiveProfileInUserHousehold: true, ActiveProfileIsServerAdministrator: true),
        ["household-member"] = new(PrincipalKind.Human, true, Account, Profile, null, Guid.NewGuid(), null,
            AccountEnabled: true, ActiveProfileInUserHousehold: true, ActiveProfileHouseholdId: Household),
        ["household-admin"] = new(PrincipalKind.Human, true, Account, Profile, null, Guid.NewGuid(), null,
            AccountEnabled: true, ActiveProfileInUserHousehold: true, ActiveProfileHouseholdId: Household, ActiveProfileIsHouseholdAdministrator: true),
    };

    [Theory]
    [InlineData("household-member")]
    [InlineData("household-admin")]
    public async Task EveryRoute_RefusesAProfileThatIsNotAServerAdministrator(string principal)
    {
        await using var app = BuildApplication();

        foreach (var route in Routes(app))
        {
            Assert.False(await PassesAsync(app, route, principal), $"{principal} must be refused {route.DisplayName}.");
        }
    }

    [Fact]
    public async Task EveryRoute_ServesAServerAdministrator()
    {
        await using var app = BuildApplication();

        foreach (var route in Routes(app))
        {
            Assert.True(await PassesAsync(app, route, "server-admin"), $"A server administrator must pass {route.DisplayName}.");
        }
    }

    [Fact]
    public void TheRoutes_AreTheListAndKeepGoing_AndNothingElse()
    {
        var app = BuildApplication();

        var routes = Routes(app).Select(route => route.RoutePattern.RawText!.TrimEnd('/')).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["/library/first-checks", "/library/first-checks/{sourceId}/keep-going"], routes);
    }

    [Fact]
    public void OnlyACheckThatIsCheckingOrPaused_IsShownOnASource()
    {
        var started = DateTimeOffset.UtcNow;
        FolderFirstCheckSnapshot Snapshot(string state) =>
            new(new FolderFirstCheck("source-a", "movies", "D:/Media", state, 50, 30, started, null), 300);

        var paused = FolderFirstCheckService.ToSourceDto(Snapshot(FolderFirstCheckStates.Paused))!;

        Assert.Equal(("paused", 50, 30, 300), (paused.State, paused.Tried, paused.Unmatched, paused.TotalFiles));
        Assert.Equal("checking", FolderFirstCheckService.ToSourceDto(Snapshot(FolderFirstCheckStates.Checking))!.State);
        Assert.Null(FolderFirstCheckService.ToSourceDto(Snapshot(FolderFirstCheckStates.Passed)));
        Assert.Null(FolderFirstCheckService.ToSourceDto(Snapshot(FolderFirstCheckStates.KeptGoing)));
        Assert.Null(FolderFirstCheckService.ToSourceDto(null));
    }

    private static WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Services.AddAuthorization(options => options.AddPolicy(
            AuthPolicies.Administrator,
            policy => policy.RequireAuthenticatedUser().AddRequirements(new EffectiveAdministratorRequirement(true))));
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new PrincipalClaimResolver());
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new AdministratorDecisions());
        builder.Services.AddScoped<IAuthorizationHandler, EffectiveAdministratorHandler>();
        builder.Services.AddSingleton<FolderFirstCheckService>(_ => null!);
        var app = builder.Build();
        app.MapFolderFirstCheckEndpoints();
        return app;
    }

    private static IReadOnlyList<RouteEndpoint> Routes(WebApplication app) =>
        [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()];

    // What the authorization middleware does for an endpoint: combine its policies, then evaluate them for the principal.
    private static async Task<bool> PassesAsync(WebApplication app, RouteEndpoint endpoint, string principal)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(PrincipalClaim, principal)], "test"));
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = user };
        var provider = scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await AuthorizationPolicy.CombineAsync(
            provider,
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
        Assert.NotNull(policy);
        var result = await scope.ServiceProvider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, context, policy);
        return result.Succeeded;
    }

    private sealed class PrincipalClaimResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(Principals[context.User.FindFirstValue(PrincipalClaim)!]);
    }
}
