using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Identity.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

public sealed class AccountEndpointContractTests
{
    [Fact]
    public void SelfServiceAuthenticationMethods_ReflectOnlyAttachedCredentials()
    {
        Assert.Equal(["password"], AccountEndpoints.AttachedAuthenticationMethods(
            true, false, false));
        Assert.Equal(["passkey", "external"], AccountEndpoints.AttachedAuthenticationMethods(
            false, true, true));
        Assert.Equal(["password", "passkey", "external"], AccountEndpoints.AttachedAuthenticationMethods(
            true, true, true));
        Assert.Empty(AccountEndpoints.AttachedAuthenticationMethods(false, false, false));
    }

    [Fact]
    public async Task AccessManagementRoutes_UseTypedReadWriteApplicationAdmission()
    {
        var builder = WebApplication.CreateBuilder();
        AddEndpointServices(builder.Services);
        await using var app = builder.Build();
        app.MapAccountEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        AssertPolicy(endpoints, "/access/accounts/", "GET", ApplicationPermissionIds.IdentityUsersRead);
        AssertPolicy(endpoints, "/access/accounts/", "POST", ApplicationPermissionIds.IdentityUsersWrite);
        AssertPolicy(endpoints, "/access/accounts/{accountId:guid}", "DELETE", ApplicationPermissionIds.IdentityUsersWrite);
        AssertPolicy(endpoints, "/access/libraries", "GET", ApplicationPermissionIds.IdentityUsersRead);
        AssertPolicy(endpoints, "/access/profiles/", "POST", ApplicationPermissionIds.IdentityUsersWrite);
    }

    [Fact]
    public async Task ExternalLoginBinding_HasNoCallerSuppliedPostRoute()
    {
        var builder = WebApplication.CreateBuilder();
        AddEndpointServices(builder.Services);
        await using var app = builder.Build();
        app.MapAccountEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith(
                "/access/self-service/external-logins", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Contains(endpoints, endpoint => Methods(endpoint).Contains("GET"));
        Assert.Contains(endpoints, endpoint => Methods(endpoint).Contains("DELETE"));
        Assert.DoesNotContain(endpoints, endpoint => Methods(endpoint).Contains("POST"));
    }

    [Fact]
    public async Task ManagedDeviceRoutes_AreSelfServiceSoEveryoneSeesOnlyWhatTheyMay()
    {
        var builder = WebApplication.CreateBuilder();
        AddEndpointServices(builder.Services);
        await using var app = builder.Build();
        app.MapAccountEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        foreach (var (route, method) in new[] { ("/access/devices/", "GET"), ("/access/devices/{deviceId:guid}", "DELETE") })
        {
            var endpoint = Assert.Single(endpoints, candidate =>
                candidate.RoutePattern.RawText == route && Methods(candidate).Contains(method));
            Assert.Contains(endpoint.Metadata.OfType<AuthorityRequirementMetadata>(),
                metadata => metadata.Requirement == "human_self_service");
        }
    }

    private static void AssertPolicy(RouteEndpoint[] endpoints, string route, string method,
        ApplicationPermissionId permission)
    {
        var endpoint = Assert.Single(endpoints, candidate =>
            candidate.RoutePattern.RawText == route && Methods(candidate).Contains(method));
        var policy = Assert.IsType<AuthorizationPolicy>(
            endpoint.Metadata.Single(metadata => metadata is AuthorizationPolicy));
        Assert.Equal(permission,
            Assert.Single(policy.Requirements.OfType<AdministratorOrApplicationRequirement>()).Permission);
    }

    private static IReadOnlyList<string> Methods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];

    private static void AddEndpointServices(IServiceCollection services)
    {
        services.AddAuthorization();
        services.AddScoped<IRequestAuthorityResolver>(_ => null!);
        services.AddScoped<ISelfServiceAuthorizationService>(_ => null!);
        services.AddScoped<IAccountRepository>(_ => null!);
        services.AddScoped<IIdentityRepository>(_ => null!);
        services.AddScoped<IProfileRepository>(_ => null!);
        services.AddScoped<IAccountExternalLoginService>(_ => null!);
        services.AddScoped<IAccountSignInMethodRepository>(_ => null!);
        services.AddScoped<IAuthorizationAuditWriter>(_ => null!);
        services.AddScoped<IGrantAdminUnlockService>(_ => null!);
        services.AddScoped<IAccountAccessMutationService>(_ => null!);
        services.AddScoped<IConfigurationLoader>(_ => null!);
        services.AddSingleton<AuthenticationPolicyMutationGate>();
        services.AddSingleton<AuthenticationProviderConfigurationService>();
        services.AddSingleton<UserManager<MediaEngine.Domain.Entities.Account>>(_ => null!);
        services.AddSingleton(TimeProvider.System);
    }
}
