using MediaEngine.Api.DependencyInjection;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Security;
using MediaEngine.Api.Services.Plugins;
using MediaEngine.Domain.Authorization;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Services;
using MediaEngine.Storage.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaEngine.Api.Tests;

public sealed class CatalogueEndpointPolicyTests
{
    [Fact]
    public async Task CollectionAndPersonRoutesUseTypedPoliciesAndResourceMetadata()
    {
        await using var app = BuildApplication();
        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        Require(routes, "/collections", HttpMethods.Get)
            .HasApplicationPermission(ApplicationPermissionIds.LibraryRead);
        Require(routes, "/collections/{id:guid}/items", HttpMethods.Get)
            .HasApplicationPermission(ApplicationPermissionIds.LibraryRead)
            .HasEntityResource(ApplicationPermissionIds.LibraryRead);
        Require(routes, "/collections/{id:guid}/items", HttpMethods.Post)
            .HasAdministratorOrApplication(ApplicationPermissionIds.CollectionsWrite);
        Require(routes, "/persons/{id:guid}", HttpMethods.Get)
            .HasApplicationPermission(ApplicationPermissionIds.LibraryRead)
            .HasEntityResource(ApplicationPermissionIds.LibraryRead);
        Require(routes, "/persons/{id:guid}/headshot", HttpMethods.Get)
            .HasApplicationPermission(ApplicationPermissionIds.ArtworkRead)
            .HasEntityResource(ApplicationPermissionIds.ArtworkRead);
        Require(routes, "/persons/{id:guid}/editor", HttpMethods.Put)
            .HasAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)
            .HasEntityResource(ApplicationPermissionIds.MetadataWrite);
        Require(routes, "/playback/{assetId:guid}/segments", HttpMethods.Get)
            .HasApplicationPermission(ApplicationPermissionIds.PlaybackRead)
            .HasAssetResource(ApplicationPermissionIds.PlaybackRead);
        Require(routes, "/playback/{assetId:guid}/segments/{segmentId:guid}", HttpMethods.Put)
            .HasAdministratorOrApplication(ApplicationPermissionIds.PlaybackWrite)
            .HasAssetResource(ApplicationPermissionIds.PlaybackWrite);
        Require(routes, "/timeline/{entityId:guid}", HttpMethods.Get)
            .HasApplicationPermission(ApplicationPermissionIds.MetadataEnrichmentRead)
            .HasAnyEntityResource(ApplicationPermissionIds.MetadataEnrichmentRead);
        Require(routes, "/timeline/{entityId:guid}/rematch", HttpMethods.Post)
            .HasAdministratorOrApplication(ApplicationPermissionIds.MetadataMatch)
            .HasAssetResource(ApplicationPermissionIds.MetadataMatch);
        Require(routes, "/library/portraits/{portraitId:guid}", HttpMethods.Get)
            .HasApplicationPermission(ApplicationPermissionIds.ArtworkRead)
            .HasCharacterPortraitResource(ApplicationPermissionIds.ArtworkRead);
        Require(routes, "/library/assets/{entityId}", HttpMethods.Get)
            .HasApplicationPermission(ApplicationPermissionIds.MetadataRead)
            .HasEntityAssetContainerResource(ApplicationPermissionIds.MetadataRead);
        Require(routes, "/library/enrichment/universe/trigger", HttpMethods.Post)
            .HasAdministratorOrApplication(ApplicationPermissionIds.MetadataEnrichmentRun);

        Assert.DoesNotContain(routes.SelectMany(route => route.Metadata.OfType<AuthorityRequirementMetadata>()),
            metadata => metadata.Requirement is "authenticated" or "enabled_human");
    }

    private static WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.Services.AddAuthorization();
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<IDatabaseConnection>(_ =>
            throw new InvalidOperationException("Endpoint inventory must not resolve storage."));
        builder.Services.AddTuvimaStorage();
        builder.Services.AddTuvimaDisplay();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IRequestAuthorityResolver, RequestAuthorityResolver>();
        builder.Services.AddScoped<CatalogueResourceAuthorizationService>();
        builder.Services.AddSingleton<IHydrationPipelineService>(_ =>
            throw new InvalidOperationException("Endpoint inventory must not resolve the pipeline."));
        builder.Services.AddSingleton<PluginSegmentDetectionService>();
        builder.Services.AddSingleton<AssetPathService>(_ =>
            throw new InvalidOperationException("Endpoint inventory must not resolve asset paths."));
        builder.Services.AddSingleton<IMetadataHarvestingService>(_ =>
            throw new InvalidOperationException("Endpoint inventory must not resolve harvesting."));
        var app = builder.Build();
        app.MapCollectionEndpoints();
        app.MapPersonEndpoints();
        app.MapPlaybackSegmentEndpoints();
        app.MapTimelineEndpoints();
        app.MapCharacterEndpoints();
        return app;
    }

    private static RouteAssertion Require(
        IEnumerable<RouteEndpoint> routes,
        string pattern,
        string method) => new(Assert.Single(routes, route =>
        (route.RoutePattern.RawText ?? string.Empty).TrimEnd('/') == pattern
        && (route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
            .Contains(method, StringComparer.OrdinalIgnoreCase)));

    private sealed class RouteAssertion(RouteEndpoint endpoint)
    {
        public RouteAssertion HasApplicationPermission(ApplicationPermissionId permission)
        {
            Assert.Contains(endpoint.Metadata.OfType<ClientScopeRequirementMetadata>(),
                metadata => metadata.Scope == permission.Value);
            return this;
        }

        public RouteAssertion HasAdministratorOrApplication(ApplicationPermissionId permission)
        {
            var requirements = endpoint.Metadata.OfType<AuthorizationPolicy>()
                .SelectMany(policy => policy.Requirements)
                .OfType<AdministratorOrApplicationRequirement>();
            Assert.Contains(requirements, requirement => requirement.Permission == permission);
            return this;
        }

        public RouteAssertion HasEntityResource(ApplicationPermissionId permission)
        {
            Assert.Contains(endpoint.Metadata.OfType<CatalogueEntityAccessMetadata>(),
                metadata => metadata.Permission == permission.Value);
            return this;
        }

        public RouteAssertion HasAssetResource(ApplicationPermissionId permission)
        {
            Assert.Contains(endpoint.Metadata.OfType<CatalogueAssetAccessMetadata>(),
                metadata => metadata.Permission == permission.Value);
            return this;
        }

        public RouteAssertion HasAnyEntityResource(ApplicationPermissionId permission)
        {
            Assert.Contains(endpoint.Metadata.OfType<CatalogueAnyEntityAccessMetadata>(),
                metadata => metadata.Permission == permission.Value);
            return this;
        }

        public RouteAssertion HasQidResource(ApplicationPermissionId permission)
        {
            Assert.Contains(endpoint.Metadata.OfType<CatalogueQidAccessMetadata>(),
                metadata => metadata.Permission == permission.Value);
            return this;
        }

        public RouteAssertion HasCharacterPortraitResource(ApplicationPermissionId permission)
        {
            Assert.Contains(endpoint.Metadata.OfType<CatalogueCharacterPortraitAccessMetadata>(),
                metadata => metadata.Permission == permission.Value);
            return this;
        }

        public RouteAssertion HasEntityAssetContainerResource(ApplicationPermissionId permission)
        {
            Assert.Contains(endpoint.Metadata.OfType<CatalogueEntityAssetContainerAccessMetadata>(),
                metadata => metadata.Permission == permission.Value);
            return this;
        }
    }
}
