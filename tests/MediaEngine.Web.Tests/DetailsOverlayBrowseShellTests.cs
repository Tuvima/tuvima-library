using Bunit;
using MediaEngine.Web.Components.Browse;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A browse lane under the details overlay keeps its filters: opening, swapping and dismissing a title neither re-reads
/// the lane's address nor reloads its list, while the lane's own navigation still does.
/// </summary>
public sealed class DetailsOverlayBrowseShellTests : AsyncBunitContext
{
    private const string Detail = "/details/movie/6b0b8b0a-8c8e-4a5e-9c79-0c2d6d4b4f11";

    private static readonly LibraryBrowsePreset Preset = new()
    {
        RouteBase = "/watch",
        Title = "Watch",
        HeroVariant = BrowseHeroVariant.Watch,
        Tabs = [new BrowseTabPreset { Id = "movies", Label = "Movies", MediaType = "Movies" }],
    };

    private readonly IEngineApiClient _api;
    private readonly NavigationManager _nav;
    private readonly DetailsOverlayState _overlay;

    public DetailsOverlayBrowseShellTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        _api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton(_api);
        Services.AddSingleton(new DeviceContextService(_api));
        Services.AddSingleton(new DashboardSessionAccessor());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(_api));
        Services.AddSingleton(new MediaReactionService(_api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
        _nav = Services.GetRequiredService<NavigationManager>();
        _nav.NavigateTo("/watch/movies?genre=Drama&sort=title");
        _overlay = new DetailsOverlayState(_nav, new NoHistory());
        _overlay.RememberPage("/watch/movies", new RouteData(typeof(Blank), new Dictionary<string, object?>()));
        _overlay.Attach();
    }

    private int ApiCalls => ((EngineApiClientStub)(object)_api).Calls.Count;

    // The lane loads in the background after it renders; wait until it has stopped calling the Engine.
    private async Task<int> SettledCallsAsync()
    {
        var previous = -1;
        while (previous != ApiCalls)
        {
            previous = ApiCalls;
            await Task.Delay(250);
        }

        return previous;
    }

    private IRenderedComponent<MediaBrowseShell> RenderLane() =>
        Render<MediaBrowseShell>(parameters => parameters
            .Add(shell => shell.Preset, Preset)
            .Add(shell => shell.Tab, "movies")
            .AddCascadingValue(_overlay));

    [Fact]
    public async Task ALaneUnderTheOverlay_IsNotReloadedWhenATitleOpensSwapsOrIsDismissed()
    {
        var lane = RenderLane();
        var callsAfterLoad = await SettledCallsAsync();
        Assert.True(callsAfterLoad > 0, "The lane loads from the Engine when it first renders.");

        _nav.NavigateTo(Detail);
        _nav.NavigateTo("/details/movie/0a1b2c3d-1111-2222-3333-444455556666?tab=cast");
        _nav.NavigateTo("/watch/movies?genre=Drama&sort=title");
        lane.Render();
        await Task.Delay(300);

        Assert.False(_overlay.IsOpen);
        Assert.Equal(callsAfterLoad, ApiCalls);
    }

    [Fact]
    public async Task ALane_StillReloadsWhenItsOwnFiltersChange()
    {
        var lane = RenderLane();
        var callsAfterLoad = await SettledCallsAsync();

        _nav.NavigateTo("/watch/movies?genre=Comedy&sort=title");

        lane.WaitForAssertion(() => Assert.True(ApiCalls > callsAfterLoad, "A change of the lane's own address reloads it."));
    }

    private sealed class Blank : ComponentBase
    {
    }

    private sealed class NoHistory : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int steps) => ValueTask.FromResult(true);
    }
}
