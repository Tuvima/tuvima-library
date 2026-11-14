using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Settings.LibraryActivity;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Working now fits the width (spec library-activity D34, D35, issue 862): the heading counts every title in progress, the row never shows
/// more than 10 tiles or more than fit across, each tile is held at least 3 seconds, a long list rotates, and the row never scrolls sideways.
/// </summary>
public sealed class LibraryActivityWorkingNowFitsTests : AsyncBunitContext
{
    private const string Folder = "src/MediaEngine.Web/Components/Settings/LibraryActivity/";

    private readonly FakeClock _clock = new();

    public LibraryActivityWorkingNowFitsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/working-now-fit.js").Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void WithThirtyTitlesInProgress_TheHeadingReadsWorkingNowThirtyTitles_AndAtMostTenTilesShow()
    {
        var cut = RenderRow(Titles(30));

        Assert.Equal("Working now · 30 titles", cut.Find("[data-testid='activity-working-now-heading']").TextContent.Trim());
        Assert.Equal(10, cut.FindAll("[data-testid='activity-tile']").Count);
    }

    [Fact]
    public void WithOneTitleInProgress_TheHeadingIsSingular()
    {
        var cut = RenderRow(Titles(1));

        Assert.Equal("Working now · 1 title", cut.Find("[data-testid='activity-working-now-heading']").TextContent.Trim());
        Assert.Single(cut.FindAll("[data-testid='activity-tile']"));
    }

    [Fact]
    public void TheHeadingCountsTheEnginesTotal_EvenWhenItListsOnlyAFewTitles()
    {
        var cut = RenderRow(Titles(12), total: 34);

        Assert.Equal("Working now · 34 titles", cut.Find("[data-testid='activity-working-now-heading']").TextContent.Trim());
    }

    [Fact]
    public async Task WhenOnlyFourTilesFitAcrossTheRow_FourShow_AndTheRowRotatesAfterThreeSeconds()
    {
        var cut = RenderRow(Titles(12));
        await cut.InvokeAsync(() => cut.Instance.OnFit(4));

        var first = TileTitles(cut);
        Assert.Equal(4, first.Count);

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(first, TileTitles(cut));

        _clock.Advance(TimeSpan.FromSeconds(1));
        cut.WaitForAssertion(() =>
        {
            var next = TileTitles(cut);
            Assert.Equal(4, next.Count);
            Assert.Empty(next.Intersect(first));
        });
    }

    [Fact]
    public async Task WhenEveryTitleFits_TheRowDoesNotRotate()
    {
        var cut = RenderRow(Titles(4));
        await cut.InvokeAsync(() => cut.Instance.OnFit(9));
        var before = TileTitles(cut);

        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(before, TileTitles(cut));
        Assert.Equal(4, before.Count);
    }

    [Fact]
    public void ATileWithAPage_StillOpensItsPage_AndIsHalfSize()
    {
        var items = Titles(1);
        items[0].DetailRoute = "/details/movie/11111111-1111-4111-8111-111111111111";

        var cut = RenderRow(items);

        var tile = cut.Find("[data-testid='activity-tile']");
        Assert.Contains("is-compact", tile.ClassName, StringComparison.Ordinal);
        Assert.Equal("/details/movie/11111111-1111-4111-8111-111111111111", tile.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public void TheRowsStylesNeverScrollSideways_HoldOneRowAndFadeOnlyWhenMotionIsAllowed()
    {
        var row = Read(Folder + "LibraryActivityWorkingNow.razor.css");
        var tile = Read(Folder + "LibraryActivityTile.razor.css");

        Assert.DoesNotContain("overflow-x: auto", row, StringComparison.Ordinal);
        Assert.DoesNotContain("overflow-x: scroll", row, StringComparison.Ordinal);
        Assert.DoesNotContain("overflow: auto", row, StringComparison.Ordinal);
        Assert.DoesNotContain("overflow: scroll", row, StringComparison.Ordinal);
        Assert.DoesNotContain("scroll-snap", row, StringComparison.Ordinal);
        Assert.Contains("overflow: hidden", row, StringComparison.Ordinal);
        Assert.Contains("flex-wrap: wrap", row, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce) { .lact-tile.is-compact { animation: none; } }", tile, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRowsTilesAreAboutHalfTodaysSize_OnADesktopAndSmallerStillOnAPhone()
    {
        var row = Read(Folder + "LibraryActivityWorkingNow.razor.css");

        Assert.Contains("--lact-working-tile-w: 107px;", row, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 600px) { .lact-working { --lact-working-tile-w: 76px;", row, StringComparison.Ordinal);
    }

    private IRenderedComponent<LibraryActivityWorkingNow> RenderRow(IReadOnlyList<IngestionMediaGroupDto> items, int? total = null)
        => Render<LibraryActivityWorkingNow>(parameters => parameters
            .Add(w => w.Items, items)
            .Add(w => w.Total, total)
            .Add(w => w.Clock, _clock));

    private static List<string> TileTitles(IRenderedComponent<LibraryActivityWorkingNow> cut)
        => cut.FindAll("[data-testid='activity-tile']").Select(tile => tile.InnerHtml).ToList();

    private static List<IngestionMediaGroupDto> Titles(int count) => Enumerable.Range(0, count)
        .Select(index => new IngestionMediaGroupDto
        {
            GroupId = Guid.Parse($"00000000-0000-4000-8000-{index:D12}"),
            MediaType = "Movies",
            Title = $"Title {index:D2}",
            StatusLabel = "Adding",
            UpdatedAt = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero).AddSeconds(-index),
        })
        .ToList();

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(
        FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    /// <summary>A hand-written clock whose timers fire when the test advances it, so the 3-second hold runs without waiting.</summary>
    private sealed class FakeClock : TimeProvider
    {
        private readonly List<FakeTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(callback, state, _now + dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            var target = _now + by;
            while (true)
            {
                var due = _timers.Where(timer => !timer.Disposed && timer.Next <= target).OrderBy(timer => timer.Next).FirstOrDefault();
                if (due is null)
                {
                    break;
                }

                _now = due.Next;
                due.Fire();
            }

            _now = target;
        }
    }

    private sealed class FakeTimer(TimerCallback callback, object? state, DateTimeOffset next, TimeSpan period) : ITimer
    {
        public DateTimeOffset Next { get; private set; } = next;
        public bool Disposed { get; private set; }

        public void Fire()
        {
            Next += period;
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
