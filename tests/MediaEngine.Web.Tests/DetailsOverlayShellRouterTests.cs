using Bunit;
using MediaEngine.Web.Components.Browse;
using MediaEngine.Web.Components.MediaHub;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The shells inside a routed page under a real Router, in the app's real order (the Router renders for a new address before
/// the shell's own location handler runs): a same-page change of tab and query reads the new tab with the new query.
/// </summary>
public sealed class DetailsOverlayShellRouterTests : AsyncBunitContext
{
    private readonly IEngineApiClient _api;
    private readonly NavigationManager _nav;

    public DetailsOverlayShellRouterTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/app-dialog.js").Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        _api = EngineApiClientStub.CreateDefault();
        Services.AddSingleton(_api);
        Services.AddSingleton(new DeviceContextService(_api));
        Services.AddSingleton(new DashboardSessionAccessor());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<ActiveProfileAccessor>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(_api));
        Services.AddSingleton(new MediaReactionService(_api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton<ViewWorkspaceService>();
        Services.AddSingleton<ViewAssetDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
        Services.AddSingleton<IDetailsOverlayHistory>(new NoHistory());
        Services.AddScoped<DetailsOverlayState>();
        _nav = Services.GetRequiredService<NavigationManager>();
    }

    private int ApiCalls => ((EngineApiClientStub)(object)_api).Calls.Count;

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

    private sealed class NoHistory : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int steps) => ValueTask.FromResult(true);
    }

    [Fact]
    public async Task ALaneThatChangesTabAndQueryInOneNavigation_LoadsOnceWithTheNewQuery()
    {
        _nav.NavigateTo("/router-shell/browse/movies?genre=Drama");
        Render<RouterHarness>();
        var oneLoad = await SettledCallsAsync();
        Assert.True(oneLoad > 0, "The lane loads from the Engine when it first renders.");

        // The Router renders the new tab before the shell's own location handler runs. Reading the new tab with the old
        // query would load the lane twice, the first time for a tab and query that never existed together.
        _nav.NavigateTo("/router-shell/browse/tv?status=Ended");
        var afterTabChange = await SettledCallsAsync();

        Assert.Equal(oneLoad, afterTabChange - oneLoad);
    }

    [Fact]
    public async Task ASectionShellThatChangesItsAddress_RendersOnceForTheNewAddressWithTheNewActiveEntry()
    {
        _nav.NavigateTo("/router-shell/section?genre=Drama");
        var cut = Render<RouterHarness>();
        var shell = cut.FindComponent<MediaSectionShell>();
        Assert.StartsWith("Drama", shell.Find("a.media-section-shell__rail-item[aria-current='page']").TextContent.Trim());
        var rendersBefore = shell.RenderCount;

        _nav.NavigateTo("/router-shell/section?genre=Comedy");
        await Task.Delay(300);

        Assert.StartsWith("Comedy", shell.Find("a.media-section-shell__rail-item[aria-current='page']").TextContent.Trim());
        Assert.Equal(rendersBefore + 1, shell.RenderCount);
    }
}

/// <summary>A routed page with a section shell whose active entry follows the address.</summary>
[Route("/router-shell/section")]
public sealed class RouterSectionPage : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<MediaSectionShell>(0);
        builder.AddComponentParameter(1, nameof(MediaSectionShell.Title), "Watch");
        builder.AddComponentParameter(2, nameof(MediaSectionShell.NavigationGroups), (IReadOnlyList<MediaSectionNavigationGroup>)
        [
            new("Library",
            [
                new("Drama", "/router-shell/section?genre=Drama", AppMaterialIcons.Outlined.Movie, Exact: true),
                new("Comedy", "/router-shell/section?genre=Comedy", AppMaterialIcons.Outlined.Movie, Exact: true),
            ]),
        ]);
        builder.CloseComponent();
    }
}

/// <summary>A routed page with a browse lane whose tab is a route value, like Watch.</summary>
[Route("/router-shell/browse/{Tab?}")]
public sealed class RouterBrowsePage : ComponentBase
{
    public static readonly LibraryBrowsePreset Preset = new()
    {
        RouteBase = "/router-shell/browse",
        Title = "Watch",
        HeroVariant = BrowseHeroVariant.Watch,
        Tabs =
        [
            new BrowseTabPreset { Id = "movies", Label = "Movies", MediaType = "Movies" },
            new BrowseTabPreset { Id = "tv", Label = "TV", MediaType = "TV" },
        ],
    };

    [Parameter] public string? Tab { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<MediaBrowseShell>(0);
        builder.AddComponentParameter(1, nameof(MediaBrowseShell.Preset), Preset);
        builder.AddComponentParameter(2, nameof(MediaBrowseShell.Tab), Tab);
        builder.CloseComponent();
    }
}
