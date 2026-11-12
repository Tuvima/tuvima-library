using System.Diagnostics;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Paging;
using MediaEngine.Storage.Contracts;
using Xunit.Abstractions;

namespace MediaEngine.Performance.Tests;

/// <summary>
/// The library work feed (<c>GET /library/works</c>) is read by Listen and other screens and, in a real log, took 1 to 1.5 s
/// each time even on a few hundred works. These checks measure the SQLite work (thousand virtual-machine instructions, the same
/// on every run) of one page of the feed at two sizes, so a per-item look-up that stops using its index shows up as growth faster
/// than the library.
/// </summary>
public sealed class LibraryWorkFeedGuardrailTests : IClassFixture<LibraryListFixture>
{
    private readonly LibraryListFixture _fixture;
    private readonly ITestOutputHelper _output;

    public LibraryWorkFeedGuardrailTests(LibraryListFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task WorkFeed_GrowsInProportionToTheLibrary()
    {
        var small = await MeasureAsync(_fixture.Small);
        var large = await MeasureAsync(_fixture.Large);

        var ratio = (double)large / small;
        _output.WriteLine("work feed: {0:F2}x the work for 2x the works", ratio);
        Assert.True(ratio <= 2.5,
            $"The work feed did {ratio:F2} times the work for twice the works ({small:N0} -> {large:N0} thousand SQLite instructions); at most 2.5 is allowed.");
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task WorkFeed_StaysWithinBudgetAtFourThousandWorks()
    {
        var work = await MeasureAsync(_fixture.Small);

        Assert.True(work < WorkFeedBudgetKiloInstructions,
            $"One page of the work feed took {work:N0} thousand SQLite instructions at {LibraryListFixture.SmallLeafWorks:N0} works; the budget is {WorkFeedBudgetKiloInstructions:N0}.");
    }

    private async Task<long> MeasureAsync(IDatabaseConnection database)
    {
        var counter = new SqliteWorkCounter(database);
        var timer = Stopwatch.StartNew();
        var page = await new LibraryWorkFeedReadService(counter).GetWorksAsync(new PagedRequest(0, 100), CancellationToken.None);
        timer.Stop();

        _output.WriteLine("Work feed: {0:N0} items, {1:N0} thousand SQLite instructions, {2:N0} ms", page.Items.Count, counter.KiloInstructions, timer.ElapsedMilliseconds);
        Assert.NotEmpty(page.Items);
        return counter.KiloInstructions;
    }

    // Measured on this fixture when the budget was set: 1,274 thousand instructions for one page of 100 at 4,000 works (2,540 at
    // 8,000), the same on every run. The budget is about one and a half times that.
    private const long WorkFeedBudgetKiloInstructions = 1_900;
}
