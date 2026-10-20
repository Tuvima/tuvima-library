namespace MediaEngine.Api.Tests;

public sealed class ActivityEndpointTests
{
    [Fact]
    public void ActivityEndpoints_ResolvePlaceholderPersonNamesBeforeReturningFeed()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs"));

        Assert.Contains("ResolveActivityPersonNameAsync", source, StringComparison.Ordinal);
        Assert.Contains("personRepo.FindByIdAsync", source, StringComparison.Ordinal);
        Assert.Contains("qidLabelRepo.GetLabelAsync", source, StringComparison.Ordinal);
        Assert.Contains("Unknown Person (", source, StringComparison.Ordinal);
        Assert.Contains("Name pending ({qid})", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivityEndpoints_KeepTheSummaryAndTheFlatLogButHaveNoBatchOrRunRoutes()
    {
        var endpoints = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs"));

        Assert.Contains("\"/summary\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("\"/recent\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("\"/log\"", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("/batches", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("/run/", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("/people", endpoints, StringComparison.Ordinal);
        Assert.DoesNotContain("IActivityBatchReadService", endpoints, StringComparison.Ordinal);
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
