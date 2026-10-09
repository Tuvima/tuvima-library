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
        var service = new IngestionPresentationReadService(_fixture.Database);

        await service.GetBatchMediaAsync(batchId, 0, 50);

        // Best of three: a shared CI runner can stall one run without the query getting slower.
        var best = TimeSpan.MaxValue;
        MediaEngine.Contracts.Paging.PagedResponse<MediaEngine.Contracts.Ingestion.IngestionMediaGroupDto> page = null!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var timer = Stopwatch.StartNew();
            page = await service.GetBatchMediaAsync(batchId, 9_950, 50);
            timer.Stop();
            _output.WriteLine("Historical ingestion deep page: {0:F0} ms", timer.Elapsed.TotalMilliseconds);
            best = timer.Elapsed < best ? timer.Elapsed : best;
            if (best < TimeSpan.FromSeconds(2))
            {
                break;
            }
        }

        Assert.Equal(10_000, page.TotalCount);
        Assert.Equal(50, page.Items.Count);
        Assert.False(page.HasMore);
        Assert.True(best < TimeSpan.FromSeconds(2),
            $"Historical ingestion deep page took {best.TotalMilliseconds:F0} ms.");
    }

    private sealed class QueryPlanRow
    {
        public string Detail { get; init; } = string.Empty;
    }
}
