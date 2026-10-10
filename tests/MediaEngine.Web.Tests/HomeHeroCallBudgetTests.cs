using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Contracts.Progress;
using MediaEngine.Web.Components.Cinematic;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>Home must stay cheap: the hero never fetches whole detail pages, and the saved list is fetched once.</summary>
public sealed class HomeHeroCallBudgetTests : AsyncBunitContext
{
    private int _detailCalls;
    private int _savedListCalls;
    private readonly List<DetailEntityType> _statusTypes = [];

    public HomeHeroCallBudgetTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetDetailPageAsync), _ =>
            {
                Interlocked.Increment(ref _detailCalls);
                return Task.FromResult<DetailPageViewModel?>(null);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetSavedItemsAsync), _ =>
            {
                Interlocked.Increment(ref _savedListCalls);
                return Task.FromResult<IReadOnlyList<ProfileSavedItemDto>>([]);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetPersonalStatusAsync), args =>
            {
                lock (_statusTypes)
                {
                    _statusTypes.Add((DetailEntityType)args![0]!);
                }

                return Task.FromResult<PersonalStatusInfo?>(new("r1", 3, 1, 0, false));
            });
        });
        Services.AddSingleton(api);
        Services.AddSingleton(new PlaybackSessionController(null!, api));
        Services.AddScoped<SavedItemService>();
    }

    [Fact]
    public void CyclingHeroSlidesNeverFetchDetailPagesAndFetchSavedListOnce()
    {
        var showId = Guid.NewGuid();
        var episodeHero = new DiscoveryHeroViewModel
        {
            Title = "The Show",
            MediaKind = "Movie",
            WorkId = Guid.NewGuid(),
            EpisodeContext = new(showId, Guid.NewGuid(), Guid.NewGuid(), "The Show", "Ep", 1, 1,
                DisplayContinuationState.Unstarted, null, null),
        };
        var movie = new DiscoveryHeroViewModel { Title = "Movie", MediaKind = "Movie", WorkId = Guid.NewGuid() };
        var book = new DiscoveryHeroViewModel { Title = "Book", MediaKind = "Book", WorkId = Guid.NewGuid() };
        var cut = Render<CinematicHeroCarousel>(p => p.Add(c => c.Items, [episodeHero, movie, book]));

        cut.WaitForAssertion(() => Assert.Single(_statusTypes));
        cut.Find("[aria-label='Next featured item']").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, _statusTypes.Count));
        cut.Find("[aria-label='Next featured item']").Click();
        cut.WaitForAssertion(() => Assert.Equal(3, _statusTypes.Count));

        Assert.Equal(0, _detailCalls);
        Assert.Equal(1, _savedListCalls);
        Assert.Equal(DetailEntityType.TvShow, _statusTypes[0]);
    }
}
