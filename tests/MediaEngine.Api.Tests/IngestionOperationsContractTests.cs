namespace MediaEngine.Api.Tests;

public sealed class IngestionOperationsContractTests
{
    [Fact]
    public void ReviewEndpoints_UseReadyOnlyReviewQueueReadService()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "Endpoints",
            "ReviewEndpoints.cs"));

        Assert.Contains("IReviewQueueReadService reviewReadService", source, StringComparison.Ordinal);
        Assert.Contains("reviewReadService.GetPendingAsync(paged.Limit, ct)", source, StringComparison.Ordinal);
        Assert.Contains("reviewReadService.GetPendingCountAsync(ct)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("libraryItemRepo.GetDetailAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Status: \"InReview\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetFourStateCountsAsync", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
