using Bunit;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Navigation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The overlay with the real Router and the real order of events: the Router has already subscribed to location changes
/// when the overlay's state sees one, and the page under the overlay must still survive a click on a detail link.
/// </summary>
public sealed class DetailsOverlayRouterTests : AsyncBunitContext
{
    private const string TitleAddress = "/details/book/6b0b8b0a-8c8e-4a5e-9c79-0c2d6d4b4f11";

    private readonly NavigationManager _nav;

    public DetailsOverlayRouterTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/app-dialog.js").Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDetailsOverlayHistory>(new NoHistory());
        Services.AddScoped<DetailsOverlayState>();
        RouterListenPage.Instances.Clear();
        _nav = Services.GetRequiredService<NavigationManager>();
    }

    [Fact]
    public void ClickingADetailLink_OpensTheOverlayOverThePageAndKeepsThatPageAlive()
    {
        _nav.NavigateTo("/router-test/listen?x=1");
        var cut = Render<RouterHarness>();
        var listen = Assert.Single(RouterListenPage.Instances);
        Assert.Empty(cut.FindAll("dialog[data-details-overlay]"));

        _nav.NavigateTo(TitleAddress);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("dialog[data-details-overlay]")));
        Assert.Contains("title", cut.Find("dialog #router-detail").TextContent);
        Assert.Same(listen, Assert.Single(RouterListenPage.Instances));
        Assert.Equal("/router-test/listen?x=1", listen.RoutedUri);
        Assert.Single(cut.FindAll("#router-detail"));
    }

    [Fact]
    public void DismissingTheOverlay_LeavesTheSamePageInstanceAndItsAddress()
    {
        _nav.NavigateTo("/router-test/listen?x=1");
        var cut = Render<RouterHarness>();
        var listen = Assert.Single(RouterListenPage.Instances);
        _nav.NavigateTo(TitleAddress);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("dialog[data-details-overlay]")));

        // What history.go(-1) does when the overlay asks to step back.
        _nav.NavigateTo("/router-test/listen?x=1");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("dialog[data-details-overlay]")));
        Assert.Same(listen, Assert.Single(RouterListenPage.Instances));
        Assert.Equal(1, listen.RenderCount);
    }

    [Fact]
    public void WhenTheOverlayStateIsCreatedAfterTheRouter_TheOverlayStillOpensOverTheSurvivingPage()
    {
        _nav.NavigateTo("/router-test/listen?x=1");
        var cut = Render<LateStateRouterHarness>();
        var listen = Assert.Single(RouterListenPage.Instances);

        _nav.NavigateTo(TitleAddress);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("dialog[data-details-overlay]")));
        Assert.Same(listen, Assert.Single(RouterListenPage.Instances));
        Assert.Single(cut.FindAll("#router-detail"));
    }

    [Fact]
    public void TheRoutesComponentCreatesTheOverlayStateBeforeTheRouter()
    {
        var routes = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Components", "Routes.razor"));

        Assert.Contains("@inject DetailsOverlayState", routes, StringComparison.Ordinal);
        Assert.True(
            routes.IndexOf("@inject DetailsOverlayState", StringComparison.Ordinal) < routes.IndexOf("<Router", StringComparison.Ordinal),
            "Routes must inject the overlay state so it subscribes to location changes before the Router does.");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private sealed class NoHistory : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int steps) => ValueTask.FromResult(true);
    }
}

/// <summary>Routes.razor in miniature: it injects the overlay state, then renders the Router and the overlay-aware route view.</summary>
public sealed class RouterHarness : ComponentBase
{
    [Inject] private DetailsOverlayState Overlay { get; set; } = default!;

    protected override void BuildRenderTree(RenderTreeBuilder builder) => RouterHarnessMarkup.Build(builder);
}

/// <summary>The same without the early injection, so the Router subscribes first: the order the route view must not depend on.</summary>
public sealed class LateStateRouterHarness : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) => RouterHarnessMarkup.Build(builder);
}

internal static class RouterHarnessMarkup
{
    public static void Build(RenderTreeBuilder builder)
    {
        builder.OpenComponent<Router>(0);
        builder.AddComponentParameter(1, nameof(Router.AppAssembly), typeof(RouterHarness).Assembly);
        builder.AddComponentParameter(2, nameof(Router.Found), (RenderFragment<RouteData>)(routeData => found =>
        {
            found.OpenComponent<DetailsOverlayRouteView>(0);
            found.AddComponentParameter(1, nameof(DetailsOverlayRouteView.RouteData), routeData);
            found.AddComponentParameter(2, nameof(DetailsOverlayRouteView.DefaultLayout), typeof(RouterPlainLayout));
            found.CloseComponent();
        }));
        builder.AddComponentParameter(3, nameof(Router.NotFoundPage), typeof(RouterNotFoundPage));
        builder.CloseComponent();
    }
}

public sealed class RouterPlainLayout : LayoutComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddContent(1, Body);
        builder.CloseElement();
    }
}

[Route("/router-test/not-found")]
public sealed class RouterNotFoundPage : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, "not found");
}

/// <summary>A routed page that records its instances and renders, so "kept alive" is observable.</summary>
[Route("/router-test/listen")]
public sealed class RouterListenPage : ComponentBase
{
    public static List<RouterListenPage> Instances { get; } = [];

    public RouterListenPage() => Instances.Add(this);

    [CascadingParameter(Name = RoutedPageQuery.CascadingName)] public string? RoutedUri { get; set; }

    public int RenderCount { get; private set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        RenderCount++;
        builder.OpenElement(0, "section");
        builder.AddAttribute(1, "id", "router-listen");
        builder.AddContent(2, RoutedUri);
        builder.CloseElement();
    }
}

[Route("/details/{EntityType}/{Id:guid}")]
public sealed class RouterDetailPage : ComponentBase
{
    [Parameter] public string EntityType { get; set; } = string.Empty;

    [Parameter] public Guid Id { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "article");
        builder.AddAttribute(1, "id", "router-detail");
        builder.AddContent(2, $"title {Id}");
        builder.CloseElement();
    }
}
