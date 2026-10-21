using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Performance.Tests;

// Guardrail for the series search (spec D7, D8): the library search behind the "add to a series" box.
public sealed partial class AudiobookYearGuardrailTests
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task SeriesSearch_BorrowedYearStaysWithinBudget()
    {
        // The same search over the same number of audiobooks: those that need a borrowed year and those that have their own.
        var needsYear = await MeasureSeriesSearchAsync(LibraryListFixture.YearBorrowingListenerId);
        var ownYear = await MeasureSeriesSearchAsync(LibraryListFixture.OwnYearListenerId);

        AssertYearCostWithinBudget("series search", needsYear, ownYear, SeriesSearchYearBudgetKiloInstructions);
    }

    private async Task<long> MeasureSeriesSearchAsync(Guid listenerId)
    {
        var audiobooks = PlayedAudiobookWorkIds(listenerId);
        Assert.True(audiobooks.Count >= 20);
        var index = new FixedSearchIndex(audiobooks);

        return await MeasureAsync(async database =>
        {
            var found = await new CollectionSearchReadService(database, index)
                .SearchAsync("Audiobooks", CancellationToken.None);
            Assert.Equal(20, found.Count);
            return found.Count;
        });
    }

    // Measured at 4,000 works when the budget was set (thousand SQLite instructions, the same on every run): the search over
    // 40 audiobooks that need a borrowed year took 3,133,463 against 3,133,308 for 40 that have their own, so the year cost
    // 155 (the rest is the search itself). With the old per-row form the year cost 46,780 (the whole library's credit list was
    // built once per search). The budget is about one and a half times the measured cost.
    private const long SeriesSearchYearBudgetKiloInstructions = 250;

    /// <summary>A search index that answers every query with the same works, as the full-text index would for a matching word.</summary>
    private sealed class FixedSearchIndex(IReadOnlyList<Guid> workIds) : ISearchIndexRepository
    {
        public Task UpsertByEntityIdAsync(Guid entityId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<Guid>> SearchAsync(string query, int limit = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(workIds.Take(limit).ToList());

        public Task RebuildAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
