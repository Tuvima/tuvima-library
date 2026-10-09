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
    public void ActivityEndpoints_ExposeBatchFirstAuditContract()
    {
        var endpoints = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Endpoints/ActivityEndpoints.cs"));
        var service = File.ReadAllText(GetRepoFilePath(@"src/MediaEngine.Api/Services/ReadServices/ActivityBatchReadService.cs"));
        var registrations = File.ReadAllText(GetRepoFilePath(
            @"src/MediaEngine.Api/DependencyInjection/ApiReadServiceCollectionExtensions.cs"));

        Assert.Contains("IActivityBatchReadService", endpoints, StringComparison.Ordinal);
        Assert.Contains("\"/batches\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("\"/batches/{batchId:guid}\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("GetActivityBatch", endpoints, StringComparison.Ordinal);
        Assert.Contains("\"/batches/{batchId:guid}/groups\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("\"/batches/{batchId:guid}/insights\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("GetActivityBatchInsights", endpoints, StringComparison.Ordinal);
        Assert.Contains("\"/batches/{batchId:guid}/events\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("GetActivityBatchEvents", endpoints, StringComparison.Ordinal);
        Assert.Contains("\"/batches/{batchId:guid}/items\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("\"/batches/{batchId:guid}/items/{assetId:guid}\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("\"/summary\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("\"/batches/{batchId:guid}/presentation\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("\"/batches/{batchId:guid}/media-groups\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("\"/people\"", endpoints, StringComparison.Ordinal);
        Assert.Contains("PagedResponse<ActivityBatchSummaryDto>", endpoints, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<IActivityBatchReadService", registrations, StringComparison.Ordinal);

        Assert.Contains("IDatabaseConnection", service, StringComparison.Ordinal);
        Assert.Contains("_db.CreateConnection()", service, StringComparison.Ordinal);
        Assert.DoesNotContain(".Open()", service, StringComparison.Ordinal);
        Assert.Contains("ingestion_batches", service, StringComparison.Ordinal);
        Assert.Contains("media_operations", service, StringComparison.Ordinal);
        Assert.Contains("identity_jobs", service, StringComparison.Ordinal);
        Assert.Contains("ingestion_log", service, StringComparison.Ordinal);
        Assert.Contains("system_activity", service, StringComparison.Ordinal);
        Assert.Contains("ingestion_batch_artifacts", service, StringComparison.Ordinal);
        Assert.Contains("canonical_values", service, StringComparison.Ordinal);
        Assert.Contains("person_media_links", service, StringComparison.Ordinal);
        Assert.Contains("persons", service, StringComparison.Ordinal);
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
