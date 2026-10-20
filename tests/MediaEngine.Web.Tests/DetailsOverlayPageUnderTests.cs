using Bunit;
using MediaEngine.Web.Components.MediaHub;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The page under the details overlay keeps its own state: a shell inside it reads the routed page's address, not the
/// address bar that names the open title, so opening, swapping and dismissing the overlay changes nothing under it.
/// </summary>
public sealed class DetailsOverlayPageUnderTests : AsyncBunitContext
{
    private const string Detail = "/details/book/6b0b8b0a-8c8e-4a5e-9c79-0c2d6d4b4f11";

    private readonly NavigationManager _nav;
    private readonly DetailsOverlayState _overlay;

    public DetailsOverlayPageUnderTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddNativeUiServices();
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.CreateDefault());
        Services.AddScoped<ActiveProfileAccessor>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddSingleton<ViewWorkspaceService>();
        Services.AddSingleton<ViewAssetDragService>();
        _nav = Services.GetRequiredService<NavigationManager>();
        _nav.NavigateTo("/watch?genre=Drama");
        _overlay = new DetailsOverlayState(_nav, new NoHistory());
        _overlay.RememberPage("/watch?genre=Drama", new RouteData(typeof(Blank), new Dictionary<string, object?>()));
        _overlay.Attach();
    }

    private static MediaSectionNavigationGroup[] Navigation() =>
    [
        new("Library",
        [
            new("Drama", "/watch?genre=Drama", AppMaterialIcons.Outlined.Movie, Exact: true),
            new("Comedy", "/watch?genre=Comedy", AppMaterialIcons.Outlined.Movie, Exact: true),
        ]),
    ];

    private IRenderedComponent<MediaSectionShell> RenderShell() =>
        Render<MediaSectionShell>(parameters => parameters
            .Add(shell => shell.Title, "Watch")
            .Add(shell => shell.NavigationGroups, Navigation())
            .AddCascadingValue(_overlay)
            .AddChildContent("<p>content</p>"));

    private int ContentScrolls => JSInterop.Invocations.Count(call => call.Identifier == "scrollElementToTop");

    private static string ActiveLabel(IRenderedComponent<MediaSectionShell> shell) =>
        shell.Find("a.media-section-shell__rail-item[aria-current='page']").TextContent.Trim();

    [Fact]
    public void ASectionShellUnderTheOverlay_KeepsItsActiveEntryAndItsScrollWhileTheOverlayOpensSwapsAndCloses()
    {
        var shell = RenderShell();
        Assert.StartsWith("Drama", ActiveLabel(shell));
        var scrollsBefore = ContentScrolls;

        _nav.NavigateTo(Detail);
        Assert.True(_overlay.IsOpen);
        Assert.StartsWith("Drama", ActiveLabel(shell));

        _nav.NavigateTo("/details/movie/0a1b2c3d-1111-2222-3333-444455556666");
        _nav.NavigateTo("/watch?genre=Drama");
        shell.Render();

        Assert.False(_overlay.IsOpen);
        Assert.StartsWith("Drama", ActiveLabel(shell));
        Assert.Equal(scrollsBefore, ContentScrolls);
    }

    [Fact]
    public void ASectionShell_StillFollowsTheRealNavigationOfItsOwnPage()
    {
        var shell = RenderShell();
        var scrollsBefore = ContentScrolls;

        _nav.NavigateTo("/watch?genre=Comedy");

        shell.WaitForAssertion(() => Assert.StartsWith("Comedy", ActiveLabel(shell)));
        shell.WaitForAssertion(() => Assert.Equal(scrollsBefore + 1, ContentScrolls));
    }

    [Fact]
    public void TheRoutedPageAddress_IgnoresTheOverlayOpeningSwappingAndClosingButFollowsItsOwnNavigation()
    {
        var address = new RoutedPageAddress(_nav, _overlay);
        Assert.Equal("/watch?genre=Drama", address.Current);

        Assert.False(address.Accept(Detail));
        Assert.False(address.Accept("/details/movie/0a1b2c3d-1111-2222-3333-444455556666?tab=cast"));
        Assert.False(address.Accept("/watch?genre=Drama"));
        Assert.Equal("/watch?genre=Drama", address.Current);

        Assert.True(address.Accept("/watch?genre=Comedy"));
        Assert.Equal("/watch?genre=Comedy", address.Current);
        Assert.EndsWith("/watch?genre=Comedy", address.Absolute);
    }

    [Fact]
    public void ASurfaceCreatedWhileTheOverlayIsOpen_SeesThePageUnderItAsItsAddress()
    {
        _nav.NavigateTo(Detail);
        Assert.True(_overlay.IsOpen);

        var address = new RoutedPageAddress(_nav, _overlay);

        Assert.Equal("/watch?genre=Drama", address.Current);
        Assert.EndsWith("/watch?genre=Drama", RoutedPageAddress.AbsoluteFor(_nav, _overlay));
        Assert.Equal(_nav.Uri, RoutedPageAddress.AbsoluteFor(_nav, overlay: null));
    }

    private sealed class Blank : ComponentBase
    {
    }

    private sealed class NoHistory : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int steps) => ValueTask.FromResult(true);
    }
}
