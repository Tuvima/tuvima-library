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
/// The details overlay (spec D1): a detail address opens over the page the person came from, that page stays alive
/// under it, and close, Esc and the backdrop dismiss it entirely, unless the open page's guard refuses.
/// </summary>
public sealed class DetailsOverlayRouteViewTests : AsyncBunitContext
{
    private static readonly Guid TitleId = Guid.Parse("6b0b8b0a-8c8e-4a5e-9c79-0c2d6d4b4f11");
    private static readonly Guid OtherTitleId = Guid.Parse("0a1b2c3d-1111-2222-3333-444455556666");

    private readonly RecordingHistory _history = new();
    private readonly ManualClock _clock = new();
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly NavigationManager _nav;
    private readonly BunitJSModuleInterop _dialogModule;

    public DetailsOverlayRouteViewTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _dialogModule = JSInterop.SetupModule("./js/app-dialog.js");
        _dialogModule.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IDetailsOverlayHistory>(_history);
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddScoped<DetailsOverlayState>();
        Services.AddSingleton(_device);
        UnderlayPage.Instances.Clear();
        _nav = Services.GetRequiredService<NavigationManager>();
    }

    private static string DetailUri(Guid id, string query = "") => $"/details/book/{id}{query}";

    private static RouteData Underlay() => new(typeof(UnderlayPage), new Dictionary<string, object?>());

    private static RouteData Detail(Guid id) => new(typeof(DetailStubPage), new Dictionary<string, object?> { ["Id"] = id });

    private IRenderedComponent<DetailsOverlayRouteView> RenderAt(string uri, RouteData data, RouteData? home = null)
    {
        _nav.NavigateTo(uri);
        return Render<DetailsOverlayRouteView>(parameters => parameters
            .Add(view => view.RouteData, data)
            .Add(view => view.DefaultLayout, typeof(PlainLayout))
            .Add(view => view.HomeRouteData, home));
    }

    // The Router shows the page for the address with its own RouteData; the tests play its part.
    private static void RouterShows(IRenderedComponent<DetailsOverlayRouteView> view, RouteData data) =>
        view.Render(parameters => parameters.Add(component => component.RouteData, data));

    // The test navigation manager also records a navigation the overlay cancelled to issue it again with its state.
    private IEnumerable<Bunit.TestDoubles.NavigationHistory> History => ((Bunit.TestDoubles.BunitNavigationManager)_nav).History;

    private int SucceededEntries => History.Count(item => item.State == Bunit.TestDoubles.NavigationState.Succeeded);

    private DetailsOverlayState State => Services.GetRequiredService<DetailsOverlayState>();

    // -- The page under the overlay is kept ------------------------------------------------------

    [Fact]
    public void OpeningATitle_ShowsItOverThePageItCameFromAndKeepsThatPageAlive()
    {
        var view = RenderAt("/listen?x=1", Underlay());
        var underlay = Assert.Single(UnderlayPage.Instances);
        Assert.Empty(view.FindAll("[data-details-overlay]"));

        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));

        Assert.Single(view.FindAll("dialog[data-details-overlay]"));
        Assert.Contains(TitleId.ToString(), view.Find("#detail-stub").TextContent);
        Assert.Same(underlay, Assert.Single(UnderlayPage.Instances));
        Assert.Equal(1, underlay.RenderCount);
    }

    [Fact]
    public void ThePageUnderTheOverlay_KeepsTheQueryItWasRoutedWith_WhileTheAddressBarNamesTheTitle()
    {
        var view = RenderAt("/listen?x=1", Underlay());

        _nav.NavigateTo(DetailUri(TitleId, "?tab=cast"));
        RouterShows(view, Detail(TitleId));

        Assert.Equal("/listen?x=1", Assert.Single(UnderlayPage.Instances).RoutedUri);
        Assert.Equal(DetailUri(TitleId, "?tab=cast"), view.Find("#detail-stub").GetAttribute("data-routed-uri"));
    }

    [Fact]
    public void ThePageInstance_SurvivesOpeningSwappingAndDismissingTheOverlay()
    {
        var view = RenderAt("/listen?x=1", Underlay());
        var underlay = Assert.Single(UnderlayPage.Instances);

        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));
        _nav.NavigateTo(DetailUri(OtherTitleId));
        RouterShows(view, Detail(OtherTitleId));
        Assert.Contains(OtherTitleId.ToString(), view.Find("#detail-stub").TextContent);

        // What the browser does after the overlay asks to step back: it lands on the page under it.
        _nav.NavigateTo("/listen?x=1");
        RouterShows(view, Underlay());

        Assert.Empty(view.FindAll("[data-details-overlay]"));
        Assert.Same(underlay, Assert.Single(UnderlayPage.Instances));
        Assert.Equal(1, underlay.RenderCount);
    }

    [Fact]
    public void ANavigationFromInsideTheOverlayToAnotherTitle_AddsAnEntryCarryingTheSameUnderlay()
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));
        var entriesBefore = SucceededEntries;

        _nav.NavigateTo(DetailUri(OtherTitleId));
        RouterShows(view, Detail(OtherTitleId));

        Assert.Equal(entriesBefore + 1, SucceededEntries);
        var entry = History.First(item => item.State == Bunit.TestDoubles.NavigationState.Succeeded);
        Assert.True(DetailsOverlayRoute.TryDecodeState(entry.Options.HistoryEntryState, out var underlay, out var depth));
        Assert.Equal("/listen", underlay);
        Assert.Equal(2, depth);
    }

    [Fact]
    public void ReloadingATitle_OpensItOverHome()
    {
        var home = new RouteData(typeof(HomePage), new Dictionary<string, object?>());

        var view = RenderAt(DetailUri(TitleId), Detail(TitleId), home);

        Assert.Single(view.FindAll("dialog[data-details-overlay]"));
        Assert.Equal("Home", view.Find("#home-page").TextContent);
        Assert.Equal(DetailsOverlayRoute.Home, State.UnderlayUri);
    }

    // -- Dismissing --------------------------------------------------------------------------------

    [Fact]
    public void TheCloseButton_IsNamedCloseAndStepsBackOverEveryTitleOpenedInTheOverlay()
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));
        _nav.NavigateTo(DetailUri(OtherTitleId));
        RouterShows(view, Detail(OtherTitleId));

        var close = view.Find("button.tl-details-overlay__close");
        Assert.Equal("Close", close.GetAttribute("aria-label"));
        close.Click();

        Assert.Equal([2], _history.Steps);
    }

    [Fact]
    public async Task Escape_AndTheBackdrop_ReachTheOverlayAsOneCancelRequestThatDismissesIt()
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));

        // app-dialog.js reports Esc, a click on the backdrop and a swipe down on the sheet's grip through RequestCancel.
        await view.InvokeAsync(() => view.FindComponent<DetailsOverlayHost>().Instance.RequestCancel());

        Assert.Equal([1], _history.Steps);
    }

    [Fact]
    public async Task Escape_IsOfferedToTheOpenPageFirst_AndWhenItUsesThePressTheOverlayStaysOpen()
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));
        var offered = 0;
        var registration = State.RegisterEscapeHandler(() =>
        {
            offered++;
            return ValueTask.FromResult(true);
        });

        await view.InvokeAsync(() => view.FindComponent<DetailsOverlayHost>().Instance.RequestCancel());

        Assert.Equal(1, offered);
        Assert.Empty(_history.Steps);
        Assert.Single(view.FindAll("dialog[data-details-overlay]"));

        // Once the page stops handling it (Editing Mode ended), Esc closes the overlay as before.
        registration.Dispose();
        await view.InvokeAsync(() => view.FindComponent<DetailsOverlayHost>().Instance.RequestCancel());

        Assert.Equal(1, offered);
        Assert.Equal([1], _history.Steps);
    }

    [Fact]
    public async Task Escape_GoesOnToDismissTheOverlayWhenTheOpenPageDoesNotUseIt()
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));
        var offered = 0;
        using var registration = State.RegisterEscapeHandler(() =>
        {
            offered++;
            return ValueTask.FromResult(false);
        });

        await view.InvokeAsync(() => view.FindComponent<DetailsOverlayHost>().Instance.RequestCancel());

        Assert.Equal(1, offered);
        Assert.Equal([1], _history.Steps);
    }

    [Fact]
    public void AfterAReload_DismissingGoesToThePageUnderTheOverlayWithoutAddingAStep()
    {
        var home = new RouteData(typeof(HomePage), new Dictionary<string, object?>());
        var view = RenderAt(DetailUri(TitleId), Detail(TitleId), home);
        var entriesBefore = SucceededEntries;

        view.Find("button.tl-details-overlay__close").Click();

        Assert.Empty(_history.Steps);
        Assert.EndsWith("/", _nav.Uri);
        Assert.Equal(entriesBefore, SucceededEntries);
    }

    [Fact]
    public async Task ARefusingGuard_KeepsTheOverlayOpen_ForCloseEscapeAndTheBackdrop()
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));
        var guard = new RecordingGuard(allow: false);
        using var registration = State.RegisterDismissGuard(guard);

        view.Find("button.tl-details-overlay__close").Click();
        await view.InvokeAsync(() => view.FindComponent<DetailsOverlayHost>().Instance.RequestCancel());

        Assert.Equal(2, guard.Asked);
        Assert.Empty(_history.Steps);
        Assert.Single(view.FindAll("dialog[data-details-overlay]"));
    }

    [Fact]
    public async Task AGuardThatAllows_LetsTheOverlayCloseAndOnceRemovedIsNotAskedAgain()
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));
        var guard = new RecordingGuard(allow: true);
        var registration = State.RegisterDismissGuard(guard);

        view.Find("button.tl-details-overlay__close").Click();
        Assert.Equal([1], _history.Steps);
        Assert.Equal(1, guard.Asked);

        registration.Dispose();
        await State.DismissAsync();
        Assert.Equal(1, guard.Asked);
    }

    [Fact]
    public void ABrowserBackOutOfTheOverlay_IsRefusedWhileAGuardRefuses()
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));
        using var registration = State.RegisterDismissGuard(new RecordingGuard(allow: false));

        _nav.NavigateTo("/listen");

        Assert.EndsWith(DetailUri(TitleId), _nav.Uri);
        Assert.True(State.IsOpen);
    }

    [Fact]
    public void ReplacingTheEntryForATab_KeepsTheUnderlayAndAsksNoGuard()
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));
        var guard = new RecordingGuard(allow: false);
        using var registration = State.RegisterDismissGuard(guard);

        State.ReplaceCurrentEntry(DetailUri(TitleId, "?tab=cast"));

        Assert.Equal(0, guard.Asked);
        Assert.Equal("/listen", State.Current.UnderlayUri);
        Assert.Equal(1, State.Current.Depth);
        var entry = History.First(item => item.State == Bunit.TestDoubles.NavigationState.Succeeded);
        Assert.True(entry.Options.ReplaceHistoryEntry);
        Assert.True(DetailsOverlayRoute.TryDecodeState(entry.Options.HistoryEntryState, out var underlay, out _));
        Assert.Equal("/listen", underlay);
    }

    [Fact]
    public async Task AFailedHistoryStep_FallsBackToTheUnderlayAndDoesNotLeaveTheGuardBypassed()
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));
        _history.Succeeds = false;

        Assert.True(await State.DismissAsync());

        Assert.Equal([1], _history.Steps);
        Assert.EndsWith("/listen", _nav.Uri);
        var fallback = History.First(item => item.State == Bunit.TestDoubles.NavigationState.Succeeded);
        Assert.True(fallback.Options.ReplaceHistoryEntry);
        RouterShows(view, Underlay());
        Assert.False(State.IsOpen);

        // The "already answered" mark covered that one navigation only: a later one is asked again.
        _nav.NavigateTo(DetailUri(OtherTitleId));
        RouterShows(view, Detail(OtherTitleId));
        var guard = new RecordingGuard(allow: false);
        using var registration = State.RegisterDismissGuard(guard);
        _nav.NavigateTo("/watch");
        Assert.Equal(1, guard.Asked);
        Assert.True(State.IsOpen);
    }

    [Fact]
    public void TheRememberedPages_AreCappedAndTheLeastRecentlyShownAreForgotten()
    {
        var page = Underlay();

        for (var index = 0; index < DetailsOverlayState.MaxRememberedPages + 10; index++)
        {
            State.RememberPage($"/page-{index}", page);
        }

        Assert.False(State.TryGetPage("/page-0", out _));
        Assert.False(State.TryGetPage("/page-9", out _));
        Assert.True(State.TryGetPage("/page-10", out _));
        Assert.True(State.TryGetPage($"/page-{DetailsOverlayState.MaxRememberedPages + 9}", out _));

        // Showing a page again keeps it.
        State.RememberPage("/page-10", page);
        State.RememberPage("/another", page);
        Assert.True(State.TryGetPage("/page-10", out _));
        Assert.False(State.TryGetPage("/page-11", out _));
    }

    [Fact]
    public void AReplaceToADetailAddressForAnotherPage_OpensOverHomeSoTheReplacedPageDoesNotStayUnderIt()
    {
        var home = new RouteData(typeof(HomePage), new Dictionary<string, object?>());
        var view = RenderAt("/read/abc", Underlay(), home);

        DetailsOverlayNavigation.ReplacePage(_nav, DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));

        Assert.Equal(DetailsOverlayRoute.Home, State.UnderlayUri);
        Assert.Equal(0, State.Current.Depth);
        var entry = History.First(item => item.State == Bunit.TestDoubles.NavigationState.Succeeded);
        Assert.True(entry.Options.ReplaceHistoryEntry);
    }

    [Fact]
    public void AnyPageIsKeptUnderTheOverlay_WhateverLayoutItUses()
    {
        var reader = new RouteData(typeof(ReaderLayoutPage), new Dictionary<string, object?>());
        var view = RenderAt("/read/abc", reader);

        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));

        Assert.Equal("/read/abc", State.UnderlayUri);
        Assert.Single(view.FindAll("#reader-page"));
        Assert.Single(view.FindAll("dialog[data-details-overlay]"));
        Assert.Single(view.FindAll("#reader-layout"));
    }

    // -- Closing twice, and leaving a reader ----------------------------------------------------------

    [Fact]
    public void ASecondCloseWhileTheFirstIsStillStepping_DoesNotStepBackPastThePageUnderTheOverlay()
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));
        _nav.NavigateTo(DetailUri(OtherTitleId));
        RouterShows(view, Detail(OtherTitleId));

        view.Find("button.tl-details-overlay__close").Click();
        view.Find("button.tl-details-overlay__close").Click();
        view.Find("button.tl-details-overlay__close").Click();

        Assert.Equal([2], _history.Steps);
    }

    [Fact]
    public async Task TheCloseInFlight_EndsWhenTheAddressChanges_SoTheNextOverlayClosesNormally()
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));
        await State.DismissAsync();
        await State.DismissAsync();
        Assert.Equal([1], _history.Steps);

        _nav.NavigateTo("/listen");
        RouterShows(view, Underlay());
        _nav.NavigateTo(DetailUri(OtherTitleId));
        RouterShows(view, Detail(OtherTitleId));
        await State.DismissAsync();

        Assert.Equal([1, 1], _history.Steps);
    }

    [Fact]
    public async Task ACloseThatNeverChangedTheAddress_DoesNotMakeTheOverlayUnclosable()
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));
        await State.DismissAsync();

        // The browser had nowhere to step back to, so no address change ever came.
        _clock.Advance(DetailsOverlayState.DismissTimeout + TimeSpan.FromMilliseconds(1));
        await State.DismissAsync();

        Assert.Equal([1, 1], _history.Steps);
    }

    [Fact]
    public async Task AReaderOpenedStraightFromTheTitle_ReturnsBySteppingBackSoTheOriginalUnderlayIsKept()
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));
        _nav.NavigateTo("/read/abc");
        RouterShows(view, Underlay());
        var entriesBefore = SucceededEntries;

        await DetailsOverlayNavigation.ExitToTitleAsync(_nav, State, DetailUri(TitleId));

        Assert.Equal([1], _history.Steps);
        Assert.Equal(entriesBefore, SucceededEntries);
    }

    [Fact]
    public async Task AReaderFromADeepLink_OpensTheTitleOverHome()
    {
        var home = new RouteData(typeof(HomePage), new Dictionary<string, object?>());
        var view = RenderAt("/read/abc", Underlay(), home);

        await DetailsOverlayNavigation.ExitToTitleAsync(_nav, State, DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));

        Assert.Empty(_history.Steps);
        Assert.True(State.IsOpen);
        Assert.Equal(DetailsOverlayRoute.Home, State.UnderlayUri);
        var entry = History.First(item => item.State == Bunit.TestDoubles.NavigationState.Succeeded);
        Assert.True(entry.Options.ReplaceHistoryEntry);
    }

    [Theory]
    [InlineData("anotherPageFirst")]
    [InlineData("anotherTitle")]
    public async Task AReaderThatDidNotComeStraightFromThatTitle_OpensTheTitleOverHomeInstead(string case_)
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));
        _nav.NavigateTo("/read/abc");
        RouterShows(view, Underlay());
        if (case_ == "anotherPageFirst")
        {
            _nav.NavigateTo("/read/abc?reader=new");
            RouterShows(view, Underlay());
        }

        var target = case_ == "anotherTitle" ? DetailUri(OtherTitleId) : DetailUri(TitleId);
        await DetailsOverlayNavigation.ExitToTitleAsync(_nav, State, target);

        Assert.Empty(_history.Steps);
        var entry = History.First(item => item.State == Bunit.TestDoubles.NavigationState.Succeeded);
        Assert.True(entry.Options.ReplaceHistoryEntry);
    }

    [Fact]
    public void WhatTheTitleNeedsFromAReaderExitIsHandedOverInMemoryOnce()
    {
        var asset = Guid.NewGuid();
        State.OfferLastAsset(TitleId, asset);

        Assert.True(State.TryTakeLastAsset(TitleId, out var taken));
        Assert.Equal(asset, taken);
        Assert.False(State.TryTakeLastAsset(TitleId, out _));
    }

    // -- Presentation ------------------------------------------------------------------------------

    [Fact]
    public void TheOverlay_IsAModalDialogLabelledByTheTitle()
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));
        State.SetTitle("Dune");

        var dialog = view.Find("dialog[data-details-overlay]");
        Assert.Equal("dialog", dialog.GetAttribute("role"));
        Assert.Equal("true", dialog.GetAttribute("aria-modal"));
        var heading = view.WaitForElement("dialog h2");
        Assert.Equal(heading.Id, dialog.GetAttribute("aria-labelledby"));
        Assert.Equal("Dune", heading.TextContent);
        Assert.Equal(heading.Id, dialog.Children.First(child => child.TagName == "H2").Id);
    }

    [Fact]
    public void OnADesktop_TheOverlayIsAWindow()
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));

        var dialog = view.Find("dialog[data-details-overlay]");
        Assert.DoesNotContain("tl-details-overlay--sheet", dialog.ClassName);
        Assert.Empty(view.FindAll("[data-app-dialog-grip]"));
    }

    [Fact]
    public async Task OnAPhone_TheOverlayIsABottomSheetWithAGrip_AndTellsTheDialogModuleSo()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));

        var dialog = view.Find("dialog[data-details-overlay]");
        Assert.Contains("tl-details-overlay--sheet", dialog.ClassName);
        Assert.Single(view.FindAll("[data-app-dialog-grip]"));
        view.WaitForAssertion(() => Assert.True((bool)_dialogModule.Invocations.Last(call => call.Identifier == "update").Arguments.Last()!));
    }

    [Fact]
    public void TheDetailPageScrollsInsideTheOverlaysOwnScroller_WhichTheOriginTrackingNamesSoOpeningATitleResetsOnlyIt()
    {
        var view = RenderAt("/listen", Underlay());
        _nav.NavigateTo(DetailUri(TitleId));
        RouterShows(view, Detail(TitleId));

        var scroller = view.Find(".tl-details-overlay__scroller");
        Assert.Equal("details-overlay", scroller.GetAttribute("data-detail-origin-scroll"));
        Assert.NotNull(scroller.QuerySelector("#detail-stub"));
    }

    private sealed class RecordingHistory : IDetailsOverlayHistory
    {
        public List<int> Steps { get; } = [];

        // False simulates a browser that could not be asked to step back.
        public bool Succeeds { get; set; } = true;

        public ValueTask<bool> GoBackAsync(int steps)
        {
            Steps.Add(steps);
            return ValueTask.FromResult(Succeeds);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class RecordingGuard(bool allow) : IDetailsOverlayDismissGuard
    {
        public int Asked { get; private set; }

        public ValueTask<bool> CanDismissAsync()
        {
            Asked++;
            return ValueTask.FromResult(allow);
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

    // A page that records every instance and render, so "kept alive" is observable.
    private sealed class UnderlayPage : ComponentBase
    {
        public static List<UnderlayPage> Instances { get; } = [];

        public UnderlayPage() => Instances.Add(this);

        [CascadingParameter(Name = RoutedPageQuery.CascadingName)] public string? RoutedUri { get; set; }

        public int RenderCount { get; private set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            RenderCount++;
            builder.OpenElement(0, "section");
            builder.AddAttribute(1, "id", "underlay");
            builder.AddContent(2, RoutedUri);
            builder.CloseElement();
        }
    }

    private sealed class ReaderLayout : LayoutComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "id", "reader-layout");
            builder.AddContent(2, Body);
            builder.CloseElement();
        }
    }

    [Layout(typeof(ReaderLayout))]
    private sealed class ReaderLayoutPage : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "section");
            builder.AddAttribute(1, "id", "reader-page");
            builder.CloseElement();
        }
    }

    private sealed class HomePage : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "section");
            builder.AddAttribute(1, "id", "home-page");
            builder.AddContent(2, "Home");
            builder.CloseElement();
        }
    }

    private sealed class DetailStubPage : ComponentBase
    {
        [Parameter] public Guid Id { get; set; }

        [CascadingParameter(Name = RoutedPageQuery.CascadingName)] public string? RoutedUri { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "article");
            builder.AddAttribute(1, "id", "detail-stub");
            builder.AddAttribute(2, "data-routed-uri", RoutedUri);
            builder.AddContent(3, Id.ToString());
            builder.CloseElement();
        }
    }
}
