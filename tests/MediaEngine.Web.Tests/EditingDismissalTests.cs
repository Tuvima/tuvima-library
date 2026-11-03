using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
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
    private readonly List<bool> _sessionCalls = [];
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
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), args =>
            {
                var request = (EditSessionRequest)args![0]!;
                _sessionCalls.Add(request.PreviewOnly);
                return Task.FromResult(LibraryBatchEditOutcome<EditSessionResult>.Ok(new EditSessionResult
                {
                    PreviewOnly = request.PreviewOnly,
                    Valid = true,
                    DataSaved = !request.PreviewOnly,
                    Changes = request.Changes.Select(change => new EditSessionChangeResult
                    {
                        Id = change.Id,
                        Kind = change.Kind,
                        EntityId = change.EntityId,
                        Status = EditSessionChangeStatuses.Ok,
                    }).ToList(),
                }));
            });
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
        _editor = Render<EditorPopover>();
    }

    private readonly IRenderedComponent<EditorPopover> _editor;

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

    private void ChooseEdit(IRenderedComponent<DetailPage> cut)
    {
        cut.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Edit").Click();
        _editor.WaitForAssertion(() => Assert.Single(_editor.FindAll(".tl-editor-window")));
        _editor.WaitForAssertion(() => Assert.NotEmpty(_editor.FindAll(".tl-editor-window__tool")));
    }

    private async Task<IRenderedComponent<DetailPage>> StartEditingAsync(DetailEntityType type = DetailEntityType.Movie)
    {
        await UseDesktopAsync();
        var cut = RenderInOpenOverlay(type);
        ChooseEdit(cut);
        return cut;
    }

    // The pop-over opens on Details by itself.
    private void OpenDetails() => _editor.WaitForElement("input[aria-label='Title']");

    private void TypeIntoTheTitle(IRenderedComponent<DetailPage> cut, string value)
    {
        OpenDetails();
        _editor.Find("input[aria-label='Title']").Input(value);
    }

    // Esc as the pop-over's dialog delivers it: app-dialog.js reports it through RequestCancel, which closes the editor (asking first when
    // changes are staged). False when no editor is open, so Esc is not the editor's.
    private async Task<bool> EscAsync(IRenderedComponent<DetailPage> cut)
    {
        var editor = Services.GetRequiredService<EditorHost>();
        if (!editor.IsOpen)
        {
            return false;
        }

        await cut.InvokeAsync(() => editor.RequestCloseAsync());
        return true;
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
        Assert.Equal("Unsaved changes", host.Find(".app-dialog-shell__title").TextContent.Trim());
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == label).Click();
    }

    // -- Cancel and "Unsaved changes" ------------------------------------------------------------

    [Fact]
    public async Task CancelWithStagedChangesAsksUnsavedChanges_AndDiscardChangesLeavesEditModeWritingNothing()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Dune");

        _editor.Find("button.tl-editor-window__cancel").Click();
        Answer(host, "Discard changes");

        _editor.WaitForAssertion(() => Assert.Empty(_editor.FindAll(".tl-editor-window")));
        Assert.Empty(_saves);
        Assert.Empty(_sessionCalls);
    }

    [Fact]
    public async Task CancelWithStagedChangesAndKeepEditing_StaysInEditModeWithTheChangesIntact()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Dune");

        _editor.Find("button.tl-editor-window__cancel").Click();
        Answer(host, "Keep editing");

        host.WaitForAssertion(() => Assert.Empty(host.FindAll(".app-confirm")));
        Assert.Single(_editor.FindAll(".tl-editor-window"));
        Assert.Equal("Dune", _editor.Find("input[aria-label='Title']").GetAttribute("value"));
        Assert.Equal("Save 1 change", _editor.Find("button.tl-editor-window__save").TextContent.Trim());
    }

    [Fact]
    public async Task CancelWithNothingStagedLeavesAtOnceWithoutAnyDialog()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();

        _editor.Find("button.tl-editor-window__cancel").Click();

        _editor.WaitForAssertion(() => Assert.Empty(_editor.FindAll(".tl-editor-window")));
        Assert.Empty(host.FindAll(".app-confirm"));
    }

    // -- Esc, in order ---------------------------------------------------------------------------

    [Fact]
    public async Task EscAsksAboutStagedChanges_AndKeepEditingLeavesTheWindowOpen()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Dune");

        // The editor is a window: with a change staged, Esc asks first; keeping the edit leaves the window and the change as they were.
        var esc = EscAsync(cut);
        Answer(host, "Keep editing");
        Assert.True(await esc);
        Assert.NotEmpty(_editor.FindAll(".tl-editor-panel"));
        Assert.Single(_editor.FindAll(".tl-editor-window"));
        Assert.Equal("Save 1 change", _editor.Find("button.tl-editor-window__save").TextContent.Trim());
        Assert.Empty(_steps);
    }

    [Fact]
    public async Task EscWithNothingStaged_ClosesThePopOverAtOnce_AndAnotherEscThenClosesTheOverlay()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();

        Assert.True(await EscAsync(cut));
        _editor.WaitForAssertion(() => Assert.Empty(_editor.FindAll(".tl-editor-window")));
        Assert.Empty(_steps);
        Assert.False(await EscAsync(cut));
        Assert.True(await DismissAsync(cut));

        Assert.Equal([1], _steps);
        Assert.Empty(host.FindAll(".app-confirm"));
    }

    [Fact]
    public async Task ABackdropClickWithStagedChangesAsksUnsavedChangesThroughTheOverlayHostInsteadOfDroppingThem()
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
        _editor.WaitForAssertion(() => Assert.NotEmpty(_editor.FindAll(".tl-editor-window__tool")));
        _editor.WaitForElement("input[aria-label='Title']").Input("Dune");

        // app-dialog.js reports a click on the backdrop (and Esc outside a field, and the sheet's grip) through RequestCancel. The editor is a
        // window, so with changes staged the very first one asks.
        var backdrop = overlay.InvokeAsync(() => overlay.Instance.RequestCancel());
        Answer(dialogs, "Keep editing");
        await backdrop;

        Assert.Empty(_steps);
        Assert.True(State.IsOpen);
        Assert.Single(_editor.FindAll(".tl-editor-window"));
        Assert.Equal("Save 1 change", _editor.Find("button.tl-editor-window__save").TextContent.Trim());
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task TheOverlayOffersEscToTheEditingHandlerOnlyWhileEditingIsOn()
    {
        await UseDesktopAsync();
        var cut = RenderInOpenOverlay();
        Assert.False(await EscAsync(cut));

        ChooseEdit(cut);
        OpenDetails();
        // Esc closes the editor window; with nothing staged that simply leaves editing.
        Assert.True(await EscAsync(cut));
        _editor.WaitForAssertion(() => Assert.Empty(_editor.FindAll(".tl-editor-window")));
        Assert.False(await EscAsync(cut));
    }

    // -- Back, close and the backdrop ------------------------------------------------------------

    [Fact]
    public async Task TheDismissGuardRefusesWhenUnsavedChangesIsClosed_AndTheOverlayStaysOpen()
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
        Assert.Single(_editor.FindAll(".tl-editor-window"));
        Assert.Equal("Dune", _editor.Find("input[aria-label='Title']").GetAttribute("value"));
    }

    [Fact]
    public async Task BrowserBackAsksUnsavedChanges_AndKeepEditingKeepsTheOverlayOpen()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Dune");

        _nav.NavigateTo("/watch");
        Answer(host, "Keep editing");

        host.WaitForAssertion(() => Assert.Empty(host.FindAll(".app-confirm")));
        Assert.EndsWith($"/details/movie/{MovieId}", _nav.Uri);
        Assert.True(State.IsOpen);
        Assert.Single(_editor.FindAll(".tl-editor-window"));
    }

    [Fact]
    public async Task TheDismissGuardLeavesEditModeAndLetsTheOverlayCloseWhenDiscardChangesIsChosen()
    {
        var host = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Dune");

        var dismissal = DismissAsync(cut);
        Answer(host, "Discard changes");

        Assert.True(await dismissal);
        Assert.Equal([1], _steps);
        _editor.WaitForAssertion(() => Assert.Empty(_editor.FindAll(".tl-editor-window")));
        Assert.Empty(_saves);
        Assert.Empty(_sessionCalls);
    }

    [Fact]
    public async Task NoGuardIsRegisteredBeforeEditingStartsOrAfterItEnds()
    {
        await UseDesktopAsync();
        var cut = RenderInOpenOverlay();
        Assert.True(await CanDismissAsync(cut));

        ChooseEdit(cut);
        _editor.WaitForElement("input[aria-label='Title']").Input("Dune");
        var host = Render<AppDialogHost>();
        var asked = CanDismissAsync(cut);
        host.WaitForElement(".app-confirm");
        host.Find("button[aria-label='Close dialog']").Click();
        Assert.False(await asked);

        _editor.Find("button.tl-editor-window__cancel").Click();
        Answer(host, "Discard changes");
        _editor.WaitForAssertion(() => Assert.Empty(_editor.FindAll(".tl-editor-window")));

        Assert.True(await CanDismissAsync(cut));
        Assert.Empty(host.FindAll(".app-confirm"));
    }

    // -- Staged changes, Save changes ------------------------------------------------------------

    [Fact]
    public async Task AStagedTitleShowsOnThePageAtOnce_AndNothingIsWrittenUntilSaveChanges()
    {
        var cut = await StartEditingAsync();

        TypeIntoTheTitle(cut, "Dune Messiah");

        cut.WaitForAssertion(() => Assert.Contains("Dune Messiah", cut.Find(".tl-detail-stage").TextContent));
        Assert.Empty(_saves);
        Assert.Empty(_sessionCalls);
    }

    [Fact]
    public async Task SaveChangesChecksThenSaves_AndLeavesEditModeOnThePage()
    {
        var dialogs = Render<AppDialogHost>();
        var cut = await StartEditingAsync();
        TypeIntoTheTitle(cut, "Dune Messiah");

        // Save changes opens Review changes; the save happens from its footer.
        _editor.Find("button.tl-editor-window__save").Click();
        _editor.WaitForAssertion(() => Assert.False(_editor.Find(".review-changes__footer button:last-child").HasAttribute("disabled")));
        _editor.Find(".review-changes__footer button:last-child").Click();

        _editor.WaitForAssertion(() => Assert.Empty(_editor.FindAll(".tl-editor-window")));
        Assert.Equal([true, true, false], _sessionCalls);
        Assert.Empty(_saves);
        Assert.Empty(cut.FindAll(".tl-editor-panel"));
    }

    // -- Where things sit ------------------------------------------------------------------------

    [Fact]
    public async Task ThePageBehindHasNoBarAndNoPanel_AndThePopOverIsDrawnOutsideIt()
    {
        var cut = await StartEditingAsync();

        _editor.WaitForAssertion(() => Assert.Single(_editor.FindAll(".tl-editor-panel")));

        // The page is never put into an editing state: no bar, no panel in its flow, its stage is its first child.
        Assert.Empty(cut.FindAll(".tl-editing-bar"));
        Assert.Empty(cut.FindAll(".tl-editor-panel"));
        var page = cut.Find("article.tl-detail-page");
        Assert.Contains("tl-detail-stage", page.FirstElementChild!.ClassList);
        Assert.Single(cut.FindAll(".tl-detail-stage"));
    }

    [Fact]
    public async Task ThePencilsTheOldBannerAndTheSectionBelowTheHeroStayGoneWhileThePopOverIsOpen()
    {
        var cut = await StartEditingAsync();
        _editor.FindAll(".tl-editor-window__tool").Single(tool => tool.TextContent.Trim() == "Match").Click();
        _editor.WaitForAssertion(() => Assert.Single(_editor.FindAll(".tl-editor-panel")));

        Assert.Empty(cut.FindAll(".tl-editing-pencil"));
        Assert.Empty(cut.FindAll("button[aria-label='Edit title']"));
        Assert.Empty(cut.FindAll(".tl-editing-banner"));
        Assert.Empty(cut.FindAll(".tl-editing-hero-controls button"));
        Assert.Empty(cut.FindAll(".tl-editing-slot"));
    }

    [Fact]
    public async Task ThePagesMenuIsTheSameWhileThePopOverIsOpen_AndAfterCancel()
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

        Assert.Contains("Mark as watched", whileEditing);
        Assert.Contains("Mark as unwatched", whileEditing);
        Assert.Contains("Viewing history", whileEditing);
        Assert.DoesNotContain("Mark as watched", _editor.Find(".tl-editor-window").TextContent, StringComparison.OrdinalIgnoreCase);

        _editor.Find("button.tl-editor-window__cancel").Click();
        _editor.WaitForAssertion(() => Assert.Empty(_editor.FindAll(".tl-editor-window")));
        Assert.Contains("Mark as watched", PageMenu());
    }

    private void ChooseEditFromOpenMenuAgain(IRenderedComponent<DetailPage> cut)
    {
        cut.Find(".tl-detail-secondary-actions button[aria-label='More actions']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Edit").Click();
        _editor.WaitForAssertion(() => Assert.Single(_editor.FindAll(".tl-editor-window")));
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
        _editor.WaitForAssertion(() => Assert.Single(_editor.FindAll(".tl-editor-panel")));
        AssertNoDrawer(cut);

        _editor.Find("button.tl-editor-window__cancel").Click();
        _editor.WaitForAssertion(() => Assert.Empty(_editor.FindAll(".tl-editor-window")));
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
