using MediaEngine.Api.Services.Display;
using MediaEngine.Storage.Contracts;
using Xunit.Abstractions;

namespace MediaEngine.Performance.Tests;

/// <summary>
/// Series and collection screens only need to know which works a profile may see, so they read the light visible-works
/// list instead of the full display row of every work. These checks measure the work SQLite does for that read at two
/// sizes: a fixed budget at 4,000 works, and a growth check that doubling the library at most about doubles the work.
/// </summary>
public sealed class VisibleWorksGuardrailTests : IClassFixture<LibraryListFixture>
{
    private readonly LibraryListFixture _fixture;
    private readonly ITestOutputHelper _output;

    public VisibleWorksGuardrailTests(LibraryListFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task VisibleWorks_StaysWithinBudgetAtFourThousandWorks()
    {
        var work = await MeasureVisibleWorksAsync(_fixture.Small);

        Assert.True(work < VisibleWorksBudgetKiloInstructions,
            $"The visible works read took {work:N0} thousand SQLite instructions at {LibraryListFixture.SmallLeafWorks:N0} works; the budget is {VisibleWorksBudgetKiloInstructions:N0}.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task VisibleWorks_GrowsInProportionToTheLibrary()
    {
        var small = await MeasureVisibleWorksAsync(_fixture.Small);
        var large = await MeasureVisibleWorksAsync(_fixture.Large);

        var ratio = (double)large / small;
        _output.WriteLine("visible works: {0:F2}x the work for 2x the works", ratio);
        Assert.True(ratio <= MaxGrowthForDoubleTheWorks,
            $"The visible works read did {ratio:F2} times the work for twice the works ({small:N0} -> {large:N0} thousand SQLite instructions); "
            + $"at most {MaxGrowthForDoubleTheWorks} is allowed. A cost that grows faster than the library usually means a per-item look-up has stopped using its index.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task VisibleWorks_ReportsTheFullDisplayReadForComparison()
    {
        var light = await MeasureVisibleWorksAsync(_fixture.Small);

        var counter = new SqliteWorkCounter(_fixture.Small);
        var rows = await new DisplayWorkProjectionReader(counter).LoadAsync(CancellationToken.None);

        // Output only: the number the light read is compared against, not a budget.
        _output.WriteLine(
            "Full display read: {0:N0} rows, {1:N0} thousand SQLite instructions (light read: {2:N0})",
            rows.Count,
            counter.KiloInstructions,
            light);
        Assert.NotEmpty(rows);
    }

    private async Task<long> MeasureVisibleWorksAsync(IDatabaseConnection database)
    {
        var work = new SqliteWorkCounter(database);

        var rows = await new DisplayVisibleWorkReader(work).LoadAsync(CancellationToken.None);

        _output.WriteLine("Visible works: {0:N0} rows, {1:N0} thousand SQLite instructions", rows.Count, work.KiloInstructions);
        Assert.NotEmpty(rows);
        return work.KiloInstructions;
    }

    // Proportional growth is 2x.
    private const double MaxGrowthForDoubleTheWorks = 2.5;

    // Measured on this fixture when the budget was set: 1,469 thousand instructions at 4,000 works (2,939 at 8,000), the
    // same on every run. The full display read of the same 4,000 works took 32,292, so the light read does under a
    // twentieth of the work. The budget is about one and a half times the fixed cost.
    private const long VisibleWorksBudgetKiloInstructions = 2_200;
}
