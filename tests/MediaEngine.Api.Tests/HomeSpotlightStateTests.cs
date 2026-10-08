using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;

namespace MediaEngine.Api.Tests;

public sealed class HomeSpotlightStateTests
{
    [Fact]
    public async Task FiveSpotlightsCoverAvailableMediaKindsAndDeduplicateRankedCandidates()
    {
        var tv = Episode(Guid.NewGuid(), 5, "TV owned");
        DisplayWorkRow Work(string type, string title) => new()
        {
            WorkId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            MediaType = type,
            Title = title,
            IsIdentityReady = true,
            CreatedAt = DateTimeOffset.UtcNow,
            BackgroundUrl = "/background.jpg",
        };
        var movies = Enumerable.Range(0, 8).Select(i => Work("Movie", "Movie " + i)).ToList();
        var book = Work("Book", "Book"); var audio = Work("Audiobook", "Audiobook");
        var track = Work("Music", "Track"); track.RootWorkId = Guid.NewGuid(); track.Album = "Album"; track.Artist = "Artist";
        var home = await Composer(new Repository([tv, .. movies, book, audio, track], [])).BuildHomeAsync();
        Assert.Equal(5, home.Spotlights.Count);
        Assert.Single(home.Spotlights, h => h.Subject == DisplaySubjectKind.TvShow);
        Assert.Single(home.Spotlights, h => h.MediaType == "Movie");
        Assert.Single(home.Spotlights, h => h.MediaType == "Book");
        Assert.Single(home.Spotlights, h => h.Subject == DisplaySubjectKind.Album);
        Assert.Single(home.Spotlights, h => h.MediaType == "Audiobook");
        Assert.Equal(5, home.Spotlights.Select(h => h.Id).Distinct().Count());
    }

    [Fact]
    public async Task MostRecentlySavedStartedShowIsTvRepresentative()
    {
        var profile = Guid.NewGuid();
        var older = Episode(Guid.NewGuid(), 5, "Older partial");
        var newest = Episode(Guid.NewGuid(), 5, "Newer partial");
        var untouched = Episode(Guid.NewGuid(), 5, "Newest catalogue");
        untouched.CreatedAt = DateTimeOffset.UtcNow.AddDays(1);
        var olderState = State(profile, older, 42); olderState.LastAccessed = DateTimeOffset.UtcNow.AddHours(-1);
        var latestState = State(profile, newest, 35); latestState.LastAccessed = DateTimeOffset.UtcNow;
        var home = await Composer(new Repository([untouched, older, newest], [olderState, latestState])).BuildHomeAsync(profileId: profile);
        Assert.Equal(newest.RootWorkId, home.Spotlights[0].EpisodeContext!.ShowWorkId);
        Assert.Equal(newest.WorkId, home.Spotlights[0].WorkId);
        Assert.Equal("Resume Episode", home.Spotlights[0].Actions[0].Label);
        Assert.Equal(latestState.EpisodeStillUrl, home.Spotlights[0].Artwork.BackgroundUrl);
    }

    [Fact]
    public async Task AllOwnedCompletedRetainsFirstOwnedRestartWithExactEpisodePresentation()
    {
        var profile = Guid.NewGuid(); var show = Guid.NewGuid();
        var first = Episode(show, 5, "First owned"); var later = Episode(show, 7, "Later owned");
        var firstState = State(profile, first, 100);
        firstState.Title = "Exact completed episode";
        firstState.Description = "Exact completed episode synopsis";
        firstState.EpisodeStillUrl = "/completed-still.jpg";
        var home = await Composer(new Repository([later, first], [State(profile, later, 100), firstState])).BuildHomeAsync(profileId: profile);
        var hero = Assert.Single(home.Spotlights);
        Assert.Equal("The Show", hero.Title);
        Assert.Equal("S2 E5 · Exact completed episode", hero.Subtitle);
        Assert.Equal(firstState.Description, hero.Description);
        Assert.Equal(firstState.EpisodeStillUrl, hero.Artwork.BackgroundUrl);
        Assert.Equal(first.WorkId, hero.WorkId);
        Assert.Equal(first.WorkId, hero.EpisodeContext!.EpisodeWorkId);
        Assert.Equal(firstState.AssetId, hero.EpisodeContext.EpisodeAssetId);
        Assert.Equal(show, hero.EpisodeContext.ShowWorkId);
        Assert.Equal(DisplayContinuationState.Completed, hero.ContinuationState);
        Assert.Equal(DisplayContinuationState.Completed, hero.EpisodeContext.State);
        Assert.Null(hero.Progress);
        Assert.Equal("Restart Episode", hero.Actions[0].Label);
        Assert.Equal(firstState.AssetId, hero.Actions[0].AssetId);
        Assert.Equal($"/watch/player/{first.WorkId:D}?restart=true", hero.Actions[0].WebUrl);
        Assert.Equal(TvEpisodeDetailRoute.Build(show, first.WorkId), hero.Actions[1].WebUrl);
        Assert.DoesNotContain(home.Shelves, shelf => shelf.Key == "continue");
    }

    [Fact]
    public async Task AllOwnedCompletedMissingStillFallsBackToShowWithoutLosingEpisodeCopy()
    {
        var profile = Guid.NewGuid(); var show = Guid.NewGuid();
        var owned = Episode(show, 5, "Completed owned");
        owned.EpisodeStillUrl = null; owned.BackgroundUrl = "/inherited-background.jpg";
        var home = await Composer(new Repository([owned], [State(profile, owned, 100)])).BuildHomeAsync(profileId: profile);
        var hero = Assert.Single(home.Spotlights);
        Assert.Equal("/show.jpg", hero.Artwork.BackgroundUrl);
        Assert.Equal("S2 E5 · Completed owned", hero.Subtitle);
        Assert.Equal(owned.Description, hero.Description);
        Assert.Equal(DisplayContinuationState.Completed, hero.ContinuationState);
        Assert.Null(hero.Progress);
        Assert.Equal("Restart Episode", hero.Actions[0].Label);
        Assert.Equal(TvEpisodeDetailRoute.Build(show, owned.WorkId), hero.Actions[1].WebUrl);
    }

    [Fact]
    public async Task ResumeUsesSavedEditionAssetAndExactEpisodeMetadata()
    {
        var profile = Guid.NewGuid(); var episode = Episode(Guid.NewGuid(), 5, "Catalogue edition title");
        var saved = State(profile, episode, 42);
        saved.AssetId = Guid.NewGuid(); saved.Title = "Exact saved episode";
        saved.Description = "Exact episode synopsis"; saved.EpisodeStillUrl = "/saved-asset-still.jpg";
        var home = await Composer(new Repository([episode], [saved])).BuildHomeAsync(profileId: profile);
        Assert.Equal(saved.AssetId, home.Hero!.Actions[0].AssetId);
        Assert.Equal(saved.AssetId, home.Hero.EpisodeContext!.EpisodeAssetId);
        Assert.Equal(saved.Description, home.Hero.Description);
        Assert.Equal("S2 E5 · Exact saved episode", home.Hero.Subtitle);
        Assert.Equal(saved.EpisodeStillUrl, home.Hero.Artwork.BackgroundUrl);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(42, true)]
    [InlineData(100, true)]
    public async Task OwnedEpisodeSelectionPreservesExactIdentityAndShowDeduplication(double progress, bool started)
    {
        var profile = Guid.NewGuid(); var show = Guid.NewGuid();
        var first = Episode(show, 5, "The selected episode");
        var next = Episode(show, 7, "The next owned episode");
        var repository = new Repository([first, next], [State(profile, first, progress)]);
        var home = await Composer(repository).BuildHomeAsync(profileId: profile);
        var hero = Assert.Single(home.Spotlights, item => item.Subject == DisplaySubjectKind.TvShow);
        var target = progress >= 99.5 ? next : first;
        Assert.Equal("The Show", hero.Title);
        Assert.Equal(target.WorkId, hero.EpisodeContext!.EpisodeWorkId);
        Assert.Equal(target.AssetId, hero.Actions[0].AssetId);
        Assert.Equal(started ? TvEpisodeDetailRoute.Build(show, target.WorkId) : $"/details/tvshow/{show:D}?context=watch", hero.Actions[1].WebUrl);
        Assert.Equal(show, hero.EpisodeContext.ShowWorkId);
        Assert.Equal(started ? target.BackgroundUrl : "/show.jpg", hero.Artwork.BackgroundUrl);
        if (started)
        {
            Assert.Equal($"S2 E{target.EpisodeNumber} · {target.Title}", hero.Subtitle);
            Assert.Equal(target.Description, hero.Description);
        }
        if (progress == 42)
        {
            Assert.Equal("Resume Episode", hero.Actions[0].Label);
            Assert.Equal(600, hero.Progress!.RemainingSeconds);
            var card = Assert.Single(home.Shelves.Single(shelf => shelf.Key == "continue").Items);
            Assert.Equal(hero.EpisodeContext.EpisodeWorkId, card.EpisodeContext!.EpisodeWorkId);
            Assert.Equal(TvEpisodeDetailRoute.Build(show, first.WorkId), card.Actions[1].WebUrl);
        }
        if (progress >= 99.5)
        {
            Assert.Null(hero.Progress);
            Assert.DoesNotContain(home.Shelves, shelf => shelf.Key == "continue");
            Assert.Equal("Watch Next Episode", hero.Actions[0].Label);
        }
    }

    [Fact]
    public async Task MissingStillUsesShowBackdropAndProviderOnlyEpisodeCannotBecomeTarget()
    {
        var profile = Guid.NewGuid(); var show = Guid.NewGuid();
        var owned = Episode(show, 5, "Owned"); owned.EpisodeStillUrl = null; owned.BackgroundUrl = "/inherited-show.jpg";
        var provider = Episode(show, 6, "Provider only"); provider.AssetId = Guid.Empty;
        var home = await Composer(new Repository([owned, provider], [State(profile, owned, 42)])).BuildHomeAsync(profileId: profile);
        var hero = Assert.Single(home.Spotlights);
        Assert.Equal(owned.WorkId, hero.EpisodeContext!.EpisodeWorkId);
        Assert.Equal("/show.jpg", hero.Artwork.BackgroundUrl);
        Assert.Equal("Synopsis for Owned", hero.Description);
    }

    [Fact]
    public async Task ProfileSwitchAndCommittedStateChangeRefreshHomeWithoutCatalogueRefresh()
    {
        var firstProfile = Guid.NewGuid(); var secondProfile = Guid.NewGuid();
        var episode = Episode(Guid.NewGuid(), 5, "Owned");
        var repository = new Repository([episode], [State(firstProfile, episode, 42)]);
        var composer = Composer(repository);
        Assert.NotNull((await composer.BuildHomeAsync(profileId: firstProfile)).Hero!.Progress);
        Assert.Null((await composer.BuildHomeAsync(profileId: secondProfile)).Hero!.Progress);
        repository.States[0].ProgressPct = 100;
        var complete = await composer.BuildHomeAsync(profileId: firstProfile);
        Assert.Equal(DisplayContinuationState.Completed, complete.Hero!.ContinuationState);
        Assert.Null(complete.Hero.Progress);
    }

    [Theory]
    [InlineData("Movie", 0, DisplayContinuationState.Unstarted)]
    [InlineData("Movie", 42, DisplayContinuationState.InProgress)]
    [InlineData("Movie", 100, DisplayContinuationState.Completed)]
    [InlineData("Book", 42, DisplayContinuationState.InProgress)]
    [InlineData("Audiobook", 42, DisplayContinuationState.InProgress)]
    [InlineData("Music", 42, DisplayContinuationState.Unstarted)]
    public void LongFormStateAndSavedTimingAreExplicitMusicHasNoProgress(string kind, double percent, DisplayContinuationState expected)
    {
        var row = new DisplayJourneyRow
        {
            WorkId = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            MediaType = kind,
            ProgressPct = percent,
            PositionSeconds = 300,
            DurationSeconds = 900,
            Runtime = "200",
            Title = "Title"
        };
        var card = new DisplayCardBuilder().FromJourney(row, "home");
        Assert.Equal(expected, card.ContinuationState);
        if (kind == "Music")
        {
            Assert.Null(card.Progress);
        }
        else
        {
            Assert.Equal(600, card.Progress!.RemainingSeconds);
            row.DurationSeconds = -1;
            Assert.Null(new DisplayCardBuilder().FromJourney(row, "home").Progress!.RemainingSeconds);
        }
    }

    private static DisplayWorkRow Episode(Guid show, int number, string title) => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        RootWorkId = show,
        MediaType = "TV",
        WorkKind = "child",
        IsIdentityReady = true,
        Title = title,
        ShowName = "The Show",
        SeasonNumber = "2",
        EpisodeNumber = number.ToString(),
        RootBackgroundUrl = "/show.jpg",
        RootDescription = "Show synopsis",
        Description = $"Synopsis for {title}",
        EpisodeStillUrl = $"/episode-{number}.jpg",
        BackgroundUrl = $"/episode-{number}.jpg",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static DisplayJourneyRow State(Guid profile, DisplayWorkRow episode, double percent) => new()
    {
        ProfileId = profile,
        WorkId = episode.WorkId,
        RootWorkId = episode.RootWorkId,
        AssetId = episode.AssetId,
        MediaType = "TV",
        ProgressPct = percent,
        LastAccessed = DateTimeOffset.UtcNow,
        Title = episode.Title,
        ShowName = episode.ShowName,
        SeasonNumber = episode.SeasonNumber,
        EpisodeNumber = episode.EpisodeNumber,
        BackgroundUrl = episode.BackgroundUrl,
        EpisodeStillUrl = episode.EpisodeStillUrl,
        Description = episode.Description,
        PositionSeconds = 300,
        DurationSeconds = 900,
    };
    private static DisplayComposerService Composer(Repository repository)
    {
        var cards = new DisplayCardBuilder(); return new(repository, cards, new DisplayShelfBuilder(cards));
    }
    private sealed class Repository(IReadOnlyList<DisplayWorkRow> works, List<DisplayJourneyRow> states) : IDisplayProjectionReadService
    {
        public List<DisplayJourneyRow> States { get; } = states;
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => Task.FromResult(works);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayJourneyRow>>(States.Where(row => row.ProfileId == profileId).ToList());
        public async Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            (await LoadStatesAsync(profileId, lane, ct)).Where(row => row.ProgressPct is > 0 and < 99.5).ToList();
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }
}
