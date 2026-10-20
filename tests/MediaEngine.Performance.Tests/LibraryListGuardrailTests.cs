using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using Xunit.Abstractions;

namespace MediaEngine.Performance.Tests;

/// <summary>
/// The library list once read every credit of a kind in the library for each item it showed, so its cost grew with the
/// square of the library (13-22 s at 4,000 works). These checks measure the work SQLite does for the first page, which is
/// the same on every run, at two sizes: a fixed budget at 4,000 works, and a growth check that doubling the library at most
/// about doubles the work.
/// </summary>
public sealed class LibraryListGuardrailTests : IClassFixture<LibraryListFixture>
{
    private readonly LibraryListFixture _fixture;
    private readonly ITestOutputHelper _output;

    public LibraryListGuardrailTests(LibraryListFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task LibraryList_UnfilteredFirstPageStaysWithinBudgetAtFourThousandWorks()
    {
        var work = await MeasureFirstPageAsync(_fixture.Small, mediaType: null);

        Assert.True(work < UnfilteredBudgetKiloInstructions,
            $"The unfiltered library list took {work:N0} thousand SQLite instructions at {LibraryListFixture.SmallLeafWorks:N0} works; the budget is {UnfilteredBudgetKiloInstructions:N0}.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task LibraryList_UnfilteredFirstPageGrowsInProportionToTheLibrary()
    {
        var small = await MeasureFirstPageAsync(_fixture.Small, mediaType: null);
        var large = await MeasureFirstPageAsync(_fixture.Large, mediaType: null);

        AssertProportionalGrowth("unfiltered library list", small, large);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task LibraryList_OneMediaTypeFirstPageStaysWithinBudgetAtFourThousandWorks()
    {
        var work = await MeasureFirstPageAsync(_fixture.Small, MediaTypeFilter);

        Assert.True(work < OneMediaTypeBudgetKiloInstructions,
            $"The {MediaTypeFilter} library list took {work:N0} thousand SQLite instructions at {LibraryListFixture.SmallLeafWorks:N0} works; the budget is {OneMediaTypeBudgetKiloInstructions:N0}.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task LibraryList_OneMediaTypeFirstPageGrowsInProportionToTheLibrary()
    {
        var small = await MeasureFirstPageAsync(_fixture.Small, MediaTypeFilter);
        var large = await MeasureFirstPageAsync(_fixture.Large, MediaTypeFilter);

        AssertProportionalGrowth($"{MediaTypeFilter} library list", small, large);
    }

    private async Task<long> MeasureFirstPageAsync(IDatabaseConnection database, string? mediaType)
    {
        var work = new SqliteWorkCounter(database);
        var repository = new LibraryItemRepository(work);

        var page = await repository.GetPageAsync(new LibraryItemQuery(MediaType: mediaType));

        _output.WriteLine(
            "Library list ({0}), {1:N0} visible: {2:N0} thousand SQLite instructions",
            mediaType ?? "no filter",
            page.TotalCount,
            work.KiloInstructions);
        Assert.Equal(50, page.Items.Count);
        Assert.True(page.HasMore);
        return work.KiloInstructions;
    }

    private void AssertProportionalGrowth(string read, long small, long large)
    {
        var ratio = (double)large / small;
        _output.WriteLine("{0}: {1:F2}x the work for 2x the works", read, ratio);
        Assert.True(ratio <= MaxGrowthForDoubleTheWorks,
            $"The {read} did {ratio:F2} times the work for twice the works ({small:N0} -> {large:N0} thousand SQLite instructions); "
            + $"at most {MaxGrowthForDoubleTheWorks} is allowed. A cost that grows faster than the library usually means a per-item look-up has stopped using its index.");
    }

    private const string MediaTypeFilter = "Movies";

    // Proportional growth is 2x. The look-ups that read every credit for each item grew 3.97-3.98x per doubling here.
    private const double MaxGrowthForDoubleTheWorks = 2.5;

    // Measured on this fixture when the budgets were set: unfiltered 14,451 and Movies 3,606 thousand instructions at
    // 4,000 works, the same on every run (the unfiltered read grows 2.03x for 2x the works). With the old credit look-ups
    // the same reads took 2,865,068 and 502,062; with the old per-audiobook look-up for the borrowed year the unfiltered
    // read took 83,876 at 4,000 works and grew 2.68x. Each budget is about one and a half times the fixed cost.
    private const long UnfilteredBudgetKiloInstructions = 21_700;
    private const long OneMediaTypeBudgetKiloInstructions = 5_400;
}
