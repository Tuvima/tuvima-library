using System.Security.Claims;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Identity.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Asking who would lose sight of files names other people, so it is an administrator's alone (simpler access D13): every profile that
/// is not an effective server administrator is refused, whatever the request says.
/// </summary>
public sealed class VisibilityAfterMoveEndpointTests
{
    private const string PrincipalClaim = "test-principal";
    private const string Route = "/access/visibility-after-move";

    private static readonly Guid Account = Guid.NewGuid();
    private static readonly Guid Profile = Guid.NewGuid();
    private static readonly Guid Household = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<string, RequestAuthority> Principals = new Dictionary<string, RequestAuthority>
    {
        ["server-admin"] = new(PrincipalKind.Human, true, Account, Profile, null, Guid.NewGuid(), null,
            AccountEnabled: true, GrantEnabled: true, AccountIsAdministrator: true, GrantAdminEnabled: true),
        ["household-member"] = new(PrincipalKind.Human, true, Account, Profile, null, Guid.NewGuid(), null,
            AccountEnabled: true, GrantEnabled: true, AccountHouseholdId: Household),
        // An application that is itself an administrator has no person behind it, so it is no effective administrator.
        ["administrator-application"] = new(PrincipalKind.ServiceApplication, true, null, null, Guid.NewGuid(), null, null,
            ApplicationEnabled: true, ApplicationIsAdministrator: true),
        ["household-admin"] = new(PrincipalKind.Human, true, Account, Profile, null, Guid.NewGuid(), null,
            AccountEnabled: true, GrantEnabled: true, AccountHouseholdId: Household, AccountIsHouseholdAdmin: true),
    };

    [Theory]
    [InlineData("household-member")]
    [InlineData("household-admin")]
    [InlineData("administrator-application")]
    public async Task TheRoute_RefusesAProfileOrApplicationThatIsNotAServerAdministrator(string principal)
    {
        await using var app = BuildApplication();

        Assert.False(await PassesAsync(app, TheRoute(app), principal), $"{principal} must be refused {Route}.");
    }

    [Fact]
    public async Task TheRoute_ServesAServerAdministrator()
    {
        await using var app = BuildApplication();

        Assert.True(await PassesAsync(app, TheRoute(app), "server-admin"));
    }

    [Fact]
    public void TheRoute_IsAPost_NotAGet_SoTheFileListTravelsInTheBody()
    {
        var app = BuildApplication();

        var endpoint = TheRoute(app);

        Assert.Contains("POST", endpoint.Metadata.OfType<HttpMethodMetadata>().Single().HttpMethods);
    }

    private static RouteEndpoint TheRoute(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == Route);

    private static WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization(options => options.AddPolicy(
            AuthPolicies.Administrator,
            policy => policy.RequireAuthenticatedUser().AddRequirements(new EffectiveAdministratorRequirement(true))));
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new PrincipalClaimResolver());
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new AdministratorDecisions());
        builder.Services.AddScoped<IAuthorizationHandler, EffectiveAdministratorHandler>();
        // The other access routes are mapped too, so what they need to be built is registered empty; this test never calls them.
        builder.Services.AddScoped<ISelfServiceAuthorizationService>(_ => null!);
        builder.Services.AddScoped<IAccountRepository>(_ => null!);
        builder.Services.AddScoped<IIdentityRepository>(_ => null!);
        builder.Services.AddScoped<IProfileRepository>(_ => null!);
        builder.Services.AddScoped<IAccountExternalLoginService>(_ => null!);
        builder.Services.AddScoped<IAccountSignInMethodRepository>(_ => null!);
        builder.Services.AddScoped<IAuthorizationAuditWriter>(_ => null!);
        builder.Services.AddScoped<IGrantAdminUnlockService>(_ => null!);
        builder.Services.AddScoped<IAccountAccessMutationService>(_ => null!);
        builder.Services.AddScoped<IConfigurationLoader>(_ => null!);
        builder.Services.AddSingleton<AuthenticationPolicyMutationGate>();
        builder.Services.AddSingleton<AuthenticationProviderConfigurationService>();
        builder.Services.AddSingleton<UserManager<MediaEngine.Domain.Entities.Account>>(_ => null!);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IVisibilityAfterMoveReadService>(_ => null!);
        var app = builder.Build();
        app.MapAccountEndpoints();
        return app;
    }

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
