namespace MediaEngine.Api.Tests;

public sealed class EditionCoverEndpointRouteTests
{
    [Fact]
    public void EditionCoverUsesReviewedAuthorizedPreviewAndReplaySafeSave()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Api",
            "Endpoints", "MetadataEndpoints.EditionCover.cs"));

        Assert.Contains("/{entityId:guid}/edition-cover-preview", source,
            StringComparison.Ordinal);
        Assert.Contains("/{entityId:guid}/edition-cover\"", source,
            StringComparison.Ordinal);
        Assert.Contains("EvaluateAssetAsync", source, StringComparison.Ordinal);
        Assert.Contains("EvaluateArtworkLinkAsync", source, StringComparison.Ordinal);
        Assert.Contains("AffectedAssetLibraries", source, StringComparison.Ordinal);
        Assert.Contains("SameEditionCoverReview(current, review.Facts)", source,
            StringComparison.Ordinal);
        Assert.Contains("CommitReviewedCoverAsync", source, StringComparison.Ordinal);
        Assert.Contains("RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)",
            source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
