using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Tests;

public sealed class HomeHeroPickerTests
{
    private static DisplayWorkRow Work(string type, string title, int ageDays = 0) => new()
    {
        WorkId = Guid.NewGuid(),
        AssetId = Guid.NewGuid(),
        MediaType = type,
        Title = title,
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-ageDays),
        BackgroundUrl = "/background.jpg",
    };

    private static DisplayComposerService Composer(
        IReadOnlyList<DisplayWorkRow> works, ProfileHeroSettings settings, IHeroOrderer? orderer = null)
    {
        var cards = new DisplayCardBuilder();
        return new(new Repository(works), cards, new DisplayShelfBuilder(cards),
            heroSettings: new FixedSettings(settings), heroOrderer: orderer);
    }

    [Fact]
    public async Task AProfileLimitedToWatch_NeverGetsABookSlide()
    {
        var works = new[] { Work("Book", "Newest book"), Work("Movie", "A movie", 5), Work("Book", "Another book", 1) };
        var settings = ProfileHeroSettings.Default with { AllowRead = false, AllowListen = false };

        var home = await Composer(works, settings).BuildHomeAsync(profileId: Guid.NewGuid());

        Assert.Equal(["A movie"], home.Spotlights.Select(hero => hero.Title));
        Assert.Equal("A movie", home.Hero!.Title);
    }

    [Fact]
    public async Task TurningTheHeroOff_LeavesHomeWithoutAHero()
    {
        var home = await Composer([Work("Movie", "A movie")], ProfileHeroSettings.Default with { Enabled = false })
            .BuildHomeAsync(profileId: Guid.NewGuid());

        Assert.Empty(home.Spotlights);
        Assert.Null(home.Hero);
        Assert.NotEmpty(home.Shelves);
    }

    [Fact]
    public async Task TheSlideCount_LimitsTheHero()
    {
        var works = Enumerable.Range(0, 6).Select(i => Work("Movie", "Movie " + i, i)).ToList();

        var home = await Composer(works, ProfileHeroSettings.Default with { SlideCount = 2 }).BuildHomeAsync(profileId: Guid.NewGuid());

        Assert.Equal(["Movie 0", "Movie 1"], home.Spotlights.Select(hero => hero.Title));
    }

    [Fact]
    public async Task ContinuedOnly_LeavesOutNewItems()
    {
        var home = await Composer([Work("Movie", "Fresh movie")], ProfileHeroSettings.Default with { ContinuedOnly = true })
            .BuildHomeAsync(profileId: Guid.NewGuid());

        Assert.Empty(home.Spotlights);
    }

    [Fact]
    public async Task ASwappedInOrderer_RanksTheSlidesWithoutBreakingTheSettings()
    {
        var works = new[] { Work("Movie", "A movie"), Work("Book", "A book", 1), Work("Movie", "Older movie", 9) };
        var settings = ProfileHeroSettings.Default with { AllowRead = false };

        var home = await Composer(works, settings, new ReverseOrderer()).BuildHomeAsync(profileId: Guid.NewGuid());

        // The default rules would put "A movie" first; the test orderer reverses it, and the book stays out.
        Assert.Equal(["Older movie", "A movie"], home.Spotlights.Select(hero => hero.Title));
    }

    [Fact]
    public async Task AProfileWithNoSavedSettings_GetsTheDefaultHero()
    {
        var cards = new DisplayCardBuilder();
        var composer = new DisplayComposerService(new Repository([Work("Book", "A book")]), cards, new DisplayShelfBuilder(cards));

        var home = await composer.BuildHomeAsync(profileId: Guid.NewGuid());

        Assert.Equal("A book", Assert.Single(home.Spotlights).Title);
    }

    private sealed class ReverseOrderer : IHeroOrderer
    {
        public IReadOnlyList<HeroSlideCandidate> Order(IReadOnlyList<HeroSlideCandidate> allowed) =>
            new DefaultHeroOrderer().Order(allowed).Reverse().ToList();
    }

    private sealed class FixedSettings(ProfileHeroSettings settings) : IProfileHeroSettingsRepository
    {
        public Task<ProfileHeroSettings> GetAsync(Guid profileId, CancellationToken ct = default) => Task.FromResult(settings);
        public Task SaveAsync(Guid profileId, ProfileHeroSettings value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Repository(IReadOnlyList<DisplayWorkRow> works) : IDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => Task.FromResult(works);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);
        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) => Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }
}
