using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Settings;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Now panel's window: never more than 10 rows, a row stays at least 3 seconds, rows keep their place, and the
/// most recently active waiting item takes the place of a row whose time is up.
/// </summary>
public sealed class IngestionNowWindowTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualClock _clock = new(Start);

    [Fact]
    public void FiveHundredItemsInFlight_NeverShowMoreThanTenRows()
    {
        var window = new IngestionNowWindow(_clock);

        window.Update(Items(500));

        Assert.Equal(IngestionNowWindow.MaxRows, window.Rows.Count);
        _clock.Advance(TimeSpan.FromSeconds(10));
        window.Tick();
        Assert.True(window.Rows.Count <= IngestionNowWindow.MaxRows);
    }

    [Fact]
    public void FirstRows_AreTheMostRecentlyActive()
    {
        var window = new IngestionNowWindow(_clock);

        window.Update(Items(30));

        // Item 0 is the oldest; the newest ten (20 to 29) show, newest first.
        Assert.Equal(Enumerable.Range(20, 10).Reverse().Select(Id), window.Rows.Select(row => row.GroupId));
    }

    [Fact]
    public void FewerThanFiveInFlight_ShowsThemAll()
    {
        var window = new IngestionNowWindow(_clock);

        window.Update(Items(3));

        Assert.Equal(3, window.Rows.Count);
        Assert.True(window.Rows.Count < IngestionNowWindow.MinRows);
    }

    [Fact]
    public void ARowThatLeavesTheSnapshotAfterOneSecond_IsStillShownUntilThreeSeconds()
    {
        var window = new IngestionNowWindow(_clock);
        window.Update(Items(2));
        var gone = window.Rows[0];

        _clock.Advance(TimeSpan.FromSeconds(1));
        window.Update(Items(2).Where(item => item.GroupId != gone.GroupId));
        Assert.Contains(window.Rows, row => row.GroupId == gone.GroupId);

        _clock.Advance(TimeSpan.FromSeconds(1.9));
        window.Tick();
        Assert.Contains(window.Rows, row => row.GroupId == gone.GroupId);

        _clock.Advance(TimeSpan.FromSeconds(0.2));
        Assert.True(window.Tick());
        Assert.DoesNotContain(window.Rows, row => row.GroupId == gone.GroupId);
    }

    [Fact]
    public void ARowKeepsItsLastKnownState_WhileItIsOnlyBeingHeldForItsMinimumTime()
    {
        var window = new IngestionNowWindow(_clock);
        window.Update([Item(1, "adding", "Identifying")]);

        _clock.Advance(TimeSpan.FromSeconds(1));
        window.Update([]);

        var held = Assert.Single(window.Rows);
        Assert.Equal("Identifying", held.StatusLabel);
        Assert.True(window.NeedsTick);
    }

    [Fact]
    public void WhenRowsFinishAndOthersWait_NewItemsTakeTheirPlaceWithoutMovingTheRowsThatStay()
    {
        var window = new IngestionNowWindow(_clock);
        window.Update(Items(14));
        var before = window.Rows.Select(row => row.GroupId).ToList();

        // Two shown rows finish; four more items are now waiting.
        var finished = new[] { before[1], before[4] };
        _clock.Advance(TimeSpan.FromSeconds(4));
        window.Update(Items(14).Where(item => !finished.Contains(item.GroupId)));

        var after = window.Rows.Select(row => row.GroupId).ToList();
        Assert.Equal(10, after.Count);
        Assert.DoesNotContain(finished[0], after);
        Assert.DoesNotContain(finished[1], after);
        foreach (var index in new[] { 0, 2, 3, 5, 6, 7, 8, 9 })
        {
            Assert.Equal(before[index], after[index]);
        }

        // The two places were filled by items that were waiting, most recently active first.
        var waiting = Items(14).Select(item => item.GroupId).Except(before).ToHashSet();
        Assert.Contains(after[1], waiting);
        Assert.Contains(after[4], waiting);
    }

    [Fact]
    public void ARowStillBeingWorkedOn_IsNotReplacedByANewerItem_EvenAfterItsMinimumTime()
    {
        var window = new IngestionNowWindow(_clock);
        window.Update(Items(10));
        var shown = window.Rows.Select(row => row.GroupId).ToList();

        _clock.Advance(TimeSpan.FromSeconds(4));
        window.Update(Items(10).Append(Item(99, updatedMinutes: 500)));

        Assert.Equal(shown, window.Rows.Select(row => row.GroupId).ToList());
        Assert.False(window.NeedsTick);
    }

    [Fact]
    public void AFinishedRowWhoseTimeIsUp_IsReplacedInItsPlaceByTheMostRecentlyActiveWaitingItem()
    {
        var window = new IngestionNowWindow(_clock);
        window.Update(Items(10));
        var finished = window.Rows[3].GroupId;
        var remaining = Items(10).Where(item => item.GroupId != finished).ToList();

        _clock.Advance(TimeSpan.FromSeconds(4));
        window.Update(remaining.Append(Item(98, updatedMinutes: 400)).Append(Item(99, updatedMinutes: 500)));

        Assert.Equal(10, window.Rows.Count);
        Assert.Equal(Id(99), window.Rows[3].GroupId);
    }

    [Fact]
    public void ARowStillInFlightIsNotReplacedBeforeItsMinimumTime()
    {
        var window = new IngestionNowWindow(_clock);
        window.Update(Items(10));

        _clock.Advance(TimeSpan.FromSeconds(1));
        window.Update(Items(10).Append(Item(99, updatedMinutes: 500)));

        Assert.DoesNotContain(window.Rows, row => row.GroupId == Id(99));
    }

    [Fact]
    public void WhenNothingIsHeldOrWaiting_NoTickIsNeeded()
    {
        var window = new IngestionNowWindow(_clock);
        window.Update(Items(3));

        Assert.False(window.NeedsTick);
    }

    [Fact]
    public void AnItemThatAppearsTwice_IsShownOnce()
    {
        var window = new IngestionNowWindow(_clock);

        window.Update([Item(1), Item(1)]);

        Assert.Single(window.Rows);
    }

    private static Guid Id(int number) => new(number, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    // Item 0 is the oldest, item count-1 the most recently active.
    private static IngestionMediaGroupDto Item(int number, string availability = "adding", string status = "Adding", int updatedMinutes = 0) => new()
    {
        GroupId = Id(number),
        Title = $"Item {number}",
        Availability = availability,
        StatusLabel = status,
        UpdatedAt = Start.AddMinutes(updatedMinutes != 0 ? updatedMinutes : number),
    };

    private static List<IngestionMediaGroupDto> Items(int count) => Enumerable.Range(0, count).Select(number => Item(number)).ToList();

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
