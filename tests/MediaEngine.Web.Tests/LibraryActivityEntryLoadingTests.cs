using Bunit;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Components.Settings.LibraryActivity;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Opening a History entry reads the whole batch (so a heading sorts every row, not only the first page) and marks a row of a running batch
/// Working while its title is in the live Working now list (review mock-ups D3).
/// </summary>
public sealed class LibraryActivityEntryLoadingTests : AsyncBunitContext
{
    private const string LibraryId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private readonly List<int> _offsets = [];
    private int _total = 230;

    public LibraryActivityEntryLoadingTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetActivityRunGroupsAsync), args =>
            {
                var offset = (int)args![3]!;
                var limit = (int)args[4]!;
                _offsets.Add(offset);
                var page = Enumerable.Range(offset, Math.Max(0, Math.Min(limit, _total - offset)))
                    .Select(index => new ActivityGroupDto($"movie:{index}", $"Movie {index:000}", "Movies", 1, null, null, 1_000, WorkOf(index)))
                    .ToList();
                return Task.FromResult<ActivityRunGroupsDto?>(new ActivityRunGroupsDto(Guid.NewGuid(), page, offset + page.Count < _total));
            })));
    }

    [Fact]
    public void OpeningABatchOfTwoHundredThirtyRows_ReadsEveryPage_SoTheListHoldsAllOfThem()
    {
        var cut = Render<LibraryActivityRunEntry>(parameters => parameters.Add(entry => entry.Run, Run(ActivityRunStatusNames.Completed)));

        cut.Find("[data-testid='lact-entry-toggle']").Click();

        cut.WaitForAssertion(() => Assert.Equal(230, cut.FindAll("[data-testid='lact-batch-row']").Count));
        Assert.Equal([0, 100, 200], _offsets);
        Assert.Empty(cut.FindAll(".lact-sentinel"));
    }

    [Fact]
    public void ARunningBatch_ReadsARowWorkingWhileItsTitleIsInTheLiveWorkingNowList_AndACompletedOneNeverDoes()
    {
        _total = 3;
        var working = new HashSet<Guid> { WorkOf(1) };

        var running = Render<LibraryActivityRunEntry>(parameters => parameters
            .Add(entry => entry.Run, Run(ActivityRunStatusNames.Active)).Add(entry => entry.WorkingIds, working));
        running.Find("[data-testid='lact-entry-toggle']").Click();
        running.WaitForAssertion(() => Assert.Equal(3, running.FindAll("[data-testid='lact-batch-row']").Count));

        Assert.Equal(["Done", "Working", "Done"], running.FindAll(".lact-batch__status").Select(status => status.TextContent.Trim()).ToList());
        Assert.Equal(["230 items", "229 done", "1 working"], running.FindAll("[data-testid='lact-batch-summary'] .lact-batch__pill").Select(pill => pill.TextContent.Trim()).ToList());

        var finished = Render<LibraryActivityRunEntry>(parameters => parameters
            .Add(entry => entry.Run, Run(ActivityRunStatusNames.Completed)).Add(entry => entry.WorkingIds, working));
        finished.Find("[data-testid='lact-entry-toggle']").Click();
        finished.WaitForAssertion(() => Assert.Equal(3, finished.FindAll("[data-testid='lact-batch-row']").Count));

        Assert.All(finished.FindAll(".lact-batch__status"), status => Assert.Equal("Done", status.TextContent.Trim()));
    }

    private static Guid WorkOf(int index) => new(index, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);

    private static ActivityRunDto Run(string status)
        => new(
            Guid.NewGuid(), LibraryId, "Library", ActivityRunKindNames.Added, status,
            DateTimeOffset.Now.AddHours(-3), status == ActivityRunStatusNames.Active ? null : DateTimeOffset.Now.AddHours(-2),
            230, [new ActivityRunCountDto("Movies", 230)], []);
}
