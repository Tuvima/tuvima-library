using System.Security.Claims;
using System.Text.Json;
using MediaEngine.Api.DependencyInjection;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services;
using MediaEngine.Api.Services.ScheduledTasks;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Ingestion.Contracts;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Settings Developer Tools sub-pages (Enrichment Tester, Development data) are offered only when the Engine says it
/// maps the development-only actions. <c>GET /system/development-tools</c> says so to administrators, and shares one
/// helper with the code that maps those actions so the answer cannot drift from what is really available.
/// </summary>
public sealed class DevelopmentToolsEndpointTests
{
    private const string Route = "/system/development-tools";
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
    public async Task Route_IsRefusedToEveryoneButAServerAdministrator(string principal)
    {
        await using var app = BuildApplication(Environments.Development);

        Assert.False(await PassesAsync(app, principal), $"{principal} must not read which development pages are offered.");
    }

    [Fact]
    public async Task Route_ServesAServerAdministrator_WithoutNeedingTheAdministratorUnlock()
    {
        var decisions = new AdministratorDecisions();
        await using var app = BuildApplication(Environments.Development, decisions);

        // The Dashboard asks as soon as Settings opens, before any PIN unlock, so the route checks eligibility only.
        Assert.True(await PassesAsync(app, "server-admin"));
        Assert.False(decisions.LastRequiredSurfaceUnlock);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Answer_MatchesExactlyWhatTheMappingHelperOffersForTheHostEnvironment(string environmentName)
    {
        await using var app = BuildApplication(environmentName);
        var offered = app.Environment.OffersDevelopmentEndpoints();

        var root = await GetAsync(app);

        Assert.Equal(offered, root.GetProperty("enrichment_tester").GetBoolean());
        Assert.Equal(offered, root.GetProperty("development_data").GetBoolean());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task NonDevelopmentEngine_OffersNeitherPage(string environmentName)
    {
        await using var app = BuildApplication(environmentName);

        var root = await GetAsync(app);

        Assert.False(root.GetProperty("enrichment_tester").GetBoolean());
        Assert.False(root.GetProperty("development_data").GetBoolean());
    }

    [Fact]
    public async Task DevelopmentEnvironment_OffersBothPages_OnlyInADebugBuild()
    {
        await using var app = BuildApplication(Environments.Development);

        var root = await GetAsync(app);

#if DEBUG
        const bool expected = true;
#else
        const bool expected = false;
#endif
        Assert.Equal(expected, root.GetProperty("enrichment_tester").GetBoolean());
        Assert.Equal(expected, root.GetProperty("development_data").GetBoolean());
    }

    [Fact]
    public void MappingDevelopmentEndpoints_AndTheAnswerShareOneHelper_SoTheyCannotDrift()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "MediaEngine.Api", "DependencyInjection", "ApiEndpointRouteBuilderExtensions.cs"));
        var mapping = source[source.IndexOf("public static WebApplication MapDevelopmentEngineEndpoints", StringComparison.Ordinal)..];

        Assert.Contains("app.Environment.OffersDevelopmentEndpoints()", mapping, StringComparison.Ordinal);
        Assert.DoesNotContain("IsDevelopment()", mapping, StringComparison.Ordinal);
    }

    private static WebApplication BuildApplication(string environmentName, AdministratorDecisions? decisions = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environmentName });
        builder.Services.AddAuthorization(options => options.AddPolicy(
            AuthPolicies.AdministratorEligibility,
            policy => policy.RequireAuthenticatedUser().AddRequirements(new EffectiveAdministratorRequirement(false))));
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new PrincipalClaimResolver());
        builder.Services.AddSingleton<IAccountAccessDecisionService>(decisions ?? new AdministratorDecisions());
        builder.Services.AddScoped<IAuthorizationHandler, EffectiveAdministratorHandler>();
        // The other system routes are mapped too; their services only need to be known so they bind as services.
        builder.Services.AddSingleton<IConfigurationLoader>(_ => null!);
        builder.Services.AddSingleton<StartupReadinessService>(_ => null!);
        builder.Services.AddSingleton<IScheduledTaskRegistry>(_ => null!);
        builder.Services.AddSingleton<IScheduledTaskRunRepository>(_ => null!);
        builder.Services.AddSingleton<IMediaOperationRepository>(_ => null!);
        builder.Services.AddSingleton<IFileWatcher>(_ => null!);
        builder.Services.AddSingleton<AssetStoreCleanupService>(_ => null!);
        builder.Services.AddSingleton<DatabaseBackupService>(_ => null!);
        builder.Services.AddSingleton<SystemResourcesService>(_ => null!);
        builder.Services.AddSingleton<DataStoreVersionRepository>(_ => null!);
        var app = builder.Build();
        app.MapSystemEndpoints();
        return app;
    }

    private static RouteEndpoint EndpointOf(WebApplication app) =>
        Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(), candidate =>
                candidate.RoutePattern.RawText == Route &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);

    // What the authorization middleware does for an endpoint: combine its policies, then evaluate them for the principal.
    private static async Task<bool> PassesAsync(WebApplication app, string principal)
    {
        var endpoint = EndpointOf(app);
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

    private static async Task<JsonElement> GetAsync(WebApplication app)
    {
        var responseBody = new MemoryStream();
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            Request = { Method = "GET", Path = Route },
            Response = { Body = responseBody },
        };

        await EndpointOf(app).RequestDelegate!(context);
        responseBody.Position = 0;
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        return JsonDocument.Parse(await new StreamReader(responseBody).ReadToEndAsync()).RootElement.Clone();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root could not be found.");
    }

    private sealed class PrincipalClaimResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(Principals[context.User.FindFirstValue(PrincipalClaim)!]);
    }
}
