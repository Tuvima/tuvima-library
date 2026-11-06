using Bunit;
using MediaEngine.Contracts.Library;
using MediaEngine.Web.Components.Settings.LibraryActivity;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The History list on the Activity page (spec library-activity, issue 690): it loads on its own first render even with no library chosen,
/// and it shows loading, empty, error (with Try again) and list states rather than a silent empty list.
/// </summary>
public sealed class LibraryActivityHistoryTests : AsyncBunitContext
{
    private Func<CancellationToken, Task<ActivityRunsDto?>> _runs = _ => Task.FromResult<ActivityRunsDto?>(new ActivityRunsDto([], false, null));
    private int _runCalls;
    private DateTimeOffset? _lastCompleted;
    private bool _lastCompletedReported;

    public LibraryActivityHistoryTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetActivityRunsAsync), args =>
            {
                Interlocked.Increment(ref _runCalls);
                return _runs((CancellationToken)args![8]!);
            })));
    }

    [Fact]
    public void TheFirstRender_WithNoLibraryChosen_LoadsTheRunsAndDrawsOneEntryEach()
    {
        _runs = _ => Task.FromResult<ActivityRunsDto?>(new ActivityRunsDto([Run("Movies", 14), Run("Books", 3)], false, DateTimeOffset.UtcNow));

        var cut = RenderHistory();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='lact-entry']").Count));
        Assert.Equal(1, _runCalls);
        Assert.True(_lastCompletedReported);
        Assert.NotNull(_lastCompleted);
    }

    [Fact]
    public void WhileTheRunsAreLoading_TheListShowsAProgressBarAndNoEmptyState()
    {
        var gate = new TaskCompletionSource<ActivityRunsDto?>();
        _runs = _ => gate.Task;

        var cut = RenderHistory();

        Assert.Empty(cut.FindAll("[data-testid='lact-timeline']"));
        Assert.DoesNotContain("Nothing has been added yet", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be loaded", cut.Markup, StringComparison.Ordinal);

        gate.SetResult(new ActivityRunsDto([Run("Movies", 1)], false, null));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='lact-entry']")));
    }

    [Fact]
    public void WhenTheEngineHasNoRuns_TheListSaysNothingHasBeenAddedYet()
    {
        var cut = RenderHistory();

        cut.WaitForAssertion(() => Assert.Contains("Nothing has been added yet", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid='lact-entry']"));
    }

    [Fact]
    public void WhenTheRunsCannotBeLoaded_TheListShowsAnErrorWithTryAgainAndTryAgainLoadsThem()
    {
        var healthy = false;
        _runs = _ => Task.FromResult<ActivityRunsDto?>(healthy ? new ActivityRunsDto([Run("TV", 85)], false, null) : null);

        var cut = RenderHistory();

        cut.WaitForAssertion(() => Assert.Contains("Activity could not be loaded", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Nothing has been added yet", cut.Markup, StringComparison.Ordinal);

        healthy = true;
        cut.Find("button:contains('Try again')").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='lact-entry']")));
        Assert.DoesNotContain("Activity could not be loaded", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ALoadThatANewerLoadReplaced_IsNotReportedAsAFailure()
    {
        var first = true;
        _runs = ct =>
        {
            if (first)
            {
                first = false;
                // The Dashboard client answers null when a call is cancelled.
                var pending = new TaskCompletionSource<ActivityRunsDto?>();
                ct.Register(() => pending.TrySetResult(null));
                return pending.Task;
            }

            return Task.FromResult<ActivityRunsDto?>(new ActivityRunsDto([Run("Movies", 2)], false, null));
        };

        var cut = RenderHistory();
        cut.Render(parameters => parameters.Add(history => history.RefreshToken, 1));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='lact-entry']")));
        Assert.DoesNotContain("Activity could not be loaded", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(2, _runCalls);
    }

    private IRenderedComponent<LibraryActivityHistory> RenderHistory()
        => Render<LibraryActivityHistory>(parameters => parameters
            .Add(history => history.LastCompletedChanged, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<DateTimeOffset?>(this, value =>
            {
                _lastCompleted = value;
                _lastCompletedReported = true;
            })));

    private static ActivityRunDto Run(string mediaType, int count) => new(
        Guid.NewGuid(),
        "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
        "Library",
        ActivityRunKindNames.Added,
        ActivityRunStatusNames.Completed,
        DateTimeOffset.UtcNow.AddMinutes(-30),
        DateTimeOffset.UtcNow.AddMinutes(-20),
        count,
        [new ActivityRunCountDto(mediaType, count)],
        []);
}
