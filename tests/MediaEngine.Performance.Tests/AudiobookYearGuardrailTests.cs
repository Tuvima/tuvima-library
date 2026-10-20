using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Storage;
using Xunit.Abstractions;

namespace MediaEngine.Performance.Tests;

/// <summary>
/// An audiobook with no original year of its own borrows the year of the book with the same title and author. A read that
/// works the year out the old way builds the whole library's credit list as soon as one of its rows needs a borrowed year
/// (42 million SQLite instructions at 4,000 works, 95 to 99 percent of such a read), and that cost grows with the library.
/// Today's slow form already grows in step with the library, so the usual "twice the library costs at most 2.5 times the work"
/// check would pass it. Each read is therefore measured twice on the same library, once with audiobooks that need a borrowed
/// year and once with the same number of audiobooks that have their own, and the difference (the year's own cost) gets a fixed
/// budget at 4,000 works. This half covers the series page list, the Continue shelf and the taste profile; the other half
/// (<c>AudiobookYearGuardrailTests.Collections.cs</c>) covers the reads that choose a page or a saved step first.
/// </summary>
public sealed partial class AudiobookYearGuardrailTests : IClassFixture<LibraryListFixture>
{
    private readonly LibraryListFixture _fixture;
    private readonly ITestOutputHelper _output;

    public AudiobookYearGuardrailTests(LibraryListFixture fixture, ITestOutputHelper output)
    {
        DapperConfiguration.Configure();
        _fixture = fixture;
        _output = output;
    }

    // Measured on the 4,000-work library, thousands of SQLite instructions, the same on every run. The year's own cost is
    // the read with 40 audiobooks that need a borrowed year minus the read with 40 that have their own. With the whole
    // library's credit list built once per visit (the old form): series page 47,138 vs 357 (year 46,781), Continue shelf
    // 49,122 vs 2,341 (46,781), taste profile 42,785 vs 26 (42,759). With the year worked out for the read's own audiobooks
    // only: series page 816 vs 356 (year 460), Continue shelf 2,568 vs 2,339 (229), taste profile 255 vs 24 (231).
    // Each budget is about 1.5 times the year cost measured after, so a return to the old form fails by more than 60 times.
    private const long SeriesPageYearBudgetKiloInstructions = 700;
    private const long ContinueShelfYearBudgetKiloInstructions = 350;
    private const long TasteSignalsYearBudgetKiloInstructions = 350;

    [Fact]
    [Trait("Category", "Performance")]
    public async Task SeriesPage_BorrowedYearStaysWithinBudget()
    {
        var needsYear = await MeasureAsync(counter => LoadSeriesPageAsync(counter, LibraryListFixture.YearBorrowingListenerId));
        var ownYear = await MeasureAsync(counter => LoadSeriesPageAsync(counter, LibraryListFixture.OwnYearListenerId));

        AssertYearCostWithinBudget("series page list", needsYear, ownYear, SeriesPageYearBudgetKiloInstructions);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task ContinueShelf_BorrowedYearStaysWithinBudget()
    {
        var needsYear = await MeasureAsync(counter => LoadContinueShelfAsync(counter, LibraryListFixture.YearBorrowingListenerId));
        var ownYear = await MeasureAsync(counter => LoadContinueShelfAsync(counter, LibraryListFixture.OwnYearListenerId));

        AssertYearCostWithinBudget("Continue shelf", needsYear, ownYear, ContinueShelfYearBudgetKiloInstructions);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task TasteSignals_BorrowedYearStaysWithinBudget()
    {
        var needsYear = await MeasureAsync(counter => LoadTasteSignalsAsync(counter, LibraryListFixture.YearBorrowingListenerId));
        var ownYear = await MeasureAsync(counter => LoadTasteSignalsAsync(counter, LibraryListFixture.OwnYearListenerId));

        AssertYearCostWithinBudget("taste profile", needsYear, ownYear, TasteSignalsYearBudgetKiloInstructions);
    }

    /// <summary>Runs one read against the 4,000-work library and returns the work SQLite did, in thousands of instructions.</summary>
    private async Task<long> MeasureAsync(Func<SqliteWorkCounter, Task<int>> read)
    {
        var counter = new SqliteWorkCounter(_fixture.Small);

        var rows = await read(counter);

        _output.WriteLine("{0:N0} rows, {1:N0} thousand SQLite instructions", rows, counter.KiloInstructions);
        Assert.True(rows > 0, "The read returned no rows, so it measured nothing.");
        return counter.KiloInstructions;
    }

    private void AssertYearCostWithinBudget(string read, long needsYear, long ownYear, long budget)
    {
        var yearCost = needsYear - ownYear;
        _output.WriteLine("{0}: {1:N0} thousand instructions with audiobooks that need a year, {2:N0} with their own; the year costs {3:N0} (budget {4:N0})",
            read, needsYear, ownYear, yearCost, budget);
        Assert.True(yearCost < budget,
            $"The {read} took {needsYear:N0} thousand SQLite instructions with audiobooks that need a borrowed year and {ownYear:N0} with audiobooks that have their own, "
            + $"so the borrowed year costs {yearCost:N0} at {LibraryListFixture.SmallLeafWorks:N0} works; the budget is {budget:N0}. "
            + "A breach usually means the read builds the whole library's credit list for the borrowed year again.");
    }

    /// <summary>The works of the audiobooks a play-history profile has played (one play per 100 works, 40 at 4,000 works).</summary>
    private List<Guid> PlayedAudiobookWorkIds(Guid listenerId)
    {
        using var connection = _fixture.Small.CreateConnection();
        var ids = connection.Query<byte[]>(
            """
            SELECT DISTINCT w.id
            FROM user_states us
            JOIN media_assets a ON a.id = us.asset_id
            JOIN editions e ON e.id = a.edition_id
            JOIN works w ON w.id = e.work_id
            WHERE us.user_id = @listener AND w.media_type = 'Audiobooks'
            """,
            new { listener = GuidSql.ToBlob(listenerId) })
            .Select(blob => GuidSql.FromDb(blob))
            .ToList();
        Assert.NotEmpty(ids);
        return ids;
    }

    /// <summary>
    /// <c>DetailCompositionOrchestrator.LoadCollectionWorksAsync</c> is internal to the Api assembly; no visibility in src/ was
    /// changed. It only uses the <c>_db</c> field, so the instance is created without running its constructor and <c>_db</c> is set.
    /// </summary>
    private async Task<int> LoadSeriesPageAsync(SqliteWorkCounter counter, Guid listenerId)
    {
        var type = typeof(DisplayWorkProjectionReader).Assembly.GetType(
                "MediaEngine.Api.Services.Details.Internals.DetailCompositionOrchestrator",
                throwOnError: false)
            ?? throw new InvalidOperationException("DetailCompositionOrchestrator was not found.");
        var orchestrator = RuntimeHelpers.GetUninitializedObject(type);
        (type.GetField("_db", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DetailCompositionOrchestrator._db was not found.")).SetValue(orchestrator, counter);
        var method = type.GetMethod("LoadCollectionWorksAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DetailCompositionOrchestrator.LoadCollectionWorksAsync was not found.");
        var resolvedWorkIds = PlayedAudiobookWorkIds(listenerId);

        var task = (Task)method.Invoke(orchestrator, [Guid.Empty, null, CancellationToken.None, resolvedWorkIds, listenerId, null])!;
        await task;

        var result = (IEnumerable)task.GetType().GetProperty("Result")!.GetValue(task)!;
        var rows = result.Cast<object>().ToList();
        // Every audiobook the page lists must come back, and the ones that need a borrowed year must show it (only some of
        // the fixture's audiobooks share a title with a book, so only some have a year to borrow).
        Assert.Equal(resolvedWorkIds.Count, rows.Count);
        var year = rows[0].GetType().GetProperty("Year")!;
        if (listenerId == LibraryListFixture.YearBorrowingListenerId)
        {
            Assert.Contains(rows, row => !string.IsNullOrEmpty((string?)year.GetValue(row)));
        }

        return rows.Count;
    }

    private async Task<int> LoadContinueShelfAsync(SqliteWorkCounter counter, Guid listenerId)
    {
        var rows = await new DisplayJourneyProjectionReader(counter).LoadAsync(listenerId, null, CancellationToken.None);

        // The listener's 40 audiobooks and 20 books are all at 40%, so all of them are on the shelf.
        Assert.Equal(PlayedAudiobookWorkIds(listenerId).Count, rows.Count(row => row.MediaType == "Audiobooks"));
        if (listenerId == LibraryListFixture.YearBorrowingListenerId)
        {
            Assert.Contains(rows, row => row.MediaType == "Audiobooks" && !string.IsNullOrEmpty(row.Year));
        }

        return rows.Count;
    }

    private async Task<int> LoadTasteSignalsAsync(SqliteWorkCounter counter, Guid listenerId)
    {
        var signals = await new TasteProfileRepository(counter).GetSignalsAsync(listenerId, 1_000);

        var audiobooks = signals.Where(signal => signal.MediaType == "Audiobooks").ToList();
        Assert.Equal(PlayedAudiobookWorkIds(listenerId).Count, audiobooks.Count);
        if (listenerId == LibraryListFixture.YearBorrowingListenerId)
        {
            Assert.Contains(audiobooks, signal => signal.ReleaseYear is not null);
        }

        return signals.Count;
    }
}
