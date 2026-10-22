using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Editing Mode on a title shown in the details overlay: Done, Esc and Back settle unsaved input with the shared
/// "Save or discard?" dialog (details overlay spec D9), Esc closes a field before a section before the overlay, the
/// banner and the section sit where the spec puts them, and no drawer renders for any kind of title.
/// </summary>
public sealed class EditingDismissalTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<int> _steps = [];
    private readonly List<Dictionary<string, string>> _saves = [];
    private bool _saveResult = true;
    private readonly NavigationManager _nav;

    public EditingDismissalTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.SaveItemDisplayOverridesAsync), args =>
            {
                _saves.Add((Dictionary<string, string>)args![1]!);
                return Task.FromResult(_saveResult);
            });
            stub.SetHandler("get_LastError", _ => "HTTP 500: could not write");
            stub.SetHandler(nameof(IEngineApiClient.GetItemHistoryWithStatusAsync), _ =>
                Task.FromResult((new List<LibraryItemHistoryDto>(), (string?)null)));
        });
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(_device);
        Services.AddSingleton(new DashboardSessionAccessor());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<MediaEditorLauncherService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(_ => new PlaybackSessionController(null!, null!));
        Services.AddSingleton(_ => new ProviderCatalogueService(api, new MemoryCache(new MemoryCacheOptions())));
        Services.AddSingleton<IDetailsOverlayHistory>(new StepRecorder(_steps));
        Services.AddScoped<DetailsOverlayState>();
        _nav = Services.GetRequiredService<NavigationManager>();
        Render<AppPopoverHost>();
    }

    private DetailsOverlayState State => Services.GetRequiredService<DetailsOverlayState>();

    private Task UseDesktopAsync() =>
        _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

    private static DetailPageViewModel Model(DetailEntityType type = DetailEntityType.Movie) => new()
    {
        Id = MovieId.ToString("D"),
        EntityType = type,
        Title = "Dune: Part One",
        Tabs = [new DetailTab { Key = "overview", Label = "Overview" }],
        OverflowActions =
        [
            new DetailAction { Key = "edit", Label = "Edit", Icon = "edit" },
            new DetailAction { Key = "status-complete", Label = "Mark as watched", Icon = "check_circle" },
            new DetailAction { Key = "status-reset", Label = "Mark as unwatched", Icon = "restart_alt" },
            new DetailAction { Key = "status-history", Label = "Viewing history", Icon = "history" },
        ],
    };

    private IRenderedComponent<DetailPage> RenderInOpenOverlay(DetailEntityType type = DetailEntityType.Movie)
    {
        _nav.NavigateTo("/watch");
        State.RememberPage("/watch", new RouteData(typeof(BlankPage), new Dictionary<string, object?>()));
        State.Attach();
        _nav.NavigateTo($"/details/movie/{MovieId}");
        Assert.True(State.IsOpen);
        return Render<DetailPage>(parameters => parameters
            .Add(page => page.Model, Model(type))
            .AddCascadingValue(State));
    }

    private static void ChooseEdit(IRenderedComponent<DetailPage> cut)
    {
        cut.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Edit").Click();
        cut.WaitForAssertion(() => Assert.Equal("Done", cut.Find("button.tl-editing-banner__done").TextContent.Trim()));
    }

    private async Task<IRenderedComponent<DetailPage>> StartEditingAsync(DetailEntityType type = DetailEntityType.Movie)
    {
        await UseDesktopAsync();
        var cut = RenderInOpenOverlay(type);
        ChooseEdit(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Edit title']")));
        return cut;
    }

    private static void TypeIntoTheTitle(IRenderedComponent<DetailPage> cut, string value)
    {
        cut.Find("button[aria-label='Edit title']").Click();
        cut.WaitForElement("input[aria-label='Edit Title']").Input(value);
    }

    // Esc as the overlay delivers it: app-dialog.js reports it through RequestCancel, which offers it to the page first.
    private async Task<bool> EscAsync(IRenderedComponent<DetailPage> cut)
    {
        var used = false;
        await cut.InvokeAsync(async () => used = await State.HandleEscapeAsync());
        return used;
    }

    // Close, Esc with nothing open, the backdrop and Back all end up here: the guards are asked, then the overlay steps back.
    private async Task<bool> DismissAsync(IRenderedComponent<DetailPage> cut)
    {
        var dismissed = false;
        await cut.InvokeAsync(async () => dismissed = await State.DismissAsync());
        return dismissed;
    }

    private async Task<bool> CanDismissAsync(IRenderedComponent<DetailPage> cut)
    {
        var allowed = false;
        await cut.InvokeAsync(async () => allowed = await State.CanDismissAsync());
        return allowed;
    }

    private static void Answer(IRenderedComponent<AppDialogHost> host, string label)
    {
        host.WaitForElement(".app-confirm");
        Assert.Equal("Save or discard?", host.Find(".app-dialog-shell__title").TextContent.Trim());
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == label).Click();
    }

    // -- Done and "Save or discard?" -----------------------------------------------------------

    [Fact]
    public async Task DoneWithUnsavedFieldInputAsksSaveOrDiscard_AndSaveSavesThenLeavesEditMode()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Dune");

        cut.Find("button.tl-editing-banner__done").Click();
        Answer(host, "Save");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-editing-banner")));
        Assert.Equal("Dune", Assert.Single(_saves)["title"]);
    }

    [Fact]
    public async Task DoneWithUnsavedFieldInputAsksSaveOrDiscard_AndDiscardDropsItAndLeavesEditMode()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Dune");

        cut.Find("button.tl-editing-banner__done").Click();
        Answer(host, "Discard");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-editing-banner")));
        Assert.Empty(_saves);
        Assert.Empty(cut.FindAll("input[aria-label='Edit Title']"));
    }

    [Fact]
    public async Task DoneWithUnsavedFieldInputAndTheDialogClosed_StaysInEditModeWithTheInputIntact()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Dune");

        cut.Find("button.tl-editing-banner__done").Click();
        host.WaitForElement(".app-confirm");
        host.Find("button[aria-label='Close dialog']").Click();

        cut.WaitForAssertion(() => Assert.Empty(host.FindAll(".app-confirm")));
        Assert.Single(cut.FindAll(".tl-editing-banner"));
        Assert.Equal("Dune", cut.Find("input[aria-label='Edit Title']").GetAttribute("value"));
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task AFailedSaveFromTheDialogStaysInEditModeWithTheFieldOpenAndItsErrorShown()
    {
        _saveResult = false;
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Typed and kept");

        cut.Find("button.tl-editing-banner__done").Click();
        Answer(host, "Save");

        cut.WaitForAssertion(() => Assert.Contains("could not write", cut.Find(".tl-editing-field__error").TextContent));
        Assert.Single(cut.FindAll(".tl-editing-banner"));
        Assert.Equal("Typed and kept", cut.Find("input[aria-label='Edit Title']").GetAttribute("value"));
    }

    [Fact]
    public async Task AFailedFieldSaveOnItsOwnShowsTheErrorInlineAndKeepsTheFieldOpen()
    {
        _saveResult = false;
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Typed and kept");

        cut.Find("input[aria-label='Edit Title']").KeyDown("Enter");

        cut.WaitForAssertion(() => Assert.Equal("alert", cut.Find(".tl-editing-field__error").GetAttribute("role")));
        Assert.Contains("could not write", cut.Find(".tl-editing-field__error").TextContent);
        Assert.Equal("Typed and kept", cut.Find("input[aria-label='Edit Title']").GetAttribute("value"));
    }

    [Fact]
    public async Task DoneWithNothingUnsavedLeavesAtOnceWithoutAnyDialog()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();

        cut.Find("button.tl-editing-banner__done").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-editing-banner")));
        Assert.Empty(host.FindAll(".app-confirm"));
    }

    // -- Esc, in order -------------------------------------------------------------------------

    [Fact]
    public async Task EscClosesAnOpenFieldFirst_ThenASection_AndOnlyThenDismissesTheOverlayLeavingEditMode()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        cut.FindAll(".tl-editing-banner__section").Single(button => button.TextContent.Contains("History")).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-slot")));
        cut.Find("button[aria-label='Close History']");
        cut.Find("button[aria-label='Edit title']").Click();

        // 1. The open field (nothing typed into it) cancels; the section and the mode stay.
        Assert.True(await EscAsync(cut));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("input[aria-label='Edit Title']")));
        Assert.NotEmpty(cut.FindAll(".tl-editing-slot"));
        Assert.Single(cut.FindAll(".tl-editing-banner"));

        // 2. The section closes; the mode stays.
        Assert.True(await EscAsync(cut));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-editing-slot")));
        Assert.Single(cut.FindAll(".tl-editing-banner"));

        // 3. Nothing is open: the press is not used, so the overlay dismisses and leaves edit mode first.
        Assert.False(await EscAsync(cut));
        Assert.Empty(_steps);
        Assert.True(await DismissAsync(cut));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-editing-banner")));
        Assert.Equal([1], _steps);
        Assert.Empty(host.FindAll(".app-confirm"));
    }

    [Fact]
    public async Task EscPressedInsideTheFocusedFieldCancelsThatFieldWithoutAsking()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Dune");

        // The field's own handler (and the overlay script, which leaves a field that owns Esc alone) keeps this as it always was.
        cut.Find("input[aria-label='Edit Title']").KeyDown("Escape");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("input[aria-label='Edit Title']")));
        Assert.Empty(host.FindAll(".app-confirm"));
        Assert.Empty(_saves);
        Assert.Single(cut.FindAll(".tl-editing-banner"));
    }

    [Fact]
    public async Task OverlayLevelEscWithTypedInputAsksSaveOrDiscardAndCancelKeepsTheInput()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Dune");

        var esc = EscAsync(cut);
        host.WaitForElement(".app-confirm");
        host.Find("button[aria-label='Close dialog']").Click();

        Assert.True(await esc);
        Assert.Equal("Dune", cut.Find("input[aria-label='Edit Title']").GetAttribute("value"));
        Assert.Empty(_saves);
        Assert.Empty(_steps);
    }

    [Fact]
    public async Task OverlayLevelEscWithTypedInputAndDiscardClosesTheFieldAndKeepsEditingAndTheOverlay()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Dune");

        var esc = EscAsync(cut);
        Answer(host, "Discard");

        Assert.True(await esc);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("input[aria-label='Edit Title']")));
        Assert.Single(cut.FindAll(".tl-editing-banner"));
        Assert.Empty(_saves);
        Assert.Empty(_steps);
    }

    [Fact]
    public async Task OverlayLevelEscWithTypedInputAndSaveSavesTheFieldAndKeepsEditing()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Dune");

        var esc = EscAsync(cut);
        Answer(host, "Save");

        Assert.True(await esc);
        Assert.Equal("Dune", Assert.Single(_saves)["title"]);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("input[aria-label='Edit Title']")));
        Assert.Single(cut.FindAll(".tl-editing-banner"));
    }

    [Fact]
    public async Task OverlayLevelEscWithAnOpenFieldNothingWasTypedInJustCancelsItWithoutAsking()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        cut.Find("button[aria-label='Edit title']").Click();

        Assert.True(await EscAsync(cut));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("input[aria-label='Edit Title']")));
        Assert.Empty(host.FindAll(".app-confirm"));
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task ABackdropClickWithTypedInputAsksSaveOrDiscardThroughTheOverlayHostInsteadOfDroppingIt()
    {
        await UseDesktopAsync();
        var dialogs = Render<AppDialogHost>();
        _nav.NavigateTo("/watch");
        State.RememberPage("/watch", new RouteData(typeof(BlankPage), new Dictionary<string, object?>()));
        State.Attach();
        _nav.NavigateTo($"/details/movie/{MovieId}");
        var overlay = Render<DetailsOverlayHost>(parameters => parameters
            .AddCascadingValue(State)
            .AddChildContent<DetailPage>(page => page.Add(component => component.Model, Model())));
        overlay.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
        overlay.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Edit").Click();
        overlay.WaitForAssertion(() => Assert.NotEmpty(overlay.FindAll("button[aria-label='Edit title']")));
        overlay.Find("button[aria-label='Edit title']").Click();
        overlay.WaitForElement("input[aria-label='Edit Title']").Input("Dune");

        // app-dialog.js reports a click on the backdrop (and Esc outside a field, and the sheet's grip) through RequestCancel.
        var backdrop = overlay.InvokeAsync(() => overlay.Instance.RequestCancel());
        Answer(dialogs, "Discard");
        await backdrop;

        Assert.Empty(_steps);
        Assert.True(State.IsOpen);
        overlay.WaitForAssertion(() => Assert.Empty(overlay.FindAll("input[aria-label='Edit Title']")));
        Assert.Single(overlay.FindAll(".tl-editing-banner"));
        Assert.Empty(_saves);

        // Nothing open now: the next one dismisses the overlay, leaving Editing Mode first.
        await overlay.InvokeAsync(() => overlay.Instance.RequestCancel());
        Assert.Equal([1], _steps);
    }

    [Fact]
    public async Task TheOverlayOffersEscToTheEditingHandlerOnlyWhileEditingModeIsOn()
    {
        await UseDesktopAsync();
        var cut = RenderInOpenOverlay();
        Assert.False(await EscAsync(cut));

        ChooseEdit(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Edit title']")));
        cut.Find("button[aria-label='Edit title']").Click();
        Assert.True(await EscAsync(cut));

        cut.Find("button.tl-editing-banner__done").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-editing-banner")));
        Assert.False(await EscAsync(cut));
    }

    // -- Back, close and the backdrop ----------------------------------------------------------

    [Fact]
    public async Task TheDismissGuardRefusesWhenSaveOrDiscardIsCancelled_AndTheOverlayStaysOpen()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Dune");

        var dismissal = DismissAsync(cut);
        host.WaitForElement(".app-confirm");
        host.Find("button[aria-label='Close dialog']").Click();

        Assert.False(await dismissal);
        Assert.Empty(_steps);
        Assert.True(State.IsOpen);
        Assert.Single(cut.FindAll(".tl-editing-banner"));
        Assert.Equal("Dune", cut.Find("input[aria-label='Edit Title']").GetAttribute("value"));
    }

    [Fact]
    public async Task BrowserBackAsksSaveOrDiscard_AndCancelKeepsTheOverlayOpen()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Dune");

        _nav.NavigateTo("/watch");
        host.WaitForElement(".app-confirm");
        host.Find("button[aria-label='Close dialog']").Click();

        cut.WaitForAssertion(() => Assert.Empty(host.FindAll(".app-confirm")));
        Assert.EndsWith($"/details/movie/{MovieId}", _nav.Uri);
        Assert.True(State.IsOpen);
        Assert.Single(cut.FindAll(".tl-editing-banner"));
    }

    [Fact]
    public async Task TheDismissGuardLeavesEditModeAndLetsTheOverlayCloseWhenDiscardIsChosen()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Dune");

        var dismissal = DismissAsync(cut);
        Answer(host, "Discard");

        Assert.True(await dismissal);
        Assert.Equal([1], _steps);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-editing-banner")));
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task NoGuardIsRegisteredBeforeEditingStartsOrAfterItEnds()
    {
        await UseDesktopAsync();
        var cut = RenderInOpenOverlay();
        Assert.True(await CanDismissAsync(cut));

        ChooseEdit(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Edit title']")));
        cut.Find("button[aria-label='Edit title']").Click();
        cut.WaitForElement("input[aria-label='Edit Title']").Input("Dune");
        var host = Render<AppDialogHost>();
        var asked = CanDismissAsync(cut);
        host.WaitForElement(".app-confirm");
        host.Find("button[aria-label='Close dialog']").Click();
        Assert.False(await asked);

        cut.Find("input[aria-label='Edit Title']").KeyDown("Escape");
        cut.Find("button.tl-editing-banner__done").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-editing-banner")));

        Assert.True(await CanDismissAsync(cut));
        Assert.Empty(host.FindAll(".app-confirm"));
    }

    // -- Where things sit ----------------------------------------------------------------------

    [Fact]
    public async Task TheBannerIsTheFirstThingInThePageAndTheOpenSectionSitsDirectlyBelowTheHero()
    {
        var cut = await StartEditingAsync();

        var page = cut.Find("article.tl-detail-page");
        Assert.Contains("tl-editing-banner", page.FirstElementChild!.ClassList);

        cut.FindAll(".tl-editing-banner__section").Single(button => button.TextContent.Contains("History")).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-slot")));

        var slot = cut.Find(".tl-editing-slot");
        Assert.Contains("tl-detail-stage", slot.PreviousElementSibling!.ClassList);
        Assert.Equal(page, slot.ParentElement);
    }

    [Fact]
    public async Task MarkAsWatchedAndItsResetAreHiddenFromThePagesMenuWhileEditing_AndBackWhenDone()
    {
        await UseDesktopAsync();
        var cut = RenderInOpenOverlay();
        IReadOnlyList<string> PageMenu()
        {
            cut.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
            var items = cut.FindAll("[role='menuitem']").Select(item => item.TextContent.Trim()).ToList();
            cut.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
            return items;
        }

        Assert.Contains("Mark as watched", PageMenu());
        Assert.Contains("Mark as unwatched", PageMenu());

        ChooseEditFromOpenMenuAgain(cut);
        var whileEditing = PageMenu();

        Assert.DoesNotContain("Mark as watched", whileEditing);
        Assert.DoesNotContain("Mark as unwatched", whileEditing);
        Assert.Contains("Viewing history", whileEditing);
        Assert.DoesNotContain("Mark as watched", cut.Find(".tl-editing-banner").TextContent, StringComparison.OrdinalIgnoreCase);

        cut.Find("button.tl-editing-banner__done").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-editing-banner")));
        Assert.Contains("Mark as watched", PageMenu());
    }

    private static void ChooseEditFromOpenMenuAgain(IRenderedComponent<DetailPage> cut)
    {
        cut.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Edit").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-editing-banner")));
    }

    [Theory]
    [InlineData(DetailEntityType.Movie)]
    [InlineData(DetailEntityType.TvShow)]
    [InlineData(DetailEntityType.TvSeason)]
    [InlineData(DetailEntityType.TvEpisode)]
    [InlineData(DetailEntityType.MusicAlbum)]
    [InlineData(DetailEntityType.Book)]
    [InlineData(DetailEntityType.Audiobook)]
    [InlineData(DetailEntityType.ComicIssue)]
    [InlineData(DetailEntityType.Work)]
    public async Task NoDrawerRendersForAnyKindOfTitleBeforeDuringOrAfterEditing(DetailEntityType type)
    {
        await UseDesktopAsync();
        var cut = RenderInOpenOverlay(type);
        AssertNoDrawer(cut);

        ChooseEdit(cut);
        AssertNoDrawer(cut);
        cut.FindAll(".tl-editing-banner__section").Single(button => button.TextContent.Contains("History")).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editing-slot")));
        AssertNoDrawer(cut);
        Assert.Single(cut.FindAll(".tl-editing-slot"));

        cut.Find("button.tl-editing-banner__done").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-editing-banner")));
        AssertNoDrawer(cut);
    }

    private static void AssertNoDrawer(IRenderedComponent<DetailPage> cut)
    {
        Assert.DoesNotContain("tl-editing-drawer", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".tl-editing-drawer"));
    }

    private sealed class BlankPage : ComponentBase
    {
    }

    private sealed class StepRecorder(List<int> steps) : IDetailsOverlayHistory
    {
        public ValueTask<bool> GoBackAsync(int count)
        {
            steps.Add(count);
            return ValueTask.FromResult(true);
        }
    }
}
