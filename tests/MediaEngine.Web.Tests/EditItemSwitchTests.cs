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
/// Switching between the items of a family while editing (universal editor spec D3, D7): choosing an item (a row of the family tree)
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

    public EditItemSwitchTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(Api());
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
        var fields = new ItemFieldEditSession(api);
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

    // -- Switching ------------------------------------------------------------------------------

    [Fact]
    public async Task ChoosingASeason_SwitchesTheItemAndItsFields_KeepsEveryPendingChange_AndSavesNothing()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(ShowId, "title", "Continental");

        Assert.True(await editor.Flow.SwitchToAsync(SeasonOne));

        Assert.Equal(SeasonOne, editor.Session.ActiveItem!.EntityId);
        Assert.Equal([ShowId, SeasonOne], editor.Session.Trail.Select(step => step.EntityId));
        Assert.Equal(SeasonOne, editor.Mode.Fields.EntityId);
        Assert.Equal("season", editor.Mode.Fields.ScopeId);
        Assert.Equal(1, editor.Session.Pending.Count);
        Assert.True(editor.Session.Pending.TryGetField(ShowId, "title", out var kept));
        Assert.Equal("Continental", kept);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task ASeasonsEpisodes_AreReachableFromTheFamily_AndChoosingOneStagesItsOwnChanges()
    {
        var editor = await StartEditorAsync();
        Assert.True(await editor.Flow.SwitchToAsync(SeasonOne));
        Assert.Equal(SeasonOne, editor.Session.ActiveItem!.EntityId);

        Assert.True(await editor.Flow.SwitchToAsync(EpisodeTwo));

        Assert.Equal(EpisodeTwo, editor.Session.ActiveItem!.EntityId);
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
    public async Task TheDetailsPanelReloadsForTheNewItem_AndShowsBusyWhileItLoads()
    {
        var gate = new TaskCompletionSource<MediaEditorContextDto?>();
        var editor = await StartEditorAsync();
        _contexts[SeasonOne] = () => gate.Task;
        await editor.Flow.SelectToolAsync(EditTool.Details);
        var panel = RenderPanel(editor);
        panel.WaitForAssertion(() => Assert.NotEmpty(panel.FindAll(".tl-editor-details")));

        var switching = editor.Flow.SwitchToAsync(SeasonOne);

        panel.WaitForAssertion(() =>
        {
            Assert.True(editor.Flow.Switching);
            Assert.NotEmpty(panel.FindAll(".tl-editor-busy"));
            Assert.Empty(panel.FindAll(".tl-editor-details"));
            Assert.All(panel.FindAll(".tl-editor-window__tabs [role='tab']"), tool => Assert.True(tool.HasAttribute("disabled")));
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
    public async Task AnItemThatCannotBeOpened_KeepsThePersonOnTheirItem_WithTheReasonInTheWindow()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(ShowId, "title", "Continental");
        await editor.Flow.SelectToolAsync(EditTool.Details);
        var panel = RenderPanel(editor);

        Assert.False(await editor.Flow.SwitchToAsync(SeasonTwo));

        panel.WaitForAssertion(() => Assert.Contains("could not be opened", panel.Find(".tl-editor-window__notice").TextContent, StringComparison.Ordinal));
        Assert.Contains("Season 2", panel.Find(".tl-editor-window__notice").TextContent, StringComparison.Ordinal);
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
        await editor.Flow.SelectToolAsync(EditTool.Details);
        var panel = RenderPanel(editor);
        panel.Find("button.tl-editor-window__save").Click();
        panel.WaitForElement(".review-changes");

        panel.Find("[aria-label='Metadata'] .review-changes__actions button").Click();

        panel.WaitForAssertion(() => Assert.Empty(panel.FindAll(".review-changes")));
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
        await editor.Flow.SelectToolAsync(EditTool.Details);
        var panel = RenderPanel(editor);
        panel.Find("button.tl-editor-window__save").Click();
        panel.WaitForElement(".review-changes");

        panel.Find("[aria-label='Metadata'] .review-changes__actions button").Click();

        panel.WaitForAssertion(() => Assert.Empty(panel.FindAll(".review-changes")));
        Assert.Equal(ticked, editor.Session.ActiveItem!.EntityId);
        Assert.Equal(ticked, editor.Mode.Fields.EntityId);
        Assert.Equal(1, editor.Session.Pending.Count);
    }

    [Fact]
    public async Task Cancel_AsksOnceForChangesOnSeveralItems_AndStayingKeepsAllOfThem()
    {
        var editor = await StartEditorAsync(discardWhenAsked: false);
        editor.Session.Pending.SetField(ShowId, "title", "Continental");
        Assert.True(await editor.Flow.SwitchToAsync(SeasonOne));
        Assert.Equal(SeasonOne, editor.Session.ActiveItem!.EntityId);
        editor.Session.Pending.SetField(SeasonOne, "title", "Premiere");
        await editor.Flow.SelectToolAsync(EditTool.Details);
        var bar = RenderPanel(editor);

        bar.Find("button.tl-editor-window__cancel").Click();

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
        Assert.True(await editor.Flow.SwitchToAsync(SeasonOne));
        Assert.Equal(SeasonOne, editor.Session.ActiveItem!.EntityId);
        editor.Session.Pending.SetField(SeasonOne, "title", "Premiere");
        await editor.Flow.SelectToolAsync(EditTool.Details);
        var bar = RenderPanel(editor);

        bar.Find("button.tl-editor-window__cancel").Click();

        bar.WaitForAssertion(() => Assert.False(editor.Mode.IsOn));
        Assert.True(editor.Session.Pending.IsEmpty);
        Assert.Empty(_writes);
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
