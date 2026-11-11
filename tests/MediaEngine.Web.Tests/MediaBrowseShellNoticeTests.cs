using System.Reflection;
using Bunit;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
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
/// Hearting a title in a browse lane: the heart on the tile is the confirmation, so a kept favorite adds no words; one that
/// could not be kept says why in an alert at the top of the lane, which stays until the next action or the close button.
/// </summary>
public sealed class MediaBrowseShellNoticeTests : AsyncBunitContext
{
    private static readonly LibraryBrowsePreset Preset = new()
    {
        RouteBase = "/watch",
        Title = "Watch",
        HeroVariant = BrowseHeroVariant.Watch,
        Tabs = [new BrowseTabPreset { Id = "movies", Label = "Movies", MediaType = "Movies" }],
    };

    private static readonly Guid Movie = Guid.Parse("6b0b8b0a-8c8e-4a5e-9c79-0c2d6d4b4f11");

    private bool _saveSucceeds = true;
    private int _saveCalls;
    private readonly NavigationManager _nav;
    private readonly DetailsOverlayState _overlay;

    public MediaBrowseShellNoticeTests() => (_nav, _overlay) = Setup();

    private (NavigationManager Nav, DetailsOverlayState Overlay) Setup()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
            {
                new(TestSessions.Profile, "Test User", "#C9922E", "Standard", DateTimeOffset.UtcNow),
            }));

            stub.SetHandler(nameof(IEngineApiClient.SaveItemAsync), args =>
            {
                _saveCalls++;
                return Task.FromResult<ProfileStateMutationDto?>(_saveSucceeds
                    ? new ProfileStateMutationDto((ProfileEntityKind)args![0]!, (Guid)args[1]!, true, null, DateTimeOffset.UtcNow)
                    : null);
            });
        });
        Services.AddSingleton(api);
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddSingleton(TestSessions.For(kids: false, administrator: false));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/watch/movies");
        var overlay = new DetailsOverlayState(nav, new NoHistory());
        overlay.RememberPage("/watch/movies", new RouteData(typeof(Blank), new Dictionary<string, object?>()));
        overlay.Attach();
        return (nav, overlay);
    }

    [Fact]
    public async Task AFavoriteThatIsKept_AddsNoNotice()
    {
        var lane = RenderLane();

        await ToggleFavoriteAsync(lane);

        Assert.Equal(1, _saveCalls);
        Assert.Empty(lane.FindAll("[data-browse-notice]"));
    }

    [Fact]
    public async Task AFavoriteTheEngineDoesNotKeep_SaysSoInAnAlertAtTheTopUntilDismissed()
    {
        _saveSucceeds = false;
        var lane = RenderLane();

        await ToggleFavoriteAsync(lane);

        lane.WaitForAssertion(() => Assert.Single(lane.FindAll("[data-browse-notice]")));
        Assert.Equal(1, _saveCalls);

        await lane.Find("[data-browse-notice] .tl-alert-close").ClickAsync();

        Assert.Empty(lane.FindAll("[data-browse-notice]"));
    }

    private IRenderedComponent<MediaBrowseShell> RenderLane()
    {
        var lane = Render<MediaBrowseShell>(parameters => parameters
            .Add(shell => shell.Preset, Preset)
            .Add(shell => shell.Tab, "movies")
            .AddCascadingValue(_overlay));
        // Give the first load time to finish so the active profile is known.
        lane.WaitForAssertion(() => Assert.NotNull(typeof(MediaBrowseShell)
            .GetField("_activeProfileId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lane.Instance)));
        return lane;
    }

    // The tile's heart calls this handler; invoking it directly skips the markup that wires it, so draw the result afterwards.
    private static async Task ToggleFavoriteAsync(IRenderedComponent<MediaBrowseShell> lane)
    {
        await lane.InvokeAsync(() => (Task)typeof(MediaBrowseShell)
            .GetMethod("ToggleFavoriteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(lane.Instance, [Movie])!);
        lane.Render();
    }

    private sealed class Blank : ComponentBase
    {
    }

    private sealed class NoHistory : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int steps) => ValueTask.FromResult(true);
    }
}
