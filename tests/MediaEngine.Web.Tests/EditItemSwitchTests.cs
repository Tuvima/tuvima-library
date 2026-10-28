using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Switching between the items of a page while editing (universal editor spec D7): the bar's trail is a row of dropdowns, choosing an item
/// loads its fields without leaving the page, every item's staged changes stay in the one pending list, Review changes can jump to any of
/// them, the "Unsaved changes" question covers all of them, and nothing is saved by switching. The Engine is a hand-written stub.
/// </summary>
public sealed class EditItemSwitchTests : AsyncBunitContext
{
    private static readonly Guid ShowId = EditTrailTestData.ShowId;
    private static readonly Guid SeasonOne = EditTrailTestData.SeasonOneId;
    private static readonly Guid SeasonTwo = EditTrailTestData.SeasonTwoId;
    private static readonly Guid EpisodeOne = EditTrailTestData.EpisodeOneId;
    private static readonly Guid EpisodeTwo = EditTrailTestData.EpisodeTwoId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<string> _writes = [];
    private readonly Dictionary<Guid, Func<Task<MediaEditorContextDto?>>> _contexts = [];
    private readonly IRenderedComponent<AppDialogHost> _dialogs;
    private readonly IRenderedComponent<AppPopoverHost> _popovers;

    public EditItemSwitchTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(Api());
        _popovers = Render<AppPopoverHost>();
        _dialogs = Render<AppDialogHost>();
        Known(ShowId, "series");
        Known(SeasonOne, "season");
        Known(EpisodeOne, "episode");
        Known(EpisodeTwo, "episode");
    }

    private void Known(Guid id, string scope) =>
        _contexts[id] = () => Task.FromResult<MediaEditorContextDto?>(EditTrailTestData.TvContext(id, scope));

    private IEngineApiClient Api() => EngineApiClientStub.Create(stub =>
    {
        stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), args =>
            _contexts.TryGetValue((Guid)args![0]!, out var answer) ? answer() : Task.FromResult<MediaEditorContextDto?>(null));
        stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
        stub.SetHandler(nameof(IEngineApiClient.SaveItemDisplayOverridesAsync), _ =>
        {
            _writes.Add("overrides");
            return Task.FromResult(true);
        });
        stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), args =>
        {
            var request = (EditSessionRequest)args![0]!;
            _writes.Add(request.PreviewOnly ? "check" : "save");
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
    });

    private sealed record Editor(EditingModeContext Mode, EditSessionState Session, EditSessionFlow Flow, List<string> Asked);

    private async Task<Editor> StartEditorAsync(bool discardWhenAsked = false, bool withCatalog = true)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var api = Services.GetRequiredService<IEngineApiClient>();
        var fields = new ItemFieldEditSession(api, _device);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = () => fields.LoadAsync(ShowId, "TV", "series", null);
        var session = new EditSessionState(new PendingEditList());
        var asked = new List<string>();
        var catalog = withCatalog ? EditTrailCatalog.From(EditTrailTestData.ShowPage()) : EditTrailCatalog.Empty;
        var flow = new EditSessionFlow(
            session,
            mode,
            new EditSessionSaver(api),
            () => catalog.PageTrail.Count > 0 ? catalog.PageTrail : [new EditTrailItem(ShowId, EditTrailTestData.ShowTitle, "Show")],
            () =>
            {
                asked.Add("unsaved");
                return Task.FromResult(discardWhenAsked);
            })
        {
            Catalog = catalog,
            OpenItem = async item =>
            {
                await fields.LoadAsync(item.EntityId, "TV", item.Kind switch { "Show" => "series", "Season" => "season", _ => "episode" }, null);
                return fields.Loaded && fields.EntityId == item.EntityId;
            },
        };
        Assert.True(await mode.EnterAsync());
        return new Editor(mode, session, flow, asked);
    }

    private IRenderedComponent<EditingBar> RenderBar(Editor editor) =>
        Render<EditingBar>(parameters => parameters.Add(bar => bar.Mode, editor.Mode).Add(bar => bar.Flow, editor.Flow));

    private IRenderedComponent<CascadingValue<EditingModeContext>> RenderPanel(Editor editor) =>
        Render<CascadingValue<EditingModeContext>>(parameters => parameters
            .Add(host => host.Value, editor.Mode)
            .Add(host => host.ChildContent, builder =>
            {
                builder.OpenComponent<EditorPanelHost>(0);
                builder.AddComponentParameter(1, nameof(EditorPanelHost.Mode), editor.Mode);
                builder.AddComponentParameter(2, nameof(EditorPanelHost.Flow), editor.Flow);
                builder.AddComponentParameter(3, nameof(EditorPanelHost.ChildContent), (RenderFragment<EditTool>)(tool => inner =>
                {
                    if (tool == EditTool.Details)
                    {
                        inner.OpenComponent<EditorDetailsPanel>(0);
                        inner.AddComponentParameter(1, nameof(EditorDetailsPanel.Mode), editor.Mode);
                        inner.AddComponentParameter(2, nameof(EditorDetailsPanel.Session), editor.Session);
                        inner.CloseComponent();
                    }
                }));
                builder.CloseComponent();
            }));

    // The dropdown's rows are drawn in the popover host, outside the bar.
    private IEnumerable<AngleSharp.Dom.IElement> MenuItems(IRenderedComponent<EditingBar> bar) =>
        bar.FindAll("[role^='menuitem']").Concat(_popovers.FindAll("[role^='menuitem']"));

    private void Choose(IRenderedComponent<EditingBar> bar, int step, string name)
    {
        bar.FindAll(".tl-editing-bar__step-trigger")[step].Click();
        bar.WaitForAssertion(() => Assert.Contains(MenuItems(bar), item => item.TextContent.Contains(name, StringComparison.Ordinal)));
        MenuItems(bar).First(item => item.TextContent.Contains(name, StringComparison.Ordinal)).Click();
    }

    // -- The trail ------------------------------------------------------------------------------

    [Fact]
    public async Task AStepWithChildren_IsADropdownOfTheItemAndItsChildren()
    {
        var editor = await StartEditorAsync();
        var bar = RenderBar(editor);

        bar.Find(".tl-editing-bar__step-trigger").Click();

        bar.WaitForAssertion(() =>
        {
            var names = MenuItems(bar).Select(item => item.TextContent.Trim()).ToList();
            Assert.Equal(["Le Continental", "Season 1", "Season 2"], names);
        });
        Assert.Equal("true", MenuItems(bar).First().GetAttribute("aria-checked"));
    }

    [Fact]
    public async Task WithNothingToSwitchTo_TheTrailIsJustTheName()
    {
        var editor = await StartEditorAsync(withCatalog: false);

        var bar = RenderBar(editor);

        Assert.Empty(bar.FindAll(".tl-editing-bar__step-trigger"));
        Assert.Contains("Le Continental", bar.Find(".tl-editing-bar__trail").TextContent);
    }

    // -- Switching ------------------------------------------------------------------------------

    [Fact]
    public async Task ChoosingASeason_SwitchesTheItemAndItsFields_KeepsEveryPendingChange_AndSavesNothing()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(ShowId, "title", "Continental");
        var bar = RenderBar(editor);

        Choose(bar, 0, "Season 1");

        bar.WaitForAssertion(() => Assert.Equal(SeasonOne, editor.Session.ActiveItem!.EntityId));
        Assert.Equal([ShowId, SeasonOne], editor.Session.Trail.Select(step => step.EntityId));
        Assert.Equal(SeasonOne, editor.Mode.Fields.EntityId);
        Assert.Equal("season", editor.Mode.Fields.ScopeId);
        Assert.Equal(1, editor.Session.Pending.Count);
        Assert.True(editor.Session.Pending.TryGetField(ShowId, "title", out var kept));
        Assert.Equal("Continental", kept);
        Assert.Empty(_writes);
        Assert.Equal(2, bar.FindAll(".tl-editing-bar__trail li").Count);
        Assert.Contains("Season 1", bar.Find(".tl-editing-bar__trail li.is-active").TextContent);
    }

    [Fact]
    public async Task ASeasonsEpisodesAreInItsDropdown_AndChoosingOneStagesItsOwnChanges()
    {
        var editor = await StartEditorAsync();
        var bar = RenderBar(editor);
        Choose(bar, 0, "Season 1");
        bar.WaitForAssertion(() => Assert.Equal(SeasonOne, editor.Session.ActiveItem!.EntityId));

        Choose(bar, 1, "Episode 2");

        bar.WaitForAssertion(() => Assert.Equal(EpisodeTwo, editor.Session.ActiveItem!.EntityId));
        Assert.Equal([ShowId, SeasonOne, EpisodeTwo], editor.Session.Trail.Select(step => step.EntityId));
        Assert.Equal("episode", editor.Mode.Fields.ScopeId);
        editor.Session.Pending.SetField(SeasonOne, "title", "Premiere");
        editor.Session.Pending.SetField(EpisodeTwo, "title", "Second");
        editor.Session.Pending.SetField(ShowId, "description", "A hotel.");
        var rows = editor.Flow.ReviewRows();
        Assert.Equal(
            ["Le Continental", "Season 1", "Episode 2"],
            rows.Select(row => row.Item).Distinct().OrderBy(name => name == "Le Continental" ? 0 : name == "Season 1" ? 1 : 2));
        Assert.All(rows, row => Assert.Equal(EditReviewGroup.Metadata, row.Group));
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task APendingChangeMarksItsItemInTheDropdown()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(SeasonOne, "title", "Premiere");
        var bar = RenderBar(editor);

        bar.Find(".tl-editing-bar__step-trigger").Click();

        bar.WaitForAssertion(() =>
        {
            var season = MenuItems(bar).First(item => item.TextContent.Contains("Season 1", StringComparison.Ordinal));
            Assert.NotEmpty(season.QuerySelectorAll(".tl-editing-bar__option-dot"));
            var show = MenuItems(bar).First(item => item.TextContent.Contains("Le Continental", StringComparison.Ordinal));
            Assert.Empty(show.QuerySelectorAll(".tl-editing-bar__option-dot"));
        });
    }

    [Fact]
    public async Task TheDetailsPanelReloadsForTheNewItem_AndShowsBusyWhileItLoads()
    {
        var gate = new TaskCompletionSource<MediaEditorContextDto?>();
        var editor = await StartEditorAsync();
        _contexts[SeasonOne] = () => gate.Task;
        await editor.Flow.SelectToolAsync(EditTool.Details);
        var panel = RenderPanel(editor);
        var bar = RenderBar(editor);
        panel.WaitForAssertion(() => Assert.NotEmpty(panel.FindAll(".tl-editor-details")));

        var switching = editor.Flow.SwitchToAsync(SeasonOne);

        bar.WaitForAssertion(() =>
        {
            Assert.True(editor.Flow.Switching);
            Assert.NotEmpty(panel.FindAll(".tl-editor-busy"));
            Assert.Empty(panel.FindAll(".tl-editor-details"));
            Assert.NotEmpty(bar.FindAll(".tl-editing-bar__opening"));
            Assert.All(bar.FindAll(".tl-editing-bar__tool"), tool => Assert.True(tool.HasAttribute("disabled")));
        });
        Assert.Equal(ShowId, editor.Session.ActiveItem!.EntityId);

        gate.SetResult(EditTrailTestData.TvContext(SeasonOne, "season"));
        Assert.True(await switching);

        panel.WaitForAssertion(() =>
        {
            Assert.Empty(panel.FindAll(".tl-editor-busy"));
            Assert.NotEmpty(panel.FindAll(".tl-editor-details"));
        });
        Assert.Equal(SeasonOne, editor.Mode.Fields.EntityId);
        Assert.False(editor.Flow.Switching);
    }

    [Fact]
    public async Task AnItemThatCannotBeOpened_KeepsThePersonOnTheirItem_WithTheReasonInTheBar()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(ShowId, "title", "Continental");
        var bar = RenderBar(editor);

        Choose(bar, 0, "Season 2");

        bar.WaitForAssertion(() => Assert.Contains("could not be opened", bar.Find(".tl-editing-bar__notice").TextContent, StringComparison.Ordinal));
        Assert.Contains("Season 2", bar.Find(".tl-editing-bar__notice").TextContent, StringComparison.Ordinal);
        Assert.Equal(ShowId, editor.Session.ActiveItem!.EntityId);
        Assert.Equal(ShowId, editor.Mode.Fields.EntityId);
        Assert.True(editor.Mode.Fields.Loaded);
        Assert.Equal(1, editor.Session.Pending.Count);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task ChoosingTheItemAlreadyBeingEdited_ChangesNothing()
    {
        var editor = await StartEditorAsync();
        var calls = 0;
        _contexts[ShowId] = () =>
        {
            calls++;
            return Task.FromResult<MediaEditorContextDto?>(EditTrailTestData.TvContext(ShowId, "series"));
        };

        var switched = await editor.Flow.SwitchToAsync(ShowId);

        Assert.True(switched);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SwitchingIsRefused_OnADeviceThatCannotEdit()
    {
        var editor = await StartEditorAsync();
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "phone", true));

        var switched = await editor.Flow.SwitchToAsync(SeasonOne);

        Assert.False(switched);
        Assert.Empty(_writes);
    }

    // -- Review changes and the unsaved-changes guard -------------------------------------------

    [Fact]
    public async Task ReviewEdit_JumpsToAnyStagedItem_EvenOneThePageNeverVisited()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(EpisodeOne, "title", "Pilot");
        var bar = RenderBar(editor);
        bar.Find("button.tl-editing-bar__save").Click();
        _dialogs.WaitForElement(".review-changes");

        _dialogs.Find("[aria-label='Metadata'] .review-changes__actions button").Click();

        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".review-changes")));
        Assert.Equal(EpisodeOne, editor.Session.ActiveItem!.EntityId);
        Assert.Equal([ShowId, SeasonOne, EpisodeOne], editor.Session.Trail.Select(step => step.EntityId));
        Assert.Equal(EpisodeOne, editor.Mode.Fields.EntityId);
        Assert.Equal(EditTool.Details, editor.Session.ActiveTool);
        Assert.Equal(1, editor.Session.Pending.Count);
        Assert.DoesNotContain("save", _writes);
    }

    [Fact]
    public async Task ReviewEdit_OnAnItemOnlyATickedRowKnew_StillSwitchesToIt()
    {
        var editor = await StartEditorAsync();
        var ticked = Guid.NewGuid();
        Known(ticked, "episode");
        editor.Session.Pending.SetField(ticked, "title", "Ticked");
        var bar = RenderBar(editor);
        bar.Find("button.tl-editing-bar__save").Click();
        _dialogs.WaitForElement(".review-changes");

        _dialogs.Find("[aria-label='Metadata'] .review-changes__actions button").Click();

        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".review-changes")));
        Assert.Equal(ticked, editor.Session.ActiveItem!.EntityId);
        Assert.Equal(ticked, editor.Mode.Fields.EntityId);
        Assert.Equal(1, editor.Session.Pending.Count);
    }

    [Fact]
    public async Task Cancel_AsksOnceForChangesOnSeveralItems_AndStayingKeepsAllOfThem()
    {
        var editor = await StartEditorAsync(discardWhenAsked: false);
        editor.Session.Pending.SetField(ShowId, "title", "Continental");
        var bar = RenderBar(editor);
        Choose(bar, 0, "Season 1");
        bar.WaitForAssertion(() => Assert.Equal(SeasonOne, editor.Session.ActiveItem!.EntityId));
        editor.Session.Pending.SetField(SeasonOne, "title", "Premiere");

        bar.Find("button.tl-editing-bar__cancel").Click();

        bar.WaitForAssertion(() => Assert.Equal(["unsaved"], editor.Asked));
        Assert.True(editor.Mode.IsOn);
        Assert.Equal(2, editor.Session.Pending.Count);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Cancel_AndDiscarding_DropsTheChangesOnEveryItem()
    {
        var editor = await StartEditorAsync(discardWhenAsked: true);
        editor.Session.Pending.SetField(ShowId, "title", "Continental");
        var bar = RenderBar(editor);
        Choose(bar, 0, "Season 1");
        bar.WaitForAssertion(() => Assert.Equal(SeasonOne, editor.Session.ActiveItem!.EntityId));
        editor.Session.Pending.SetField(SeasonOne, "title", "Premiere");

        bar.Find("button.tl-editing-bar__cancel").Click();

        bar.WaitForAssertion(() => Assert.False(editor.Mode.IsOn));
        Assert.True(editor.Session.Pending.IsEmpty);
        Assert.Empty(_writes);
    }

    // -- The page's own preview -----------------------------------------------------------------

    [Fact]
    public async Task ThePageKeepsPreviewingItsOwnItem_WhileAnotherItemIsEdited()
    {
        var editor = await StartEditorAsync();
        var preview = new EditPendingPreview(editor.Session, editor.Mode.Fields) { PageEntityId = ShowId };
        editor.Session.Pending.SetField(ShowId, "title", "Continental");
        await editor.Flow.SwitchToAsync(EpisodeOne);
        editor.Session.Pending.SetField(EpisodeOne, "title", "Pilot");

        Assert.Equal(EpisodeOne, editor.Mode.Fields.EntityId);
        Assert.Equal("Continental", preview.Title("Le Continental"));
        Assert.Equal("Le Continental", new EditPendingPreview(editor.Session, editor.Mode.Fields) { PageEntityId = SeasonTwo }.Title("Le Continental"));
    }

    [Fact]
    public async Task TheEnginesChainOfTheLoadedItem_NamesShowSeasonAndEpisode()
    {
        var editor = await StartEditorAsync();
        await editor.Flow.SwitchToAsync(EpisodeTwo);

        var chain = editor.Mode.Fields.ScopeChain;

        Assert.Equal([ShowId, SeasonOne, EpisodeTwo], chain.Select(step => step.EntityId));
        Assert.Equal(["Show", "Season", "Episode"], chain.Select(step => step.Kind));
    }
}
