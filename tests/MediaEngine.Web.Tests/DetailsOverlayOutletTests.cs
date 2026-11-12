using Bunit;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The details window is drawn by the layout's outlet, outside the box that holds the page (spec music-player-and-listen-fixes D5): that box is a size
/// container with hidden overflow, and whether a browser makes it the containing block of the fixed window inside it differs by engine and version.
/// The window's state, focus return and dismissal are unchanged.
/// </summary>
public sealed class DetailsOverlayOutletTests : AsyncBunitContext
{
    private static readonly Guid TitleId = Guid.Parse("6b0b8b0a-8c8e-4a5e-9c79-0c2d6d4b4f11");

    private readonly NavigationManager _nav;

    public DetailsOverlayOutletTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/app-dialog.js").Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDetailsOverlayHistory>(new Steps());
        Services.AddScoped<DetailsOverlayState>();
        Services.AddScoped<DetailsOverlayOutletState>();
        Services.AddSingleton(new DeviceContextService(EngineApiClientStub.CreateDefault()));
        _nav = Services.GetRequiredService<NavigationManager>();
    }

    private IRenderedComponent<DetailsOverlayRouteView> RenderAt(string uri, Type layout)
    {
        _nav.NavigateTo(uri);
        return Render<DetailsOverlayRouteView>(parameters => parameters
            .Add(view => view.RouteData, new RouteData(typeof(UnderlayPage), new Dictionary<string, object?>()))
            .Add(view => view.DefaultLayout, layout));
    }

    private void OpenTitle(IRenderedComponent<DetailsOverlayRouteView> view)
    {
        _nav.NavigateTo($"/details/book/{TitleId}");
        view.Render(parameters => parameters.Add(component => component.RouteData,
            new RouteData(typeof(DetailStubPage), new Dictionary<string, object?> { ["Id"] = TitleId })));
    }

    [Fact]
    public void WhenTheLayoutHasAnOutlet_TheWindowIsDrawnThereAndNotInsideThePageBox()
    {
        var view = RenderAt("/listen", typeof(OutletLayout));

        OpenTitle(view);

        var dialog = view.Find("dialog[data-details-overlay]");
        Assert.Empty(view.FindAll(".page-box dialog"));
        Assert.Empty(view.FindAll(".page-box [data-app-dialog-backdrop]"));
        Assert.NotNull(dialog.Closest(".frame"));
        Assert.Contains(TitleId.ToString(), dialog.QuerySelector("#detail-stub")!.TextContent);
        Assert.Contains("listen-page", view.Find(".page-box").InnerHtml, StringComparison.Ordinal);
        Assert.True(dialog.PreviousElementSibling!.HasAttribute("data-app-dialog-backdrop"));
    }

    [Fact]
    public void TheWindowInTheOutlet_KeepsThePageCascadeAndTheOverlayStateOfTheOneBesideThePage()
    {
        var view = RenderAt("/listen", typeof(OutletLayout));

        OpenTitle(view);

        Assert.Equal($"/details/book/{TitleId}", view.Find("#detail-stub").GetAttribute("data-routed-uri"));
        Assert.Equal("/listen", view.Find("#underlay").TextContent);
        Assert.Equal("true", view.Find("#detail-stub").GetAttribute("data-has-overlay-state"));
    }

    [Fact]
    public void ClosingTheWindow_TakesItOutOfTheOutlet_AndTheOutletDrawsNothingAgain()
    {
        var view = RenderAt("/listen", typeof(OutletLayout));
        OpenTitle(view);
        Assert.Single(view.FindAll("dialog[data-details-overlay]"));

        _nav.NavigateTo("/listen");
        view.Render(parameters => parameters.Add(component => component.RouteData,
            new RouteData(typeof(UnderlayPage), new Dictionary<string, object?>())));

        Assert.Empty(view.FindAll("[data-details-overlay]"));
        Assert.Empty(view.FindAll("[data-app-dialog-backdrop]"));
        Assert.Null(Services.GetRequiredService<DetailsOverlayOutletState>().Content);
    }

    [Fact]
    public async Task EscapeAndTheBackdropStillAskTheOverlayStateToDismiss_FromTheWindowInTheOutlet()
    {
        var view = RenderAt("/listen", typeof(OutletLayout));
        OpenTitle(view);
        var guard = new RefusingGuard();
        using var registration = Services.GetRequiredService<DetailsOverlayState>().RegisterDismissGuard(guard);

        await view.InvokeAsync(() => view.FindComponent<DetailsOverlayHost>().Instance.RequestCancel());

        Assert.Equal(1, guard.Asked);
        Assert.Single(view.FindAll("dialog[data-details-overlay]"));
    }

    [Fact]
    public void ALayoutWithoutAnOutlet_StillDrawsTheWindowBesideThePage()
    {
        var view = RenderAt("/listen", typeof(PlainLayout));

        OpenTitle(view);

        Assert.Single(view.FindAll(".plain-layout dialog[data-details-overlay]"));
        Assert.Null(Services.GetRequiredService<DetailsOverlayOutletState>().Content);
    }

    [Fact]
    public void TheShellDrawsTheOutletOutsideThePageBox_ButInsideTheAppFrame_SoThePhoneDockRuleStillSeesTheWindow()
    {
        var layout = File.ReadAllText(Path.Combine(FindWebRoot(), "Shared", "MainLayout.razor")).Replace("\r\n", "\n");

        Assert.Contains("@implements IDetailsOverlayOutletLayout", layout, StringComparison.Ordinal);
        var frame = layout.IndexOf("<div class=\"playback-app-frame\">", StringComparison.Ordinal);
        var content = layout.IndexOf("<div class=\"playback-app-frame__content ", StringComparison.Ordinal);
        var layoutEnd = layout.IndexOf("</AppLayout>", StringComparison.Ordinal);
        var outlet = layout.IndexOf("<DetailsOverlayOutlet />", StringComparison.Ordinal);
        var dock = layout.IndexOf("<div class=\"playback-app-frame__audio-dock\"", StringComparison.Ordinal);
        var frameEnd = layout.IndexOf("</AppCssElement>\n</div>\n</AppCssElement>", StringComparison.Ordinal);

        Assert.True(frame >= 0 && content > frame && layoutEnd > content, "the page box sits inside the app frame");
        Assert.True(outlet > layoutEnd, "the outlet comes after the page box has closed, never inside it");
        Assert.True(outlet < dock && dock < frameEnd, "the outlet sits beside the player bar dock, still inside the app frame");
        Assert.DoesNotContain("<DetailsOverlayHost", layout, StringComparison.Ordinal);

        // The phone navigation drops under the backdrop by a rule on the frame that holds both it and the window.
        var css = File.ReadAllText(Path.Combine(FindWebRoot(), "Shared", "MainLayout.razor.css")).Replace("\r\n", "\n");
        Assert.Contains(".playback-app-frame:has(dialog[data-details-overlay][open]) .layout-shell__intent-dock {", css, StringComparison.Ordinal);
    }

    [Fact]
    public void NoStylesheetOrCommentNamesAPortalThatDoesNotExist()
    {
        var css = File.ReadAllText(Path.Combine(FindWebRoot(), "wwwroot", "app.css"));

        Assert.DoesNotContain("DetailsOverlayPortal", css, StringComparison.Ordinal);
        Assert.Contains("DetailsOverlayOutlet", css, StringComparison.Ordinal);
    }

    private static string FindWebRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), "src", "MediaEngine.Web");
    }

    private sealed class Steps : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int steps) => ValueTask.FromResult(true);
    }

    private sealed class RefusingGuard : IDetailsOverlayDismissGuard
    {
        public int Asked { get; private set; }

        public ValueTask<bool> CanDismissAsync()
        {
            Asked++;
            return ValueTask.FromResult(false);
        }
    }

    // A layout that, like the shell, holds the page in its own box and draws the outlet beside it.
    private sealed class OutletLayout : LayoutComponentBase, IDetailsOverlayOutletLayout
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "frame");
            builder.OpenElement(2, "div");
            builder.AddAttribute(3, "class", "page-box");
            builder.AddContent(4, Body);
            builder.CloseElement();
            builder.OpenComponent<DetailsOverlayOutlet>(5);
            builder.CloseComponent();
            builder.CloseElement();
        }
    }

    private sealed class PlainLayout : LayoutComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "plain-layout");
            builder.AddContent(2, Body);
            builder.CloseElement();
        }
    }

    private sealed class UnderlayPage : ComponentBase
    {
        [CascadingParameter(Name = RoutedPageQuery.CascadingName)] public string? RoutedUri { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "section");
            builder.AddAttribute(1, "id", "underlay");
            builder.AddAttribute(2, "class", "listen-page");
            builder.AddContent(3, RoutedUri);
            builder.CloseElement();
        }
    }

    private sealed class DetailStubPage : ComponentBase
    {
        [Parameter] public Guid Id { get; set; }

        [CascadingParameter(Name = RoutedPageQuery.CascadingName)] public string? RoutedUri { get; set; }

        [CascadingParameter] public DetailsOverlayState? Overlay { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "article");
            builder.AddAttribute(1, "id", "detail-stub");
            builder.AddAttribute(2, "data-routed-uri", RoutedUri);
            builder.AddAttribute(3, "data-has-overlay-state", Overlay is not null ? "true" : "false");
            builder.AddContent(4, Id.ToString());
            builder.CloseElement();
        }
    }
}
