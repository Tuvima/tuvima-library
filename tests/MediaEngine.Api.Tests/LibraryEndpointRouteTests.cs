using System.IO;

namespace MediaEngine.Api.Tests;

public sealed class LibraryEndpointRouteTests
{
    [Fact]
    public void LibraryEndpoints_ExposeWorkFeedForHomePage()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs"));

        Assert.Contains("group.MapGet(\"/works\", async (", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"GetLibraryWorks\")", source, StringComparison.Ordinal);
        Assert.Contains(".Produces<PagedResponse<LibraryWorkListItemDto>>(StatusCodes.Status200OK)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryOverview_DelegatesOperationalAggregatesToReadService()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs"));
        var serviceSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/ReadServices/LibraryOverviewReadService.cs"));

        Assert.Contains("ILibraryOverviewReadService overviewReadService", source, StringComparison.Ordinal);
        Assert.Contains("overviewReadService.GetOverviewAggregatesAsync(ct)", source, StringComparison.Ordinal);
        Assert.Contains("RecentlyAddedSql", serviceSource, StringComparison.Ordinal);
        Assert.Contains("FROM identity_jobs", serviceSource, StringComparison.Ordinal);
        Assert.Contains("PipelineSuccessRate = overview.PipelineSuccessRate", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryEndpoints_HomeFeedUsesSharedVisibilityPredicateAndRichArtworkFields()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs"));
        var serviceSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/ReadServices/LibraryWorkFeedReadService.cs"));

        Assert.Contains("ILibraryWorkFeedReadService workFeedReadService", source, StringComparison.Ordinal);
        Assert.Contains("workFeedReadService.GetWorksAsync(page, ct, allowedWorkIds)", source, StringComparison.Ordinal);
        Assert.Contains("display.LoadWorksAsync(ct)", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".RequireAdminOrStandardUser()", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".RequireAnyRole()", source, StringComparison.Ordinal);
        Assert.Contains("HomeVisibilitySql.VisibleWorkPredicate(\"w.id\", \"w.curator_state\", \"w.is_catalog_only\")", serviceSource, StringComparison.Ordinal);
        Assert.Contains("HomeVisibilitySql.VisibleAssetPathPredicate(\"ad.file_path_root\")", serviceSource, StringComparison.Ordinal);
        Assert.Contains("w.collection_id AS collection_id", serviceSource, StringComparison.Ordinal);
        Assert.Contains("w.work_kind AS work_kind", serviceSource, StringComparison.Ordinal);
        Assert.Contains("root_work_id AS RootWorkId", serviceSource, StringComparison.Ordinal);
        Assert.Contains("CoverUrl = ResolveArtworkUrl", serviceSource, StringComparison.Ordinal);
        Assert.Contains("BackgroundUrl = ResolveArtworkUrl", serviceSource, StringComparison.Ordinal);
        Assert.Contains("BannerUrl = ResolveArtworkUrl", serviceSource, StringComparison.Ordinal);
        Assert.Contains("HeroUrl = null", serviceSource, StringComparison.Ordinal);
        Assert.Contains("LogoUrl = ResolveArtworkUrl", serviceSource, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryEndpoints_BatchEditRoutesFieldsByLineageScope()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs"));
        var serviceSource = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/ReadServices/LibraryCurationReadService.cs"));

        Assert.Contains("curationReadService.ResolveBatchEditTargetsAsync(", source, StringComparison.Ordinal);
        Assert.Contains("ClaimScopeCatalog.IsParentScoped(key, mediaType)", serviceSource, StringComparison.Ordinal);
        Assert.Contains("claimsByTargetAndKey", source, StringComparison.Ordinal);
        Assert.Contains("album fields are written once", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryEndpoints_DelegateDatabaseAccessToTypedServices()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs"));

        Assert.DoesNotContain("IDatabaseConnection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateConnection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QueryAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT ", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetUniverseCandidatesAsync(ct)", source, StringComparison.Ordinal);
        Assert.Contains("GetUniverseUnlinkedAsync(ct)", source, StringComparison.Ordinal);
        Assert.Contains("GetBestUniverseCandidateQidsAsync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("catch {", source, StringComparison.Ordinal);
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
