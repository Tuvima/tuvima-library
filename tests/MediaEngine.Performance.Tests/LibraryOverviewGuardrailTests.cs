using System.Reflection;
using Dapper;
using MediaEngine.Storage;
using Xunit.Abstractions;

namespace MediaEngine.Performance.Tests;

/// <summary>
/// The library summary tiles in Settings read the library overview (bug B17). Its counts once built the whole item projection
/// three times, with every display field (the year look-up alone is most of that statement): 28,938 thousand SQLite
/// instructions at 4,000 works. It now builds the projection once and skips the display-only columns no count reads. These
/// checks keep the read within a budget and prove the skipped columns change no count.
/// </summary>
public sealed partial class LibraryOverviewGuardrailTests : IClassFixture<LibraryListFixture>
{
    private const long OverviewCountsBudgetKiloInstructions = 10_000;

    private readonly LibraryListFixture _fixture;
    private readonly ITestOutputHelper _output;

    public LibraryOverviewGuardrailTests(LibraryListFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task OverviewCounts_StayWithinBudgetAtFourThousandWorks()
    {
        var counter = new SqliteWorkCounter(_fixture.Small);
        await new LibraryItemRepository(counter).GetOverviewCountsAsync(CancellationToken.None);
        var work = counter.KiloInstructions;
        _output.WriteLine($"Overview counts: {work:N0} thousand SQLite instructions at {LibraryListFixture.SmallLeafWorks:N0} works.");

        Assert.True(work < OverviewCountsBudgetKiloInstructions,
            $"The library overview counts took {work:N0} thousand SQLite instructions at {LibraryListFixture.SmallLeafWorks:N0} works; the budget is {OverviewCountsBudgetKiloInstructions:N0}. "
            + "Check that the counts still read the projection once and in its counts-only form.");
    }

    [Fact]
    public async Task OverviewCounts_MatchTheCountsOfTheFullProjection()
    {
        var counts = await new LibraryItemRepository(_fixture.Small).GetOverviewCountsAsync(CancellationToken.None);

        var build = typeof(LibraryItemRepository).GetMethod("BuildProjectionSql", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("LibraryItemRepository.BuildProjectionSql was not found.");
        var fullProjection = (string)build.Invoke(null, [false, false])!;
        using var connection = _fixture.Small.CreateConnection();
        var expected = connection.QueryFirst<(int Total, int InReview, int Visible, int Hidden, int ArtPending, int Enriched, int WithQid, int UniverseAssigned)>(
            fullProjection + """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN status = 'InReview' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN library_visibility = 'visible' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN library_visibility = 'hidden' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN artwork_state = 'pending' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN pipeline_step = 'Enrichment' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN wikidata_qid IS NOT NULL AND wikidata_qid != '' AND wikidata_qid NOT LIKE 'NF%' THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN universe_assigned = 1 THEN 1 ELSE 0 END), 0)
            FROM full_data;
            """);

        Assert.Equal(expected.Total, counts.Projection.TotalItems);
        Assert.Equal(expected.InReview, counts.Lifecycle.InReview);
        Assert.Equal(expected.Hidden, counts.Projection.HiddenByQualityGate);
        Assert.Equal(expected.ArtPending, counts.Projection.ArtPending);
        Assert.Equal(expected.Enriched, counts.Projection.EnrichedStage3);
        Assert.Equal(expected.WithQid, counts.Projection.WithQid);
        Assert.Equal(expected.UniverseAssigned, counts.Projection.UniverseAssigned);
        Assert.True(expected.Visible > 0, "The fixture should hold visible items, or this comparison proves nothing.");
    }
}
