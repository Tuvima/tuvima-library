using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Display;

namespace MediaEngine.Api.Tests;

public sealed class NextEpisodesPlannerTests
{
    private static readonly Guid Show = Guid.NewGuid();

    private static DisplayWorkRow Episode(string title, string? season, string? episode, Guid? asset = null, Guid? show = null, string mediaType = "TV") => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = asset ?? Guid.NewGuid(),
        RootWorkId = show ?? Show,
        MediaType = mediaType,
        Title = title,
        SeasonNumber = season,
        EpisodeNumber = episode,
        EpisodeStillUrl = $"/still/{title}",
    };

    [Fact]
    public void ListsTheOwnedEpisodesAfterTheOneInPlay_InWatchOrder_AcrossSeasons()
    {
        // Carried out of order on purpose: the order comes from season and episode, not from the list.
        var s1e1 = Episode("S1E1", "1", "1");
        var s1e2 = Episode("S1E2", "1", "2");
        var s1e3 = Episode("S1E3", "1", "3");
        var s2e1 = Episode("S2E1", "2", "1");
        var special = Episode("Special", "0", "1");

        var plan = NextEpisodesPlanner.Plan(Show, s1e2.AssetId, 3, [special, s2e1, s1e3, s1e1, s1e2])!;

        Assert.Equal(Show, plan.ShowId);
        Assert.Equal(s1e2.AssetId, plan.AfterAssetId);
        // Specials sort after every numbered season, as the show page's own next-episode choice does.
        Assert.Equal(["S1E3", "S2E1", "Special"], plan.Episodes.Select(next => next.Title));
        Assert.Equal([1, 2, 0], plan.Episodes.Select(next => next.SeasonNumber!.Value));
        Assert.Equal("/still/S1E3", plan.Episodes[0].StillUrl);
        Assert.Equal(s1e3.WorkId, plan.Episodes[0].WorkId);
        Assert.Equal(s1e3.AssetId, plan.Episodes[0].AssetId);
    }

    [Fact]
    public void TheLastEpisode_GetsAnEmptyList_NotAnError()
    {
        var last = Episode("Last", "1", "9");
        var first = Episode("First", "1", "1");

        var plan = NextEpisodesPlanner.Plan(Show, last.AssetId, 3, [first, last])!;

        Assert.Empty(plan.Episodes);
    }

    [Fact]
    public void ACombinedFileCountsOnce_AndTheEpisodesItCoversAreSkipped()
    {
        var combinedAsset = Guid.NewGuid();
        var one = Episode("One", "1", "1", combinedAsset);
        var two = Episode("Two", "1", "2", combinedAsset);
        var three = Episode("Three", "1", "3");
        var fourAndFiveAsset = Guid.NewGuid();
        var four = Episode("Four", "1", "4", fourAndFiveAsset);
        var five = Episode("Five", "1", "5", fourAndFiveAsset);
        var six = Episode("Six", "1", "6");

        // Playing the file that covers One and Two: the list starts at Three, and Four-and-Five is one entry.
        var plan = NextEpisodesPlanner.Plan(Show, combinedAsset, 10, [one, two, three, four, five, six])!;

        Assert.Equal(["Three", "Four", "Six"], plan.Episodes.Select(next => next.Title));
        Assert.DoesNotContain(plan.Episodes, next => next.AssetId == combinedAsset);
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    [InlineData(10, 10)]
    [InlineData(11, 10)]
    [InlineData(int.MaxValue, 10)]
    public void TheCountIsClampedToOneThroughTen(int? requested, int expected) =>
        Assert.Equal(expected, NextEpisodesPlanner.ClampCount(requested));

    [Fact]
    public void ThePlanNeverReturnsMoreThanTheClampedCount()
    {
        var episodes = Enumerable.Range(1, 30).Select(number => Episode($"E{number}", "1", number.ToString())).ToList();

        var plan = NextEpisodesPlanner.Plan(Show, episodes[0].AssetId, NextEpisodesPlanner.ClampCount(500), episodes)!;
        var small = NextEpisodesPlanner.Plan(Show, episodes[0].AssetId, NextEpisodesPlanner.ClampCount(-1), episodes)!;

        Assert.Equal(NextEpisodesPlanner.MaxCount, plan.Episodes.Count);
        Assert.Single(small.Episodes);
        Assert.Equal("E2", small.Episodes[0].Title);
    }

    [Fact]
    public void UnknownShowOrFile_AndAnotherShowsFile_AreAllNotFound()
    {
        var mine = Episode("Mine", "1", "1");
        var otherShow = Guid.NewGuid();
        var theirs = Episode("Theirs", "1", "1", show: otherShow);
        var movie = Episode("Movie", null, null, show: Show, mediaType: "Movie");
        var works = new[] { mine, theirs, movie };

        Assert.Null(NextEpisodesPlanner.Plan(Show, Guid.NewGuid(), 3, works));
        Assert.Null(NextEpisodesPlanner.Plan(Show, theirs.AssetId, 3, works));
        Assert.Null(NextEpisodesPlanner.Plan(Show, movie.AssetId, 3, works));
        Assert.Null(NextEpisodesPlanner.Plan(Guid.NewGuid(), mine.AssetId, 3, works));
        Assert.Null(NextEpisodesPlanner.Plan(Show, mine.AssetId, 3, []));
    }

    [Fact]
    public void OnlyWhatTheCallerIsAllowedToSee_CanAppear()
    {
        // The planner works on the already-authorized projection: a work a Kids profile may not see is simply absent.
        var one = Episode("One", "1", "1");
        var allowed = Episode("Three", "1", "3");

        var plan = NextEpisodesPlanner.Plan(Show, one.AssetId, 3, [one, allowed])!;

        Assert.Equal(["Three"], plan.Episodes.Select(next => next.Title));
    }
}
