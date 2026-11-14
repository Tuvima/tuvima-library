using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Settings.LibraryActivity;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Working now window (spec library-activity D34): never more than 10 tiles or more than fit, each held at least 2 seconds,
/// and a rotation through every title in progress when they do not all fit.
/// </summary>
public sealed class WorkingNowRotationTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualClock _clock = new(Start);

    [Fact]
    public void FiftyTitlesInProgress_NeverShowMoreThanTenTiles_EvenOnAVeryWideRow()
    {
        var rotation = new WorkingNowRotation(_clock);

        rotation.Update(Titles(50), 40);

        Assert.Equal(WorkingNowRotation.MaxShown, rotation.Shown.Count);
        Assert.Equal(50, rotation.Total);
    }

    [Fact]
    public void OnlyAsManyTilesAsFitShow_AndANarrowerRowDropsTheOnesThatNoLongerFit()
    {
        var rotation = new WorkingNowRotation(_clock);
        rotation.Update(Titles(30), 8);
        Assert.Equal(8, rotation.Shown.Count);

        var changed = rotation.Update(Titles(30), 3);

        Assert.True(changed);
        Assert.Equal(3, rotation.Shown.Count);
        Assert.Equal(Ids(0, 3), rotation.Shown.Select(item => item.GroupId));
    }

    [Fact]
    public void ATileIsHeldForTwoSeconds_ThenTheRowRotatesToTheNextTitlesInLine()
    {
        var rotation = new WorkingNowRotation(_clock);
        rotation.Update(Titles(12), 4);

        _clock.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(rotation.Tick());
        Assert.Equal(Ids(0, 4), rotation.Shown.Select(item => item.GroupId));

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(rotation.Tick());
        Assert.Equal(Ids(4, 4), rotation.Shown.Select(item => item.GroupId));
    }

    [Fact]
    public void ARowMeasuredNarrowerThanFirstGuessed_RotatesToTheTitlesAfterTheOnesItKept()
    {
        var rotation = new WorkingNowRotation(_clock);
        rotation.Update(Titles(12), WorkingNowRotation.MaxShown);

        rotation.Update(Titles(12), 4);
        _clock.Advance(WorkingNowRotation.MinimumHold);
        rotation.Tick();

        Assert.Equal(Ids(4, 4), rotation.Shown.Select(item => item.GroupId));
    }

    [Fact]
    public void ARunThroughEveryTitle_ShowsEachOfThemAtLeastOnce_AndComesBackToTheFirst()
    {
        var rotation = new WorkingNowRotation(_clock);
        rotation.Update(Titles(34), 5);
        var seen = new HashSet<Guid>(rotation.Shown.Select(item => item.GroupId));

        for (var step = 0; step < 12; step++)
        {
            _clock.Advance(WorkingNowRotation.MinimumHold);
            rotation.Tick();
            Assert.Equal(5, rotation.Shown.Count);
            Assert.Equal(5, rotation.Shown.Select(item => item.GroupId).Distinct().Count());
            seen.UnionWith(rotation.Shown.Select(item => item.GroupId));
        }

        Assert.Equal(34, seen.Count);
    }

    [Fact]
    public void WhenEveryTitleFits_NothingRotates_AndTheRowNeedsNoTick()
    {
        var rotation = new WorkingNowRotation(_clock);
        rotation.Update(Titles(5), 8);

        _clock.Advance(TimeSpan.FromSeconds(60));

        Assert.False(rotation.NeedsTick);
        Assert.False(rotation.Tick());
        Assert.Equal(Ids(0, 5), rotation.Shown.Select(item => item.GroupId));
    }

    [Fact]
    public void ATitleThatFinishesEarly_StaysItsTwoSeconds_ThenLeaves()
    {
        var rotation = new WorkingNowRotation(_clock);
        rotation.Update(Titles(3), 8);

        _clock.Advance(TimeSpan.FromSeconds(1));
        rotation.Update(Titles(3).Skip(1).ToList(), 8);
        Assert.Equal(3, rotation.Shown.Count);
        Assert.True(rotation.NeedsTick);

        _clock.Advance(TimeSpan.FromSeconds(0.9));
        Assert.False(rotation.Tick());
        Assert.Equal(3, rotation.Shown.Count);

        _clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.True(rotation.Tick());
        Assert.Equal(Ids(1, 2), rotation.Shown.Select(item => item.GroupId));
    }

    [Fact]
    public void AFreshTitleThatArrivesWhileThereIsRoom_JoinsAtOnce_WithoutDisturbingTheTilesAlreadyShown()
    {
        var rotation = new WorkingNowRotation(_clock);
        rotation.Update(Titles(3), 8);

        _clock.Advance(TimeSpan.FromSeconds(1));
        rotation.Update(Titles(4), 8);

        Assert.Equal(Ids(0, 4), rotation.Shown.Select(item => item.GroupId));
    }

    private static IEnumerable<Guid> Ids(int from, int count) => Enumerable.Range(from, count).Select(Id);

    private static Guid Id(int index) => Guid.Parse($"00000000-0000-4000-8000-{index:D12}");

    private static List<IngestionMediaGroupDto> Titles(int count) => Enumerable.Range(0, count)
        .Select(index => new IngestionMediaGroupDto { GroupId = Id(index), MediaType = "Movies", Title = $"Title {index:D2}" })
        .ToList();

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
