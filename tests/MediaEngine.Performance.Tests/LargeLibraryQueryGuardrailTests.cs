using System.Diagnostics;
using Dapper;
using MediaEngine.Api.Services.ReadServices;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MediaEngine.Performance.Tests;

public sealed class LargeLibraryQueryGuardrailTests : IClassFixture<LargeLibraryFixture>
{
    private readonly LargeLibraryFixture _fixture;
    private readonly ITestOutputHelper _output;

    public LargeLibraryQueryGuardrailTests(LargeLibraryFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void CollectionPage_UsesCoveringIndexAtOneHundredThousandWorks()
    {
        var collectionId = _fixture.SeedCollectionItems(100_000);
        using var connection = _fixture.Database.CreateConnection();

        var plan = connection.Query<QueryPlanRow>(
            """
            EXPLAIN QUERY PLAN
            SELECT work_id
            FROM collection_items
            WHERE collection_id = @collectionId
            ORDER BY sort_order
            LIMIT 50;
            """, new { collectionId }).ToList();

        Assert.Contains(plan, row => row.Detail.Contains(
            "idx_collection_items_collection_sort",
            StringComparison.OrdinalIgnoreCase));

        var timer = Stopwatch.StartNew();
        var rows = connection.Query<Guid>(
            """
            SELECT work_id
            FROM collection_items
            WHERE collection_id = @collectionId
            ORDER BY sort_order
            LIMIT 50;
            """, new { collectionId }).AsList();
        timer.Stop();

        Assert.Equal(50, rows.Count);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2),
            $"Indexed collection page took {timer.Elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public void FilePathIndexes_UseCaseInsensitiveCollation()
    {
        using var connection = _fixture.Database.CreateConnection();
        var indexes = connection.Query<string>(
            "SELECT sql FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL;").ToList();

        Assert.Contains(indexes, sql => sql.Contains(
            "file_path_root COLLATE NOCASE",
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(indexes, sql => sql.Contains(
            "file_hash_cache(absolute_path COLLATE NOCASE)",
            StringComparison.OrdinalIgnoreCase));
        Assert.Contains(indexes, sql => sql.Contains(
            "persons (name COLLATE NOCASE)",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task HistoricalIngestion_DeepPageStaysResponsiveAtTenThousandGroups()
    {
        var batchId = _fixture.SeedIngestionBatch(10_000);
        var work = new SqliteWorkCounter(_fixture.Database);
        var service = new IngestionPresentationReadService(work);

        // Responsive is measured as the work SQLite does, not the seconds it takes: elapsed time moves with whatever else
        // the machine is running, the work for the same data does not.
        var page = await service.GetBatchMediaAsync(batchId, 9_950, 50);

        _output.WriteLine("Historical ingestion deep page: {0:N0} thousand SQLite instructions", work.KiloInstructions);
        Assert.Equal(10_000, page.TotalCount);
        Assert.Equal(50, page.Items.Count);
        Assert.False(page.HasMore);
        Assert.True(work.KiloInstructions < DeepPageWorkBudgetKiloInstructions,
            $"Historical ingestion deep page took {work.KiloInstructions:N0} thousand SQLite instructions; the budget is {DeepPageWorkBudgetKiloInstructions:N0}.");
    }

    // The deep page measured 9,159 thousand instructions, the same on every run, when this budget was set. The budget is two
    // and a half times that, so it trips when the query starts doing a different order of work (an extra scan, a per-row
    // lookup) and stays quiet for small changes to the query.
    private const long DeepPageWorkBudgetKiloInstructions = 23_000;

    private sealed class QueryPlanRow
    {
        public string Detail { get; init; } = string.Empty;
    }
}
