namespace MediaEngine.Api.Tests;

public sealed class ReviewRemediationOrderingTests
{
    [Fact]
    public void RetagRetry_DoesNotResolveReviewUntilWorkerWriteSucceeds()
    {
        var maintenance = ReadSource("src/MediaEngine.Api/Endpoints/MaintenanceEndpoints.cs");
        var worker = ReadSource("src/MediaEngine.Api/Services/RetagSweepWorker.cs");

        var retryStart = maintenance.IndexOf("/maintenance/retag-sweep/retry/{assetId:guid}", StringComparison.Ordinal);
        var retryEnd = maintenance.IndexOf(".WithTags(\"Maintenance\")", retryStart, StringComparison.Ordinal);
        var retryEndpoint = maintenance[retryStart..retryEnd];

        Assert.DoesNotContain("UpdateStatusAsync", retryEndpoint, StringComparison.Ordinal);
        Assert.Contains("await _writeBackService.WriteMetadataAsync", worker, StringComparison.Ordinal);
        // The adapter currently cannot prove a complete physical round trip for
        // every format. An attempted write must leave the review unresolved.
        Assert.DoesNotContain("ResolveWritebackReviewsAsync(stale.AssetId, ct)", worker, StringComparison.Ordinal);
        Assert.Contains("totalCompletedUnverified++", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchEndpoints_ResolveReviewAfterRemediationSideEffects()
    {
        var source = ReadSource("src/MediaEngine.Api/Endpoints/ItemCanonicalEndpoints.cs");
        var retailPublish = source.IndexOf("new RetailMetadataHarvestedEvent", StringComparison.Ordinal);
        var retailResolve = source.IndexOf("UpdateStatusAsync(reviewItemId", retailPublish, StringComparison.Ordinal);
        var wikidataPublish = source.IndexOf("new WikidataMetadataHarvestedEvent", StringComparison.Ordinal);
        var wikidataResolve = source.IndexOf("UpdateStatusAsync(reviewItemId", wikidataPublish, StringComparison.Ordinal);

        Assert.True(retailResolve > retailPublish);
        Assert.True(wikidataResolve > wikidataPublish);
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(directory!.FullName, relativePath));
    }
}
