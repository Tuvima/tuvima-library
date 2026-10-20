namespace MediaEngine.Api.Tests;

public sealed class IngestionOperationsContractTests
{
    [Fact]
    public void BatchProgressService_DoesNotReusePersistedFailedCountWhenJobSnapshotExists()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Providers",
            "Services",
            "BatchProgressService.cs"));

        Assert.Contains("snapshot.TotalJobs > 0", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.PipelineFailed", source, StringComparison.Ordinal);
        Assert.Contains("batch.FilesFailed", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var failed = Math.Max(batch.FilesFailed, snapshot.PipelineFailed);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchCompletion_WaitsForDurableChildOperationsAndRunsWithoutADashboardClient()
    {
        var root = FindRepoRoot();
        var progressSource = File.ReadAllText(Path.Combine(
            root, "src", "MediaEngine.Providers", "Services", "BatchProgressService.cs"));
        var repositorySource = File.ReadAllText(Path.Combine(
            root, "src", "MediaEngine.Storage", "IngestionBatchRepository.cs"));
        var hostedServicesSource = File.ReadAllText(Path.Combine(
            root, "src", "MediaEngine.Api", "DependencyInjection", "TuvimaHostedServiceCollectionExtensions.cs"));
        var reconcilerSource = File.ReadAllText(Path.Combine(
            root, "src", "MediaEngine.Api", "Services", "IngestionBatchProgressHostedService.cs"));

        Assert.Contains("snapshot.OutstandingOperations == 0", progressSource, StringComparison.Ordinal);
        Assert.Contains("failed_retryable", repositorySource, StringComparison.Ordinal);
        Assert.Contains("AddHostedService<IngestionBatchProgressHostedService>()", hostedServicesSource, StringComparison.Ordinal);
        Assert.Contains("GetActiveAsync", reconcilerSource, StringComparison.Ordinal);
        Assert.Contains("EmitProgressAsync", reconcilerSource, StringComparison.Ordinal);
    }

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

    [Fact]
    public void CurrentSchema_IncludesIngestionBatchArtifactLedger()
    {
        var schema = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Storage",
            "Schema",
            "schema.snapshot.sql"));

        Assert.Contains("CREATE TABLE ingestion_batch_artifacts", schema, StringComparison.Ordinal);
        Assert.Contains("artifact_type", schema, StringComparison.Ordinal);
        Assert.Contains("parent_entity_id", schema, StringComparison.Ordinal);
        Assert.Contains("provider_id", schema, StringComparison.Ordinal);
        Assert.Contains("detail_json", schema, StringComparison.Ordinal);
        Assert.Contains("idx_ingestion_batch_artifacts_batch", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE ingestion_batch_artifacts", schema, StringComparison.OrdinalIgnoreCase);

        var storageRegistrations = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "MediaEngine.Api",
            "DependencyInjection",
            "TuvimaStorageServiceCollectionExtensions.cs"));
        Assert.Contains(
            "IIngestionBatchArtifactRepository",
            storageRegistrations,
            StringComparison.Ordinal);
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
