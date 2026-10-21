using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Tests;

/// <summary>How the owned story reads Wikidata work items and a profile's progress for the spoiler rule: pure, no data store.</summary>
public sealed class OwnedStoryTests
{
    private static readonly Guid Show = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Film = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Entity = Guid.Parse("00000000-0000-0000-0000-0000000000e1");

    private static DisplayWorkRow Episode(Guid work, string qid, string? season, string? number) => new()
    {
        WorkId = work,
        RootWorkId = Show,
        MediaType = "TV",
        IdentityQid = qid,
        SeasonNumber = season,
        EpisodeNumber = number,
    };

    private static DisplayWorkRow FilmRow() => new()
    {
        WorkId = Film,
        RootWorkId = Film,
        MediaType = "Movies",
        IdentityQid = "Q-film",
    };

    private static FictionalEntityWorkLink Link(string workQid, string linkType = "present_in_work") =>
        new(Entity, workQid, null, linkType);

    private static OwnedStory Story(params DisplayWorkRow[] rows) =>
        new(rows, new HashSet<string>(rows.Where(row => row.IdentityQid != "Q-show").Select(row => row.IdentityQid!), StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void AnEpisodesOwnIdentity_IsThatEpisode_WhileTheShowsIdentityIsTheShowWithNoPosition()
    {
        var story = Story(
            Episode(Guid.NewGuid(), "Q-ep1", "1", "1"),
            Episode(Guid.NewGuid(), "Q-ep2", "2", "05"),
            Episode(Guid.NewGuid(), "Q-show", "1", "3"));

        Assert.Equal(new WorkPosition(Show, new EpisodePosition(1, 1)), Assert.Single(story.Resolve("Q-ep1")));
        Assert.Equal(new WorkPosition(Show, new EpisodePosition(2, 5)), Assert.Single(story.Resolve("q-ep2")));
        Assert.Equal(new WorkPosition(Show, null), Assert.Single(story.Resolve("Q-show")));
        Assert.Empty(story.Resolve("Q-not-owned"));
    }

    [Fact]
    public void AShowsOwnIdentity_IsVisibleAndNamesTheShow_EvenWhenEveryEpisodeHasItsOwn()
    {
        var rows = new[]
        {
            Episode(Guid.NewGuid(), "Q-ep1", "1", "1"),
            Episode(Guid.NewGuid(), "Q-ep2", "1", "2"),
        };
        var story = new OwnedStory(
            rows,
            new HashSet<string>(["Q-ep1", "Q-ep2"], StringComparer.OrdinalIgnoreCase),
            new Dictionary<Guid, string> { [Show] = "Q-the-show", [Guid.NewGuid()] = "Q-unrelated" });

        Assert.Equal(new WorkPosition(Show, null), Assert.Single(story.Resolve("q-the-show")));
        Assert.Contains("Q-the-show", story.VisibleQids);
        Assert.Contains("Q-ep1", story.VisibleQids);
        Assert.DoesNotContain("Q-unrelated", story.VisibleQids);
        Assert.Equal(2, story.RowsFor("Q-the-show").Count);
        Assert.Single(story.RowsFor("Q-ep2"));
    }

    [Fact]
    public void AnEpisodeWithNoSeasonReadsAsSeasonOne_AndOneWithNoNumberHasNoPosition()
    {
        var story = Story(
            Episode(Guid.NewGuid(), "Q-ep1", null, "4"),
            Episode(Guid.NewGuid(), "Q-ep2", "1", null),
            Episode(Guid.NewGuid(), "Q-ep3", "1", "6-7"));

        Assert.Equal(new EpisodePosition(1, 4), Assert.Single(story.Resolve("Q-ep1")).Episode);
        Assert.Null(Assert.Single(story.Resolve("Q-ep2")).Episode);
        Assert.Equal(new EpisodePosition(1, 6), Assert.Single(story.Resolve("Q-ep3")).Episode);
    }

    [Fact]
    public void FirstSeen_IsTheEarliestOwnedEpisode_AndAFirstAppearanceLinkWinsOverAPresenceLink()
    {
        var story = Story(
            Episode(Guid.NewGuid(), "Q-ep1", "1", "1"),
            Episode(Guid.NewGuid(), "Q-ep2", "1", "2"),
            Episode(Guid.NewGuid(), "Q-ep3", "1", "3"));

        var presenceOnly = story.FirstSeen([Link("Q-ep3"), Link("Q-ep2")]);
        var withFirstAppearance = story.FirstSeen([Link("Q-ep1"), Link("Q-ep3", "first_appearance"), Link("Q-ep2")]);

        Assert.Equal(new WorkPosition(Show, new EpisodePosition(1, 2)), Assert.Single(presenceOnly));
        Assert.Equal(new WorkPosition(Show, new EpisodePosition(1, 3)), Assert.Single(withFirstAppearance));
    }

    [Fact]
    public void FirstSeen_IsTheTitleAlone_WhenTheLinksNameOnlyTheWholeWork_AndOneEntryPerTitle()
    {
        var story = Story(
            Episode(Guid.NewGuid(), "Q-ep1", "1", "1"),
            Episode(Guid.NewGuid(), "Q-show", "1", "2"),
            FilmRow());

        var positions = story.FirstSeen([Link("Q-show"), Link("Q-film")]);

        Assert.Equal(2, positions.Count);
        Assert.Contains(new WorkPosition(Show, null), positions);
        Assert.Contains(new WorkPosition(Film, null), positions);
    }

    [Fact]
    public void Progress_IsStartedOnceAnEpisodeIs_AndFinishedOnlyWhenEveryOwnedEpisodeIs()
    {
        var one = Guid.NewGuid();
        var two = Guid.NewGuid();
        var three = Guid.NewGuid();
        var story = Story(
            Episode(one, "Q-ep1", "1", "1"),
            Episode(two, "Q-ep2", "1", "2"),
            Episode(three, "Q-ep3", "1", "3"));

        var partway = story.ProgressOf([new ViewerWorkProgress(one, 100), new ViewerWorkProgress(two, 30)]).For(Show);
        var all = story.ProgressOf(
            [new ViewerWorkProgress(one, 100), new ViewerWorkProgress(two, 99.5), new ViewerWorkProgress(three, 100)]).For(Show);

        Assert.True(partway.Started);
        Assert.False(partway.Finished);
        Assert.Equal(new EpisodePosition(1, 2), partway.FurthestStartedEpisode);
        Assert.Equal(new EpisodePosition(1, 1), partway.FurthestFinishedEpisode);
        Assert.True(all.Finished);
        Assert.Equal(new EpisodePosition(1, 3), all.FurthestFinishedEpisode);
    }

    [Fact]
    public void Progress_IsNothing_ForATitleTheProfileHasNotStarted()
    {
        var story = Story(Episode(Guid.NewGuid(), "Q-ep1", "1", "1"), FilmRow());

        var viewer = story.ProgressOf([new ViewerWorkProgress(Film, 100)]);

        Assert.False(viewer.For(Show).Started);
        Assert.True(viewer.For(Film).Finished);
        Assert.Null(viewer.For(Film).FurthestStartedEpisode);
    }

    [Fact]
    public async Task TheStory_IsBuiltOncePerRequest_HoweverManyTimesItIsAsked()
    {
        var display = new CountingDisplay([Episode(Guid.NewGuid(), "Q-ep1", "1", "1")]);
        var reader = new CharacterFirstSeenReader(display, new NoProgress());

        var first = await reader.LoadAsync();
        var second = await reader.LoadAsync();

        Assert.Same(first, second);
        Assert.Equal(1, display.Loads);
    }

    private sealed class CountingDisplay(IReadOnlyList<DisplayWorkRow> rows) : IDisplayProjectionReadService
    {
        public int Loads { get; private set; }

        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct)
        {
            Loads++;
            return Task.FromResult(rows);
        }

        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) => LoadJourneyAsync(profileId, lane, ct);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }

    private sealed class NoProgress : IViewerProgressRepository
    {
        public Task<IReadOnlyList<ViewerWorkProgress>> GetStartedWorksAsync(Guid profileId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ViewerWorkProgress>>([]);

        public Task<IReadOnlyDictionary<Guid, string>> GetWorkQidsAsync(IEnumerable<Guid> workIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task<IReadOnlySet<string>> GetChildWorkQidsAsync(IEnumerable<string> workQids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    [Fact]
    public void ACharacterCountsOncePerTitle_NotOncePerEpisode()
    {
        var story = Story(
            Episode(Guid.NewGuid(), "Q-ep1", "1", "1"),
            Episode(Guid.NewGuid(), "Q-ep2", "1", "2"),
            FilmRow());
        var view = new CharacterSpoilerView(SpoilerLevel.UpToProgress, story, ViewerProgress.Empty);

        Assert.Equal(2, view.CountWorks([Link("Q-ep1"), Link("Q-ep2"), Link("Q-film"), Link("Q-film")]));
        Assert.Equal(1, view.CountWorks([Link("Q-gone"), Link("q-gone")]));
    }
}
