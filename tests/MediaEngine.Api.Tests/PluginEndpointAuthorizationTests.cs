using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

public sealed class PluginEndpointAuthorizationTests
{
    [Fact]
    public async Task PluginAndLoreRoutes_UseExactPermissionsWithoutLegacyApplicationDiscoveryBypass()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<PluginCatalog>(_ => null!);
        builder.Services.AddSingleton<PluginDirectoryService>(_ => null!);
        builder.Services.AddSingleton<IPluginExecutionContextFactory>(_ => null!);
        builder.Services.AddSingleton<IMediaOperationRepository>(_ => null!);
        builder.Services.AddSingleton<PluginScheduledSegmentService>(_ => null!);
        builder.Services.AddSingleton<PluginUniverseLoreService>(_ => null!);
        await using var app = builder.Build();
        app.MapPluginEndpoints();
        app.MapUniverseLoreEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        var expected = new (string Route, string Method, ApplicationPermissionId Permission)[]
        {
            ("/plugins/", "GET", ApplicationPermissionIds.PluginsRead),
            ("/plugins/directory", "GET", ApplicationPermissionIds.PluginsRead),
            ("/plugins/{pluginId}", "GET", ApplicationPermissionIds.PluginsRead),
            ("/plugins/{pluginId}/removal-impact", "GET", ApplicationPermissionIds.PluginsRead),
            ("/plugins/{pluginId}/enable", "POST", ApplicationPermissionIds.PluginsManage),
            ("/plugins/{pluginId}/disable", "POST", ApplicationPermissionIds.PluginsManage),
            ("/plugins/{pluginId}/ai-steps/{stepKey}/run", "POST", ApplicationPermissionIds.PluginsManage),
            ("/plugins/{pluginId}/service/lookup", "POST", ApplicationPermissionIds.PluginsManage),
            ("/plugins/{pluginId}/settings", "PUT", ApplicationPermissionIds.PluginsManage),
            ("/plugins/{pluginId}/manifest", "GET", ApplicationPermissionIds.PluginsRead),
            ("/plugins/{pluginId}/manifest", "PUT", ApplicationPermissionIds.PluginsManage),
            ("/plugins/{pluginId}", "DELETE", ApplicationPermissionIds.PluginsManage),
            ("/plugins/{pluginId}/health", "POST", ApplicationPermissionIds.PluginsRead),
            ("/plugins/{pluginId}/jobs", "GET", ApplicationPermissionIds.PluginsJobsRead),
            ("/plugins/jobs/segment-detection/run", "POST", ApplicationPermissionIds.PluginsJobsRun),
            ("/universe/{qid}/lore-sources", "GET", ApplicationPermissionIds.PluginsRead),
            ("/universe/{qid}/lore-sources/manual", "POST", ApplicationPermissionIds.PluginsManage),
            ("/universe/{qid}/lore-sources/{sourceId:guid}/approve", "POST", ApplicationPermissionIds.PluginsManage),
            ("/universe/{qid}/lore-sources/{sourceId:guid}/reject", "POST", ApplicationPermissionIds.PluginsManage),
            ("/universe/{qid}/lore/enrich", "POST", ApplicationPermissionIds.PluginsJobsRun),
        };

        // +1 for the legacy lore discovery route, +3 for the plugin secrets routes (administrator-only; see PluginSecretsTests) and +5 for the plugin directory actions (administrator-only, install included; see PluginDirectoryEndpointTests) and +1 for Upload plugin (in-person administrator only; see MappedEndpointInventoryTests) and +2 for a plugin's Activity list and the People-action log (see PluginActivityEndpointTests) and +2 for a plugin's library limit (see PluginLibraryLimitTests).
        Assert.Equal(expected.Length + 1 + 3 + 5 + 1 + 2 + 2, endpoints.Length);
        foreach (var route in expected)
        {
            AssertPolicy(endpoints, route.Route, route.Method, route.Permission);
        }

        var legacyDiscovery = Assert.Single(endpoints, candidate =>
            candidate.RoutePattern.RawText == "/universe/{qid}/lore-sources/discover" &&
            candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);
        Assert.Contains(legacyDiscovery.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => metadata.Policy == AuthPolicies.Administrator);
    }

    private static void AssertPolicy(RouteEndpoint[] endpoints, string route, string method, ApplicationPermissionId permission)
    {
        var endpoint = Assert.Single(endpoints, candidate =>
            candidate.RoutePattern.RawText == route &&
            candidate.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
        var policy = Assert.IsType<AuthorizationPolicy>(endpoint.Metadata.Single(metadata => metadata is AuthorizationPolicy));
        Assert.Equal(permission, Assert.Single(policy.Requirements.OfType<AdministratorOrApplicationRequirement>()).Permission);
    }
}
