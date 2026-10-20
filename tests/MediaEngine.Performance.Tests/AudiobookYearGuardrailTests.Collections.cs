using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Storage;

namespace MediaEngine.Performance.Tests;

// Guardrails for the collection editor's details read, its "add media" search and a person's credits (spec D7, D8).
public sealed partial class AudiobookYearGuardrailTests
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task CollectionEditorDetails_BorrowedYearStaysWithinBudget()
    {
        var needsYear = await MeasureCollectionDetailsAsync(LibraryListFixture.YearBorrowingListenerId);
        var ownYear = await MeasureCollectionDetailsAsync(LibraryListFixture.OwnYearListenerId);

        AssertYearCostWithinBudget("collection editor details", needsYear, ownYear, CollectionDetailsYearBudgetKiloInstructions);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task AddMedia_BorrowedYearStaysWithinBudget()
    {
        // The same search over the same number of audiobooks: those that need a borrowed year and those that have their own.
        var needsYear = await MeasureAddMediaAsync(LibraryListFixture.YearBorrowingListenerId);
        var ownYear = await MeasureAddMediaAsync(LibraryListFixture.OwnYearListenerId);

        AssertYearCostWithinBudget("add media search", needsYear, ownYear, AddMediaYearBudgetKiloInstructions);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task PersonCredits_BorrowedYearStaysWithinBudget()
    {
        var needsYear = await MeasurePersonCreditsAsync(LibraryListFixture.YearBorrowingNarratorId);
        var ownYear = await MeasurePersonCreditsAsync(LibraryListFixture.OwnYearNarratorId);

        AssertYearCostWithinBudget("person credits", needsYear, ownYear, PersonCreditsYearBudgetKiloInstructions);
    }

    private async Task<long> MeasureCollectionDetailsAsync(Guid listenerId)
    {
        var workIds = await ListenerAudiobookWorkIdsAsync(_fixture.Small, listenerId);
        Assert.NotEmpty(workIds);

        return await MeasureAsync(
            $"Collection editor details ({workIds.Count} audiobooks of {listenerId:N})",
            _fixture.Small,
            async database =>
            {
                var items = await new CollectionMediaLookupReadService(database)
                    .ResolveMetadataAsync(workIds, CancellationToken.None);
                Assert.Equal(workIds.Count, items.Count);
                return items.Count;
            });
    }

    private async Task<long> MeasureAddMediaAsync(Guid listenerId)
    {
        var audiobooks = (await ListenerAudiobookWorkIdsAsync(_fixture.Small, listenerId)).ToHashSet();
        Assert.True(audiobooks.Count >= 24);

        return await MeasureAsync(
            $"Add media (first page of {audiobooks.Count} audiobooks of {listenerId:N})",
            _fixture.Small,
            async database =>
            {
                var found = await new CollectionMediaLookupReadService(database)
                    .LookupAsync(null, "Audiobooks", new HashSet<Guid>(), audiobooks, 0, 24, CancellationToken.None);
                Assert.Equal(24, found.Count);
                return found.Count;
            });
    }

    private async Task<long> MeasurePersonCreditsAsync(Guid personId)
    {
        return await MeasureAsync(
            $"Person credits ({personId:N})",
            _fixture.Small,
            async database =>
            {
                var service = new PersonCreditReadService(
                    new CanonicalValueArrayRepository(database),
                    new PersonRepository(database),
                    database);
                var credits = await service.GetLibraryCreditsAsync(personId, CancellationToken.None);
                Assert.NotEmpty(credits);
                return credits.Count;
            });
    }

    // Measured at 4,000 works when the budgets were set (thousand SQLite instructions, the same on every run): the borrowed
    // year cost 255 in the collection editor details (409 with 40 audiobooks that need it, 154 with 40 that have their own),
    // 163 in the "add media" first page (891 and 728) and 298 for a person credited on 40 audiobooks (3,758 and 3,460). With
    // the old per-row form the year cost 42,825, 42,359 and 46,780 (the whole library's credit list was built once per
    // read). Each budget is about one and a half times the measured cost.
    private const long CollectionDetailsYearBudgetKiloInstructions = 400;
    private const long AddMediaYearBudgetKiloInstructions = 250;
    private const long PersonCreditsYearBudgetKiloInstructions = 450;
}
