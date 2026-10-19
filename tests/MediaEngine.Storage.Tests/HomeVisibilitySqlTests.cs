using MediaEngine.Storage;

namespace MediaEngine.Storage.Tests;

public sealed class HomeVisibilitySqlTests
{
    [Fact]
    public void VisibleAssetPathPredicate_ExcludesStagingAndQuarantineRoots()
    {
        var predicate = HomeVisibilitySql.VisibleAssetPathPredicate("ma.file_path_root");

        Assert.Contains("NOT LIKE '%/.data/staging/%'", predicate, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE '%\\.data\\staging\\%'", predicate, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE '%/quarantine/%'", predicate, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE '%\\quarantine\\%'", predicate, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadyWorkPredicate_ReadsTheStoredMarkAndKeepsMissingFilesAndCatalogOnlyOut()
    {
        var predicate = WorkReadinessSql.ReadyWorkPredicate("w");

        Assert.Contains("w.ready_at IS NOT NULL", predicate, StringComparison.Ordinal);
        Assert.DoesNotContain("review_queue", predicate, StringComparison.Ordinal);
        Assert.DoesNotContain("identity_jobs", predicate, StringComparison.Ordinal);
        Assert.Contains("COALESCE(w.is_catalog_only, 0) = 0", predicate, StringComparison.Ordinal);
        Assert.Contains("ma_v.file_path_root", predicate, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE '%/quarantine/%'", predicate, StringComparison.Ordinal);
    }
}
