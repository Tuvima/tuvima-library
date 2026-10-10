using MediaEngine.Api.Endpoints;
using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Tests;

public sealed class ViewEndpointRouteTests
{
    [Theory]
    [InlineData(ViewScopeKind.Mine)]
    [InlineData(ViewScopeKind.Shared)]
    public void PreferenceScopeProfileId_IsOnlyPersistedForExplicitProfileScope(ViewScopeKind scope)
    {
        Assert.Null(ViewEndpoints.PreferenceScopeProfileId(scope, Guid.NewGuid()));
    }

    [Fact]
    public void OtherPeopleListIsServerAdministratorOnlyAndOpeningIsRecordedAtTheBoundary()
    {
        var root = FindRepoRoot();
        var endpoint = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Api", "Endpoints", "ViewEndpoints.cs"));
        var routeStart = endpoint.IndexOf("group.MapGet(\"/other-people\"", StringComparison.Ordinal);
        var routeEnd = endpoint.IndexOf("group.MapGet(\"/photo-views\"", routeStart, StringComparison.Ordinal);
        var route = endpoint[routeStart..routeEnd];

        // Household administrators, members and apps are refused (403) both by the policy and by the handler.
        Assert.Contains(".RequireEffectiveAdministrator()", route, StringComparison.Ordinal);
        Assert.Contains("PrincipalKind.Human", route, StringComparison.Ordinal);
        Assert.Contains("IsEffectiveAdministrator", route, StringComparison.Ordinal);
        Assert.Contains("ApiErrors.Forbidden", route, StringComparison.Ordinal);

        // Every read of another household goes through the one authorization boundary, which records it.
        var authorization = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Api", "Services", "View", "ViewResourceAuthorization.cs"));
        Assert.Contains("otherPeople.RecordOpenAsync", authorization, StringComparison.Ordinal);
    }

    [Fact]
    public void PreferenceScopeProfileId_PreservesExplicitProfileTarget()
    {
        var profileId = Guid.NewGuid();

        Assert.Equal(profileId,
            ViewEndpoints.PreferenceScopeProfileId(ViewScopeKind.Profile, profileId));
    }

    [Fact]
    public void ScopeEndpointAuthorizesBeforeProvisioningAndResolvesAfterProvisioning()
    {
        var endpoint = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "MediaEngine.Api", "Endpoints", "ViewEndpoints.cs"));
        var routeStart = endpoint.IndexOf("group.MapGet(\"/scopes\"", StringComparison.Ordinal);
        var routeEnd = endpoint.IndexOf("group.MapGet(\"/preferences\"", routeStart, StringComparison.Ordinal);
        var route = endpoint[routeStart..routeEnd];

        var permission = route.IndexOf("AuthorizePersonalScopeBootstrapAsync", StringComparison.Ordinal);
        var provision = route.IndexOf("EnsurePersonalSpaceAsync", StringComparison.Ordinal);
        var resolution = route.LastIndexOf("AuthorizeAsync", StringComparison.Ordinal);

        Assert.True(permission >= 0);
        Assert.True(provision > permission);
        Assert.True(resolution > provision);
    }

    [Fact]
    public void EngineMapsOnlyTheViewLocalAssetSurface()
    {
        var root = FindRepoRoot();
        var routes = File.ReadAllText(Path.Combine(
            root, "src", "MediaEngine.Api", "DependencyInjection", "ApiEndpointRouteBuilderExtensions.cs"));
        var hosted = File.ReadAllText(Path.Combine(
            root, "src", "MediaEngine.Api", "DependencyInjection", "TuvimaHostedServiceCollectionExtensions.cs"));
        var endpointPath = Path.Combine(
            root, "src", "MediaEngine.Api", "Endpoints", "ViewEndpoints.cs");
        var endpoint = File.ReadAllText(endpointPath);

        Assert.Contains("app.MapViewEndpoints();", routes, StringComparison.Ordinal);
        Assert.DoesNotContain("MapPhotoEndpoints", routes, StringComparison.Ordinal);
        Assert.DoesNotContain("PhotoLibraryIndexHostedService", hosted, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            root, "src", "MediaEngine.Api", "Endpoints", "PhotoEndpoints.cs")));
        Assert.Contains("MapGroup(\"/view\")", endpoint, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/scopes\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/assets\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/assets/timeline-index\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("MapPost(\"/uploads\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("/items/{id:guid}/content", endpoint, StringComparison.Ordinal);
        Assert.Contains("/items/{id:guid}/thumbnail", endpoint, StringComparison.Ordinal);
        Assert.Contains("/items/{id:guid}/preview", endpoint, StringComparison.Ordinal);
        Assert.Contains("/items/{id:guid}/description", endpoint, StringComparison.Ordinal);
        Assert.Contains("/items/{id:guid}/tags", endpoint, StringComparison.Ordinal);
        Assert.Contains("/items/{id:guid}/location", endpoint, StringComparison.Ordinal);
        Assert.Contains("/galleries", endpoint, StringComparison.Ordinal);
        Assert.Contains("[FromBody] ViewGalleryItemsRequest request", endpoint, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/share-targets\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("GetGalleryShareTargetsAsync", endpoint, StringComparison.Ordinal);
        Assert.Contains("TryValidateGalleryShares", endpoint, StringComparison.Ordinal);
        Assert.Contains("/admin/profiles/{profileId:guid}/sources", endpoint, StringComparison.Ordinal);
        Assert.Contains("/admin/profiles/{profileId:guid}/reconcile", endpoint, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/admin/shared/sources\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("MapPost(\"/admin/shared/sources\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("MapPut(\"/admin/shared/sources/{sourceId:guid}\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("MapDelete(\"/admin/shared/sources/{sourceId:guid}\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("MapPost(\"/admin/shared/reconcile\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("IViewSharedLibraryRepository shared", endpoint, StringComparison.Ordinal);
        var sourcesRoute = endpoint.IndexOf("/admin/profiles/{profileId:guid}/sources", StringComparison.Ordinal);
        var scanRoute = endpoint.IndexOf("/admin/profiles/{profileId:guid}/reconcile", sourcesRoute, StringComparison.Ordinal);
        Assert.True(sourcesRoute >= 0 && scanRoute > sourcesRoute);
        Assert.Contains(".RequireEffectiveAdministrator();", endpoint[sourcesRoute..scanRoute], StringComparison.Ordinal);
        Assert.DoesNotContain("RequireAnyRole", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("RequireAdmin", endpoint, StringComparison.Ordinal);
        Assert.Contains("new ViewPersonalSpaceAdminReviewDto(profileId, null, [], [])", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientDeviceId", endpoint[sourcesRoute..scanRoute], StringComparison.Ordinal);
        Assert.DoesNotContain("Guid? profileId, HttpContext", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("/admin/libraries/", endpoint, StringComparison.Ordinal);
        Assert.Contains("Guid? scopeProfileId", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("MapGet(\"/libraries\"", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("MapGet(\"/{libraryId:guid}\"", endpoint, StringComparison.Ordinal);
        Assert.Contains("IViewRequestProfileContext", endpoint, StringComparison.Ordinal);
        Assert.Contains("IViewResourceAuthorizationService", endpoint, StringComparison.Ordinal);
        Assert.Contains(": ViewScopeRequest.Shared;", endpoint, StringComparison.Ordinal);
        Assert.Contains("AuthorizeOwnedItemAsync", endpoint, StringComparison.Ordinal);
        Assert.Contains("ViewThumbnailService thumbnails", endpoint, StringComparison.Ordinal);
        Assert.Contains("thumbnails.GetOrCreateAsync", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void AdministratorSourceRoutesRequireTheUnlockedAdministratorPolicy()
    {
        var endpoint = File.ReadAllText(FindRepoRoot() +
            @"/src/MediaEngine.Api/Endpoints/ViewEndpoints.cs");
        var extensions = File.ReadAllText(FindRepoRoot() +
            @"/src/MediaEngine.Api/Security/RequestAuthorityServices.cs");

        Assert.Equal(10, Count(endpoint, ".RequireEffectiveAdministrator();"));
        Assert.Contains("bool surfaceUnlock = true", extensions, StringComparison.Ordinal);
        Assert.Contains("surfaceUnlock ? AuthPolicies.Administrator", extensions, StringComparison.Ordinal);
        Assert.Contains("ViewResourceKind.FolderPolicy", endpoint, StringComparison.Ordinal);
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0;
             index += value.Length)
        {
            count++;
        }

        return count;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
