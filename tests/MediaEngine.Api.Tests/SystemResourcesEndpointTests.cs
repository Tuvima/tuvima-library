using System.Security.Claims;
using System.Text.Json;
using MediaEngine.AI.Infrastructure;
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

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Settings System summary reads <c>GET /system/resources</c>, which only server administrators (or an
/// application granted system status read) may call. Household administrators and members are refused.
/// </summary>
public sealed class SystemResourcesEndpointTests
{
    private const string Route = "/system/resources";
    private const string PrincipalClaim = "test-principal";

    private static readonly Guid Account = Guid.NewGuid();
    private static readonly Guid Profile = Guid.NewGuid();
    private static readonly Guid Household = Guid.NewGuid();
    private static readonly Guid NativeApp = BuiltInApplicationIds.NativeClient;
    private static readonly Guid StatusApp = Guid.NewGuid();
    private static readonly Guid PlainApp = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<string, RequestAuthority> Principals = new Dictionary<string, RequestAuthority>
    {
        ["server-admin"] = new(PrincipalKind.Human, true, Account, Profile, null, Guid.NewGuid(), null,
            AccountEnabled: true, GrantEnabled: true, AccountIsAdministrator: true, GrantAdminEnabled: true),
        ["household-member"] = new(PrincipalKind.Human, true, Account, Profile, null, Guid.NewGuid(), null,
            AccountEnabled: true, GrantEnabled: true, AccountHouseholdId: Household),
        ["household-admin"] = new(PrincipalKind.Human, true, Account, Profile, null, Guid.NewGuid(), null,
            AccountEnabled: true, GrantEnabled: true, AccountHouseholdId: Household, AccountIsHouseholdAdmin: true),
        // A paired native app for a signed-in administrator that was not granted system status read.
        ["native-app-without-status-read"] = new(PrincipalKind.DelegatedUserClient, true, Account, Profile, NativeApp, null, Guid.NewGuid(),
            AccountEnabled: true, GrantEnabled: true, ApplicationEnabled: true, AccountIsAdministrator: true, GrantAdminEnabled: true),
        // A paired native app that does hold the permission still acts for a person who is not an administrator.
        ["native-app-with-status-read"] = new(PrincipalKind.DelegatedUserClient, true, Account, Profile, StatusApp, null, Guid.NewGuid(),
            AccountEnabled: true, GrantEnabled: true, ApplicationEnabled: true),
        ["service-without-status-read"] = new(PrincipalKind.ServiceApplication, true, null, null, PlainApp,
            ApplicationEnabled: true),
        ["service-with-status-read"] = new(PrincipalKind.ServiceApplication, true, null, null, StatusApp,
            ApplicationEnabled: true),
    };

    [Fact]
    public async Task Route_IsRefusedToNonAdministrators_ExactlyLikeTheStartupReadiness()
    {
        await using var app = BuildApplication();

        var resources = RequirementOf(app, Route);
        var readiness = RequirementOf(app, "/system/readiness");

        Assert.Equal(ApplicationPermissionIds.SystemStatusRead, resources.Permission);
        Assert.False(resources.AllowHouseholdAdministrator);
        Assert.Equal(readiness.Permission, resources.Permission);
        Assert.Equal(readiness.AllowHouseholdAdministrator, resources.AllowHouseholdAdministrator);
    }

    [Theory]
    [InlineData("household-member")]
    [InlineData("household-admin")]
    [InlineData("native-app-without-status-read")]
    [InlineData("native-app-with-status-read")]
    [InlineData("service-without-status-read")]
    public async Task Route_IsRefusedToEveryoneButAServerAdministratorOrAStatusApplication(string principal)
    {
        await using var app = BuildApplication();

        Assert.False(await PassesAsync(app, principal), $"{principal} must not read the server's resources.");
    }

    [Theory]
    [InlineData("server-admin")]
    [InlineData("service-with-status-read")]
    public async Task Route_ServesAServerAdministratorAndAnApplicationGrantedSystemStatusRead(string principal)
    {
        await using var app = BuildApplication();

        Assert.True(await PassesAsync(app, principal), $"{principal} must read the server's resources.");
    }

    [Fact]
    public async Task Route_ReturnsTheReadingWithSnakeCaseNames()
    {
        await using var app = BuildApplication();

        var (status, body) = await GetAsync(app);

        Assert.Equal(StatusCodes.Status200OK, status);
        var root = JsonDocument.Parse(body).RootElement;
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("engine_version").GetString()));
        Assert.True(root.GetProperty("uptime_seconds").GetInt64() >= 0);
        Assert.Equal(12.5, root.GetProperty("processor_percent").GetDouble());
        Assert.Equal(700, root.GetProperty("memory_used_mb").GetInt64());
        Assert.Equal(16_000, root.GetProperty("memory_total_mb").GetInt64());
        var drive = Assert.Single(root.GetProperty("drives").EnumerateArray());
        Assert.Equal(@"C:\", drive.GetProperty("root").GetString());
        Assert.True(drive.GetProperty("holds_library").GetBoolean());
        Assert.True(drive.GetProperty("holds_data_store").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("data_store_size_bytes").ValueKind);
    }

    private static WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IRequestAuthorityResolver>(new PrincipalClaimResolver());
        builder.Services.AddSingleton<IAccountAccessDecisionService>(new AdministratorDecisions());
        builder.Services.AddSingleton<MediaEngine.Domain.Contracts.IAuthorizationEvaluator>(new StatusReadEvaluator(StatusApp));
        builder.Services.AddScoped<IAuthorizationHandler, AdministratorOrApplicationHandler>();
        builder.Services.AddSingleton(new SystemResourcesService(
            () => new ResourceSnapshot { CpuPressure = 0.125, EngineRamMb = 700, TotalRamMb = 16_000 },
            () => @"C:\Media",
            @"C:\Tuvima\library.db",
            () => DateTimeOffset.UtcNow.AddHours(-1),
            _ => new DriveReading(@"C:\", "System", 1_000, 400),
            TimeProvider.System));
        // The other system routes are mapped too; their services only need to be known so they bind as services.
        builder.Services.AddSingleton<IDatabaseConnection>(_ => null!);
        builder.Services.AddSingleton<DataStoreVersionRepository>(_ => null!);
        builder.Services.AddSingleton<IConfigurationLoader>(_ => null!);
        builder.Services.AddSingleton<StartupReadinessService>(_ => null!);
        builder.Services.AddSingleton<IScheduledTaskRegistry>(_ => null!);
        builder.Services.AddSingleton<IScheduledTaskRunRepository>(_ => null!);
        builder.Services.AddSingleton<IMediaOperationRepository>(_ => null!);
        builder.Services.AddSingleton<IFileWatcher>(_ => null!);
        builder.Services.AddSingleton<AssetStoreCleanupService>(_ => null!);
        builder.Services.AddSingleton<DatabaseBackupService>(_ => null!);
        var app = builder.Build();
        app.MapSystemEndpoints();
        return app;
    }

    private static RouteEndpoint EndpointOf(WebApplication app, string route) =>
        Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(), candidate =>
                candidate.RoutePattern.RawText == route &&
                candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("GET") == true);

    private static AdministratorOrApplicationRequirement RequirementOf(WebApplication app, string route) =>
        Assert.Single(EndpointOf(app, route).Metadata.OfType<AuthorizationPolicy>()
            .SelectMany(policy => policy.Requirements)
            .OfType<AdministratorOrApplicationRequirement>());

    // What the authorization middleware does for an endpoint: combine its policies, then evaluate them for the principal.
    private static async Task<bool> PassesAsync(WebApplication app, string principal)
    {
        var endpoint = EndpointOf(app, Route);
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

    private static async Task<(int Status, string Text)> GetAsync(WebApplication app)
    {
        var responseBody = new MemoryStream();
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            Request = { Method = "GET", Path = Route },
            Response = { Body = responseBody },
        };

        await EndpointOf(app, Route).RequestDelegate!(context);
        responseBody.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(responseBody).ReadToEndAsync());
    }

    private sealed class PrincipalClaimResolver : IRequestAuthorityResolver
    {
        public ValueTask<RequestAuthority> ResolveAsync(HttpContext context, CancellationToken ct = default) =>
            ValueTask.FromResult(Principals[context.User.FindFirstValue(PrincipalClaim)!]);
    }

    /// <summary>Allows the system status read permission only to the applications named; everything else is denied.</summary>
    private sealed class StatusReadEvaluator(params Guid[] applicationsWithStatusRead) : MediaEngine.Domain.Contracts.IAuthorizationEvaluator
    {
        public ValueTask<AuthorizationDecision> EvaluateAsync(RequestAuthority authority,
            AuthorizationRequirement requirement, ResourceAuthorizationContext? resource,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                requirement.ApplicationPermission == ApplicationPermissionIds.SystemStatusRead
                    && authority.ApplicationId is { } application
                    && applicationsWithStatusRead.Contains(application)
                    ? AuthorizationDecision.Allow()
                    : AuthorizationDecision.Deny(AuthorizationDenialReason.AdministratorRequired));
    }
}
