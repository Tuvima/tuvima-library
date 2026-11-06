using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Services;

namespace MediaEngine.Api.Tests;

/// <summary>A folder of Other videos is one Home hero slide that plays the next unfinished video and says where the person is.</summary>
public sealed class HomeVideoFolderHeroTests
{
    [Fact]
    public async Task AFolderNobodyHasStarted_HeroSaysPlayAndStartsItsFirstVideo_WithItsCountsAndAnOpenFolderLink()
    {
        var root = Guid.NewGuid();
        var first = Video(root, "Welcome", "1", "1");
        var home = await Composer(new Repository([Video(root, "Loops", "2", "1"), first, Video(root, "Variables", "1", "2")], [])).BuildHomeAsync();

        var hero = Assert.Single(home.Spotlights);

        Assert.Equal("Python Course", hero.Title);
        Assert.Equal(VideoFolderWording.FolderMediaType, hero.MediaType);
        Assert.False(hero.IsContinued);
        Assert.Equal(DisplayContinuationState.Unstarted, hero.ContinuationState);
        Assert.Equal("Play", hero.Actions[0].Label);
        Assert.Equal($"/watch/player/{first.WorkId:D}", hero.Actions[0].WebUrl);
        Assert.Equal("Open folder", hero.Actions[1].Label);
        Assert.Equal($"/details/tvshow/{root:D}?context=watch", hero.Actions[1].WebUrl);
        Assert.Equal("3 videos · 2 sections", hero.Subtitle);
        Assert.Null(hero.Progress);
    }

    [Fact]
    public async Task AFolderWithAStartedVideo_HeroSaysResumeNamesTheVideoAndTheTimeLeft_AsOneSlide()
    {
        var profile = Guid.NewGuid();
        var root = Guid.NewGuid();
        var first = Video(root, "Welcome", "1", "1");
        var second = Video(root, "Variables", "1", "2");
        var third = Video(root, "Loops", "2", "1");
        var home = await Composer(new Repository([first, second, third], [State(profile, first, 100), State(profile, second, 40)]))
            .BuildHomeAsync(profileId: profile);

        var hero = Assert.Single(home.Spotlights);

        Assert.True(hero.IsContinued);
        Assert.Equal(DisplayContinuationState.InProgress, hero.ContinuationState);
        Assert.Equal("Resume", hero.Actions[0].Label);
        Assert.Equal($"/watch/player/{second.WorkId:D}", hero.Actions[0].WebUrl);
        Assert.Equal("3 videos · 2 sections · Next up: Variables · 25 minutes left", hero.Subtitle);
        Assert.Equal(1500, hero.Progress!.RemainingSeconds);
        Assert.False(VideoFolderWording.UsesTvWording(hero.Subtitle));
    }

    [Fact]
    public async Task AFolderWithEveryVideoFinished_HeroPlaysItFromTheStartAgain()
    {
        var profile = Guid.NewGuid();
        var root = Guid.NewGuid();
        var first = Video(root, "Welcome", "1", "1");
        var second = Video(root, "Variables", "1", "2");
        var home = await Composer(new Repository([first, second], [State(profile, first, 100), State(profile, second, 100)]))
            .BuildHomeAsync(profileId: profile);

        var hero = Assert.Single(home.Spotlights);

        Assert.False(hero.IsContinued);
        Assert.Equal("Play", hero.Actions[0].Label);
        Assert.EndsWith("?restart=true", hero.Actions[0].WebUrl);
        Assert.Equal("2 videos", hero.Subtitle);
    }

    [Fact]
    public async Task AFolderHoldingOneVideo_HeroCountsOneVideoAndPlaysIt()
    {
        var root = Guid.NewGuid();
        var only = Video(root, "Sample Talk", null, null);
        var home = await Composer(new Repository([only, Video(root, "Sample Talk copy", null, null)], [])).BuildHomeAsync();

        var hero = Assert.Single(home.Spotlights);

        Assert.Equal("Play", hero.Actions[0].Label);
        Assert.StartsWith("2 videos", hero.Subtitle);
        Assert.DoesNotContain("section", hero.Subtitle, StringComparison.OrdinalIgnoreCase);
    }

    private static DisplayWorkRow Video(Guid root, string title, string? section, string? number) => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        RootWorkId = root,
        MediaType = "Videos",
        Title = title,
        ShowName = "Python Course",
        SeasonNumber = section,
        EpisodeNumber = number,
        CoverUrl = "/frame.jpg",
        BackgroundUrl = "/frame.jpg",
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
    };

    private static DisplayJourneyRow State(Guid profile, DisplayWorkRow video, double percent) => new()
    {
        ProfileId = profile,
        WorkId = video.WorkId,
        RootWorkId = video.RootWorkId,
        AssetId = video.AssetId,
        MediaType = "Videos",
        ProgressPct = percent,
        LastAccessed = DateTimeOffset.UtcNow,
        Title = video.Title,
        ShowName = video.ShowName,
        SeasonNumber = video.SeasonNumber,
        EpisodeNumber = video.EpisodeNumber,
        BackgroundUrl = video.BackgroundUrl,
        CoverUrl = video.CoverUrl,
        PositionSeconds = 300,
        DurationSeconds = 1800,
    };

    private static DisplayComposerService Composer(Repository repository)
    {
        var cards = new DisplayCardBuilder();
        return new(repository, cards, new DisplayShelfBuilder(cards));
    }

    private sealed class Repository(IReadOnlyList<DisplayWorkRow> works, List<DisplayJourneyRow> states) : IDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => Task.FromResult(works);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayJourneyRow>>(states.Where(row => row.ProfileId == profileId).ToList());
        public async Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            (await LoadStatesAsync(profileId, lane, ct)).Where(row => row.ProgressPct is > 0 and < 99.5).ToList();
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }
}
