using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Tests authorization metadata ASP.NET executes, including group inheritance.
/// The complete production inventory and exact public exceptions are checked by
/// MappedEndpointInventoryTests, replacing the obsolete role-based source scan.
/// </summary>
public sealed class RouteAuthorizationGuardrailTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdministratorSurface_RequiresUnlock_WhileEligibilityDoesNot(bool grouped)
    {
        await using var app = BuildApplication();
        if (grouped)
        {
            app.MapGroup("/surface").RequireEffectiveAdministrator().MapGet("/", () => "ok");
            app.MapGroup("/eligible").RequireEffectiveAdministrator(surfaceUnlock: false).MapGet("/", () => "ok");
        }
        else
        {
            app.MapGet("/surface", () => "ok").RequireEffectiveAdministrator();
            app.MapGet("/eligible", () => "ok").RequireEffectiveAdministrator(surfaceUnlock: false);
        }

        Assert.Equal(AuthPolicies.Administrator, Assert.Single(Find(app, "/surface").Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
        Assert.Equal(AuthPolicies.AdministratorEligibility, Assert.Single(Find(app, "/eligible").Metadata.GetOrderedMetadata<IAuthorizeData>()).Policy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplicationServicePolicies_RequireAuthenticationAndTheExactOperation(bool grouped)
    {
        await using var app = BuildApplication();
        if (grouped)
        {
            app.MapGroup("/administration").RequireAdministratorOrApplication(ApplicationPermissionIds.BackupRestore)
                .MapPost("/", () => "ok");
            app.MapGroup("/human-or-application").RequireHumanOrApplicationPermission(ApplicationPermissionIds.SystemActivityRead)
                .MapGet("/", () => "ok");
        }
        else
        {
            app.MapPost("/administration", () => "ok").RequireAdministratorOrApplication(ApplicationPermissionIds.BackupRestore);
            app.MapGet("/human-or-application", () => "ok").RequireHumanOrApplicationPermission(ApplicationPermissionIds.SystemActivityRead);
        }
        app.MapGet("/application-only", () => "ok").RequireApplicationPermission(ApplicationPermissionIds.LibraryRead);

        var administration = RequiredPolicy(Find(app, "/administration"));
        Assert.Equal(ApplicationPermissionIds.BackupRestore,
            Assert.Single(administration.Requirements.OfType<AdministratorOrApplicationRequirement>()).Permission);
        var humanOrApplication = RequiredPolicy(Find(app, "/human-or-application"));
        Assert.Equal(ApplicationPermissionIds.SystemActivityRead,
            Assert.Single(humanOrApplication.Requirements.OfType<HumanOrApplicationPermissionRequirement>()).Permission);
        var applicationOnly = RequiredPolicy(Find(app, "/application-only"));
        Assert.Equal(ApplicationPermissionIds.LibraryRead,
            Assert.Single(applicationOnly.Requirements.OfType<ApplicationPermissionRequirement>()).Permission);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InPersonAdministratorRoutes_RequireSurfaceUnlockAndAHumanCaller(bool grouped)
    {
        await using var app = BuildApplication();
        if (grouped)
        {
            app.MapGroup("/in-person").RequireEffectiveAdministratorInPerson().MapPost("/", () => "ok");
        }
        else
        {
            app.MapPost("/in-person", () => "ok").RequireEffectiveAdministratorInPerson();
        }

        var policy = RequiredPolicy(Find(app, "/in-person"));
        var requirement = Assert.Single(policy.Requirements.OfType<EffectiveAdministratorRequirement>());
        Assert.True(requirement.SurfaceUnlock);
        Assert.True(requirement.HumanOnly);
        Assert.False(requirement.AllowHouseholdAdministrator);
        Assert.DoesNotContain(policy.Requirements, item => item is AdministratorOrApplicationRequirement or ApplicationPermissionRequirement);
    }

    private static AuthorizationPolicy RequiredPolicy(RouteEndpoint endpoint)
    {
        Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        var policy = Assert.Single(endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
        Assert.Single(policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>());
        Assert.DoesNotContain(policy.Requirements, requirement => requirement is RolesAuthorizationRequirement);
        return policy;
    }

    private static WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        return builder.Build();
    }

    private static RouteEndpoint Find(WebApplication app, string path) => Assert.Single(
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
        endpoint => endpoint.RoutePattern.RawText?.TrimEnd('/') == path);
}
