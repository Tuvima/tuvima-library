using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The universal editor's pop-over window (spec v3 WP1, WP5): what its footer shows before and after something is staged, which
/// tools it offers, the "Unsaved changes" question, the panel closing without ending the edit, the Details tool staging instead of
/// saving, provider facts staying read-only, and Save changes checking before it saves and reporting each change that failed.
/// </summary>
public sealed class UniversalEditorWindowTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<bool> _sessionCalls = [];
    private readonly List<string> _overrideSaves = [];
    private Func<EditSessionRequest, EditSessionResult> _answer = request => Ok(request);
    private int _refreshes;
    private MediaEditorContextDto? _context;
    private readonly IRenderedComponent<AppDialogHost> _dialogs;

    public UniversalEditorWindowTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        // Save changes opens Review changes, which reads the write-back setting through the same client the editor uses.
        Services.AddSingleton(Api());
        Services.AddSingleton(new DashboardSessionAccessor());
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Render<AppPopoverHost>();
        _dialogs = Render<AppDialogHost>();
    }

    // Save changes opens Review changes as a page of the window; saving happens from its footer.
    private static void SaveFromReview(IRenderedComponent<CascadingValue<EditingModeContext>> window)
    {
        window.Find("button.tl-editor-window__save").Click();
        window.WaitForElement(".review-changes");
        window.WaitForAssertion(() => Assert.False(window.Find(".review-changes__footer button:last-child").HasAttribute("disabled")));
        window.Find(".review-changes__footer button:last-child").Click();
    }

    private Task UseDesktopAsync() =>
        _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

    private IEngineApiClient Api()
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(_context ?? EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.SaveItemDisplayOverridesAsync), _ =>
            {
                _overrideSaves.Add("saved");
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetEditorFollowUpAsync), _ => Task.FromResult(_followUp));
            stub.SetHandler(nameof(IEngineApiClient.GetEditorQuickFactsAsync), _ => Task.FromResult(_quickFacts));
            stub.SetHandler(nameof(IEngineApiClient.GetItemExplicitAsync), _ =>
            {
                _explicitAsks++;
                return Task.FromResult<MediaEngine.Contracts.Items.ItemExplicitResponse?>(
                    new MediaEngine.Contracts.Items.ItemExplicitResponse { EntityId = MovieId, IsExplicit = _explicitSaved, Supported = true });
            });
            stub.SetHandler(nameof(IEngineApiClient.SaveItemExplicitAsync), _ =>
            {
                _explicitDirectSaves++;
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), args =>
            {
                var request = (EditSessionRequest)args![0]!;
                _sessionCalls.Add(request.PreviewOnly);
                return Task.FromResult(LibraryBatchEditOutcome<EditSessionResult>.Ok(_answer(request)));
            });
        });
        return api;
    }

    private EditorFollowUpDto? _followUp;
    private ItemQuickFactsDto? _quickFacts;
    private readonly bool _explicitSaved = false;
    private int _explicitAsks;
    private int _explicitDirectSaves;

    [Fact]
    public async Task AfterAMatchSave_TheRibbonListsTheNamedSteps_AndShowsAnAmberReasonWhenOneStops()
    {
        _followUp = new EditorFollowUpDto
        {
            Steps =
            [
                new EditorFollowUpStepDto { Key = EditorFollowUpSteps.Matched, Label = "Matched", State = EditorFollowUpStates.Done },
                new EditorFollowUpStepDto { Key = EditorFollowUpSteps.Details, Label = "Details from the providers", State = EditorFollowUpStates.Failed, Reason = "TMDB did not answer." },
            ],
        };
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetTarget(EditSessionChangeKinds.Identity, MovieId, "provider-1");
        var bar = RenderBar(editor);
        var ribbon = Render<EditSavedRibbon>(parameters => parameters.Add(component => component.Flow, editor.Flow));

        SaveFromReview(bar);

        ribbon.WaitForAssertion(() => Assert.Equal(2, ribbon.FindAll(".tl-saved-ribbon__steps li").Count));
        Assert.Contains("Details from the providers: TMDB did not answer.", ribbon.Find(".tl-saved-ribbon").TextContent, StringComparison.Ordinal);
        Assert.Contains("Needs Review", ribbon.Find(".tl-saved-ribbon").TextContent, StringComparison.Ordinal);
    }

    private static EditSessionResult Ok(EditSessionRequest request) => new()
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
    };

    private sealed record Editor(EditingModeContext Mode, EditSessionState Session, EditSessionFlow Flow, List<string> Asked);

    private async Task<Editor> StartEditorAsync(MediaEditorContextDto? context = null, bool discardWhenAsked = false, bool turnOn = true)
    {
        await UseDesktopAsync();
        _context = context;
        var api = Services.GetRequiredService<IEngineApiClient>();
        var fields = new ItemFieldEditSession(api);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = () => fields.LoadAsync(MovieId, context?.MediaType ?? "Movies", context?.InitialScope ?? "movie", null);
        var session = new EditSessionState(new PendingEditList());
        var asked = new List<string>();
        var flow = new EditSessionFlow(
            session,
            mode,
            new EditSessionSaver(api),
            () => [new EditTrailItem(MovieId, "Dune: Part One", "Movie")],
            () =>
            {
                asked.Add("unsaved");
                return Task.FromResult(discardWhenAsked);
            })
        {
            Saved = () =>
            {
                _refreshes++;
                return Task.CompletedTask;
            },
        };
        if (turnOn)
        {
            Assert.True(await mode.EnterAsync());
        }

        return new Editor(mode, session, flow, asked);
    }

    // The window as the pop-over draws it: the Details tool open (the host opens it first), with the History tab where the page keeps history.
    private IRenderedComponent<CascadingValue<EditingModeContext>> RenderBar(Editor editor)
    {
        editor.Flow.SelectToolAsync(EditTool.Details).GetAwaiter().GetResult();
        return RenderPanel(editor);
    }

    private IRenderedComponent<CascadingValue<EditingModeContext>> RenderPanel(Editor editor) =>
        Render<CascadingValue<EditingModeContext>>(parameters => parameters
            .Add(host => host.Value, editor.Mode)
            .Add(host => host.ChildContent, builder =>
            {
                builder.OpenComponent<EditorPanelHost>(0);
                builder.AddComponentParameter(1, nameof(EditorPanelHost.Mode), editor.Mode);
                builder.AddComponentParameter(2, nameof(EditorPanelHost.Flow), editor.Flow);
                builder.AddComponentParameter(8, nameof(EditorPanelHost.ShowHistory), true);
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

    private static MediaEditorContextDto TvContext(string scopeId) => new()
    {
        LaunchEntityId = MovieId,
        MediaType = "TV",
        InitialScope = scopeId,
        AvailableTabs = ["details", "artwork", "links", "history"],
        Scopes =
        [
            new MediaEditorScopeDto
            {
                ScopeId = scopeId,
                Label = scopeId,
                FieldEntityId = MovieId,
                DisplayTitle = "A show",
                CanEditFields = true,
                CanEditArtwork = true,
                ArtworkSlots = ["CoverArt"],
                FieldSnapshot = new MediaEditorScopeFieldSnapshotDto(),
            },
        ],
    };

    private static MediaEditorContextDto MovieWithTitleOverride()
    {
        var context = EditingModeTestData.MovieContext();
        context.Scopes[0].FieldSnapshot.DisplayOverrides["title"] = "Dune (my cut)";
        return context;
    }

    // -- The window's states ----------------------------------------------------------------------

    [Fact]
    public async Task WithNothingPending_SaveIsOffAndSaysNoChangesYet()
    {
        var editor = await StartEditorAsync();

        var cut = RenderBar(editor);

        var save = cut.Find("button.tl-editor-window__save");
        Assert.True(save.HasAttribute("disabled"));
        Assert.Equal("Save changes", save.TextContent.Trim());
        Assert.Equal("No changes yet", cut.Find(".tl-editor-window__changes").TextContent.Trim());
    }

    [Fact]
    public async Task OncePendingChangesExist_SaveShowsTheCountAndIsOn()
    {
        var editor = await StartEditorAsync();
        var cut = RenderBar(editor);

        editor.Session.Pending.SetField(MovieId, "title", "Dune");
        editor.Session.Pending.SetField(MovieId, "tagline", "Fear is the mind-killer");
        editor.Session.Pending.SetField(MovieId, "rating", "9");

        cut.WaitForAssertion(() =>
        {
            var save = cut.Find("button.tl-editor-window__save");
            Assert.False(save.HasAttribute("disabled"));
            Assert.Equal("Save changes", save.TextContent.Trim());
            Assert.Equal("3 unsaved changes", cut.Find(".tl-editor-window__changes").TextContent.Trim());
        });
    }

    [Fact]
    public async Task TheWindowOffersDetailsArtworkMatchAndHistoryForAMovie()
    {
        var editor = await StartEditorAsync();

        var cut = RenderBar(editor);

        Assert.Equal(["Details", "Artwork", "Match", "History"], cut.FindAll(".tl-editor-window__tabs [role='tab']").Select(tool => tool.TextContent.Trim()));
    }

    [Fact]
    public async Task MatchIsHiddenWhereTheEditorCannotMatch_AndArtworkWhereItCannotChangeArtwork()
    {
        var noMatch = TvContext("group");
        var editor = await StartEditorAsync(noMatch);
        Assert.Equal(["Details", "Artwork", "History"], RenderBar(editor).FindAll(".tl-editor-window__tabs [role='tab']").Select(tool => tool.TextContent.Trim()));

        var noArtwork = EditingModeTestData.MovieContext();
        noArtwork.Scopes[0].CanEditArtwork = false;
        var second = await StartEditorAsync(noArtwork);
        Assert.Equal(["Details", "Match", "History"], RenderBar(second).FindAll(".tl-editor-window__tabs [role='tab']").Select(tool => tool.TextContent.Trim()));
    }

    [Fact]
    public async Task TheWindowIsNotDrawnWhileTheEditorIsClosed_OrOnADeviceThatCannotEdit()
    {
        var off = await StartEditorAsync(turnOn: false);
        Assert.Empty(RenderBar(off).FindAll(".tl-editor-window"));

        var on = await StartEditorAsync();
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "tablet", true));
        Assert.Empty(RenderBar(on).FindAll(".tl-editor-window"));
    }

    // -- Cancel and "Unsaved changes" ------------------------------------------------------------

    [Fact]
    public async Task CancelWithNothingPending_ClosesTheEditorWithoutAsking()
    {
        var editor = await StartEditorAsync();
        var cut = RenderBar(editor);

        cut.Find("button.tl-editor-window__cancel").Click();

        cut.WaitForAssertion(() => Assert.False(editor.Mode.IsOn));
        Assert.Empty(editor.Asked);
        Assert.False(editor.Session.IsActive);
    }

    [Fact]
    public async Task CancelWithPendingChanges_AsksUnsavedChanges_AndKeepEditingChangesNothing()
    {
        var editor = await StartEditorAsync(discardWhenAsked: false);
        editor.Session.Pending.SetField(MovieId, "title", "Dune");
        var cut = RenderBar(editor);

        cut.Find("button.tl-editor-window__cancel").Click();

        cut.WaitForAssertion(() => Assert.Single(editor.Asked));
        Assert.True(editor.Mode.IsOn);
        Assert.Equal(1, editor.Session.Pending.Count);
    }

    [Fact]
    public async Task CancelWithPendingChanges_DiscardingThemClosesTheEditorWithAnEmptyList()
    {
        var editor = await StartEditorAsync(discardWhenAsked: true);
        editor.Session.Pending.SetField(MovieId, "title", "Dune");
        var cut = RenderBar(editor);

        cut.Find("button.tl-editor-window__cancel").Click();

        cut.WaitForAssertion(() => Assert.False(editor.Mode.IsOn));
        Assert.True(editor.Session.Pending.IsEmpty);
        Assert.Empty(_sessionCalls);
    }

    [Fact]
    public async Task UnsavedChangesDialog_UsesTheSharedWarningDialog_WithDiscardAndKeepEditing()
    {
        var host = Render<AppDialogHost>();
        var dialogs = Services.GetRequiredService<IAppDialogService>();

        var answer = EditingUnsavedChangesDialog.AskDiscardAsync(dialogs);

        host.WaitForElement(".app-confirm");
        Assert.Equal("Unsaved changes", host.Find(".app-dialog-shell__title").TextContent.Trim());
        Assert.Equal(["Discard changes", "Keep editing"], host.FindAll(".app-dialog-shell__footer button").Select(button => button.TextContent.Trim()));
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Discard changes").Click();
        Assert.True(await answer);
    }

    [Fact]
    public async Task UnsavedChangesDialog_KeepEditingAndClosingBothMeanStay()
    {
        var host = Render<AppDialogHost>();
        var dialogs = Services.GetRequiredService<IAppDialogService>();

        var answer = EditingUnsavedChangesDialog.AskDiscardAsync(dialogs);
        host.WaitForElement(".app-confirm");
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Keep editing").Click();

        Assert.False(await answer);
    }

    [Fact]
    public async Task LeavingThePageWithPendingChanges_GoesThroughTheSameQuestion()
    {
        var editor = await StartEditorAsync(discardWhenAsked: false);
        editor.Session.Pending.SetField(MovieId, "title", "Dune");

        Assert.False(await editor.Flow.CanDismissAsync());
        Assert.Single(editor.Asked);
        Assert.True(editor.Mode.IsOn);
    }

    // -- The panel -------------------------------------------------------------------------------

    [Fact]
    public async Task TheWindowIsClosedUntilAToolIsChosen_AndHasTheSameSizeForEveryTool()
    {
        var editor = await StartEditorAsync();
        var cut = RenderPanel(editor);
        Assert.Empty(cut.FindAll(".tl-editor-panel"));

        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-editor-window")));
        Assert.Equal("Details", cut.Find(".tl-editor-panel__title").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll(".tl-editor-window__cancel"));
        Assert.NotEmpty(cut.FindAll(".tl-editor-window__save"));

        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Match));
        cut.WaitForAssertion(() => Assert.Equal("match", cut.Find(".tl-editor-window").GetAttribute("data-tool")));
        Assert.NotEmpty(cut.FindAll(".tl-editor-window__save"));
    }

    [Fact]
    public async Task HistoryIsATabOfTheWindow_AndOpeningDetailsAgainClosesIt()
    {
        var editor = await StartEditorAsync();
        var cut = RenderPanel(editor);

        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.WaitForAssertion(() => Assert.Contains("History", cut.FindAll(".tl-editor-window__tabs [role='tab']").Select(tool => tool.TextContent.Trim())));

        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.History));
        cut.WaitForAssertion(() => Assert.Equal("history", cut.Find(".tl-editor-window").GetAttribute("data-tool")));
        Assert.Equal(EditingSection.History, editor.Mode.OpenSectionKind);
        Assert.Equal("History", cut.Find(".tl-editor-panel__title").TextContent.Trim());

        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.WaitForAssertion(() => Assert.Equal("details", cut.Find(".tl-editor-window").GetAttribute("data-tool")));
        Assert.Equal(EditingSection.Details, editor.Mode.OpenSectionKind);
    }

    [Fact]
    public async Task ClosingTheWindowWithChangesStaged_Asks_AndKeepEditingKeepsEverything()
    {
        var editor = await StartEditorAsync(discardWhenAsked: false);
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editor-details")));
        editor.Session.Pending.SetField(MovieId, "title", "Dune");

        cut.Find("button.tl-editor-panel__close").Click();

        cut.WaitForAssertion(() => Assert.Single(editor.Asked));
        Assert.NotEmpty(cut.FindAll(".tl-editor-panel"));
        Assert.True(editor.Mode.IsOn);
        Assert.True(editor.Session.IsActive);
        Assert.Equal(1, editor.Session.Pending.Count);
    }

    [Fact]
    public async Task OpeningTheWindow_MovesFocusIntoIt_AndClosingItWithNothingStaged_LeavesEditing()
    {
        var editor = await StartEditorAsync();
        var panel = RenderPanel(editor);

        await panel.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        panel.WaitForAssertion(() => Assert.Contains(FocusedIds(), id => id == "tl-editor-panel-title"));

        panel.Find("button.tl-editor-panel__close").Click();

        panel.WaitForAssertion(() => Assert.False(editor.Mode.IsOn));
    }

    [Fact]
    public async Task SaveInTheWindow_OpensReviewAsAPageOfTheWindow_AndBackToEditingReturnsToTheTool()
    {
        var editor = await StartEditorAsync();
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editor-details")));
        editor.Session.Pending.SetField(MovieId, "title", "Dune");

        cut.Find(".tl-editor-window__save").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".review-changes--page")));
        Assert.Equal("Review changes", cut.Find(".tl-editor-panel__title").TextContent.Trim());
        Assert.Empty(cut.FindAll(".app-dialog-shell"));
        Assert.Empty(cut.FindAll(".tl-editor-window__save"));
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Back to editing").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editor-details")));
        Assert.Equal("Details", cut.Find(".tl-editor-panel__title").TextContent.Trim());
        Assert.True(editor.Session.IsActive);
    }

    private List<string?> FocusedIds() => JSInterop.Invocations
        .Where(call => call.Identifier == "tuvimaFocusById")
        .Select(call => call.Arguments[0] as string)
        .ToList();

    [Fact]
    public async Task EscAsksUnsavedChangesWhenChangesArePending_AndKeepEditingLeavesTheWindowOpen()
    {
        var editor = await StartEditorAsync(discardWhenAsked: false);
        await editor.Flow.SelectToolAsync(EditTool.Details);
        editor.Session.Pending.SetField(MovieId, "title", "Dune");

        // The editor is a window: Esc asks "Unsaved changes" straight away when something is staged, and Keep editing leaves it open.
        Assert.True(await editor.Flow.HandleEscapeAsync());
        Assert.Single(editor.Asked);
        Assert.True(editor.Mode.IsOn);
        Assert.Equal(EditTool.Details, editor.Session.ActiveTool);
    }

    [Fact]
    public async Task EscWithNothingPendingAndNoPanel_IsNotUsed_SoTheOverlayCanClose()
    {
        var editor = await StartEditorAsync();

        Assert.False(await editor.Flow.HandleEscapeAsync());
    }

    // -- The Explicit mark (videos and series of videos only) ------------------------------------

    private static MediaEditorContextDto VideoContext(string mediaType = "Videos")
    {
        var context = EditingModeTestData.MovieContext();
        context.MediaType = mediaType;
        return context;
    }

    [Fact]
    public async Task ExplicitSwitch_ShowsForAVideo_WithAnInfoTooltip()
    {
        var editor = await StartEditorAsync(VideoContext());
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));

        var row = cut.WaitForElement(".tl-editor-explicit");
        Assert.Contains("Explicit", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("content limit", row.TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll(".tl-editor-explicit input[role='switch']"));
    }

    [Fact]
    public async Task ExplicitSwitch_IsAbsentForABook_AndTheEngineIsNotAsked()
    {
        var editor = await StartEditorAsync(VideoContext("Books"));
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.WaitForElement("input[aria-label='Title']");

        Assert.Empty(cut.FindAll(".tl-editor-explicit"));
        Assert.Equal(0, _explicitAsks);
    }

    [Fact]
    public async Task TurningExplicitOn_OnlyStagesIt_AndTurningItBackOffTakesItOut()
    {
        var editor = await StartEditorAsync(VideoContext());
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));

        cut.WaitForElement(".tl-editor-explicit input[role='switch']").Change(true);

        cut.WaitForAssertion(() =>
        {
            Assert.True(editor.Session.Pending.TryGetField(MovieId, ItemFieldEditSession.ExplicitFieldKey, out var staged));
            Assert.Equal("true", staged);
        });
        Assert.Equal(0, _explicitDirectSaves);
        Assert.Empty(_sessionCalls);

        cut.Find(".tl-editor-explicit input[role='switch']").Change(false);
        cut.WaitForAssertion(() => Assert.True(editor.Session.Pending.IsEmpty));
    }

    [Fact]
    public async Task StagedExplicit_ReadsOffThenOnInReview_AndSaveSendsItAsADetailsChange()
    {
        var editor = await StartEditorAsync(VideoContext());
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.WaitForElement(".tl-editor-explicit input[role='switch']").Change(true);
        cut.WaitForAssertion(() => Assert.Equal(1, editor.Session.Pending.Count));

        var row = Assert.Single(EditReviewRows.Build(editor.Session.Pending, editor.Session, editor.Mode.Fields));
        Assert.Equal("Explicit", row.What);
        Assert.Equal("Off", row.Before);
        Assert.Equal("On", row.After);

        EditSessionRequest? sent = null;
        _answer = request =>
        {
            if (!request.PreviewOnly)
            {
                sent = request;
            }

            return Ok(request);
        };
        SaveFromReview(cut);

        cut.WaitForAssertion(() => Assert.NotNull(sent));
        var field = Assert.Single(Assert.Single(sent!.Changes).Fields);
        Assert.Equal("explicit", field.Key);
        Assert.Equal("true", field.Value);
        Assert.Equal(0, _explicitDirectSaves);
    }

    [Fact]
    public async Task CancellingTheEditor_DiscardsAStagedExplicit_WithoutSavingIt()
    {
        var editor = await StartEditorAsync(VideoContext(), discardWhenAsked: true);
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.WaitForElement(".tl-editor-explicit input[role='switch']").Change(true);
        cut.WaitForAssertion(() => Assert.Equal(1, editor.Session.Pending.Count));

        await cut.InvokeAsync(() => editor.Flow.LeaveAsync());

        Assert.True(editor.Session.Pending.IsEmpty);
        Assert.Equal(0, _explicitDirectSaves);
        Assert.Empty(_sessionCalls);
    }

    // -- The Details tool ------------------------------------------------------------------------

    [Fact]
    public async Task TypingInADetailsField_StagesTheChange_AndWritesNothingUntilSave()
    {
        var editor = await StartEditorAsync();
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));

        cut.WaitForElement("input[aria-label='Title']");
        cut.UnlockField("title");
        cut.Find("input[aria-label='Title']").Input("Dune 2");

        cut.WaitForAssertion(() =>
        {
            Assert.True(editor.Session.Pending.TryGetField(MovieId, "title", out var staged));
            Assert.Equal("Dune 2", staged);
        });
        Assert.Empty(_overrideSaves);
        Assert.Empty(_sessionCalls);
    }

    [Fact]
    public async Task TypingTheValueTheItemAlreadyHas_TakesTheStagedChangeBackOut()
    {
        var editor = await StartEditorAsync();
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.WaitForElement("input[aria-label='Title']");
        cut.UnlockField("title");
        var title = cut.Find("input[aria-label='Title']");

        title.Input("Dune 2");
        cut.WaitForAssertion(() => Assert.Equal(1, editor.Session.Pending.Count));
        cut.Find("input[aria-label='Title']").Input("Dune: Part One");

        cut.WaitForAssertion(() => Assert.True(editor.Session.Pending.IsEmpty));
    }

    [Fact]
    public async Task ProviderFacts_AreNotRepeatedInTheEditableDetails()
    {
        var context = EditingModeTestData.MovieContext();
        context.Scopes[0].FieldSnapshot.AllowedOverrideKeys = ["title", "description"];
        var editor = await StartEditorAsync(context);
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.WaitForElement(".tl-editor-details");

        Assert.Empty(cut.FindAll(".tl-editor-field--locked"));
        Assert.Single(cut.FindAll("input[aria-label='Title']"));
        Assert.Empty(cut.FindAll(".tl-editor-field[data-field-key='rating']"));
    }

    [Fact]
    public async Task AnOverriddenField_IsYours_AndRevertStagesAnEmptyValue()
    {
        var editor = await StartEditorAsync(MovieWithTitleOverride());
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        var title = cut.WaitForElement(".tl-editor-field[data-field-key='title']");
        Assert.Equal("yours", cut.LockStateOf("title"));

        title.QuerySelectorAll("button").Single(button => button.GetAttribute("aria-label") == "Reset to provider value").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(editor.Session.Pending.TryGetField(MovieId, "title", out var staged));
            Assert.True(string.IsNullOrEmpty(staged));
            Assert.Contains("provider's value", cut.Find(".tl-editor-field[data-field-key='title'] .tl-editor-field__note").TextContent);
        });
        Assert.Empty(_overrideSaves);
    }

    // -- Locks by default (editor v5 D5, WP6) ------------------------------------------------------
    // Two meanings are kept apart: the form lock only stops accidental typing (a field nobody changed stays the provider's), and a changed
    // field is "yours" (kept on every provider refresh, with Revert).

    [Fact]
    public async Task EveryProviderField_OpensLocked_WithAnAccentLockToClickAndADimmedValue()
    {
        var editor = await StartEditorAsync();
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.WaitForElement(".tl-editor-details");

        var providerFields = cut.FindAll(".tl-editor-field[data-field-key]:not([data-field-key='custom_tags'])");

        Assert.NotEmpty(providerFields);
        Assert.All(providerFields, field =>
        {
            Assert.Equal("true", field.GetAttribute("data-locked"));
            Assert.Contains("tl-editor-field--form-locked", field.ClassName, StringComparison.Ordinal);
            var toggle = field.QuerySelector("button.tl-editor-field__lock-toggle")!;
            Assert.Equal("Unlock to edit", toggle.GetAttribute("aria-label"));
            Assert.Equal("locked", field.QuerySelector("[data-lock-state]")!.GetAttribute("data-lock-state"));
        });
        Assert.All(cut.FindAll(".tl-editor-field[data-field-key='title'] input, .tl-editor-field[data-field-key='description'] textarea"),
            input => Assert.True(input.HasAttribute("disabled")));
        Assert.True(editor.Session.Pending.IsEmpty);
    }

    [Fact]
    public async Task UnlockingAField_StagesNothing_AndLockingItAgainStagesNothing()
    {
        var editor = await StartEditorAsync();
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        var field = cut.WaitForElement(".tl-editor-field[data-field-key='title']");

        field.QuerySelector("button.tl-editor-field__lock-toggle")!.Click();

        cut.WaitForAssertion(() =>
        {
            var open = cut.Find(".tl-editor-field[data-field-key='title']");
            Assert.Equal("false", open.GetAttribute("data-locked"));
            Assert.Equal("false", open.GetAttribute("data-changed"));
            Assert.Equal("unlocked", cut.LockStateOf("title"));
            Assert.Equal("Lock", open.QuerySelector("button.tl-editor-field__lock-toggle")!.GetAttribute("aria-label"));
            Assert.False(cut.Find("input[aria-label='Title']").HasAttribute("disabled"));
        });
        Assert.True(editor.Session.Pending.IsEmpty);

        cut.Find(".tl-editor-field[data-field-key='title'] button.tl-editor-field__lock-toggle").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("true", cut.Find(".tl-editor-field[data-field-key='title']").GetAttribute("data-locked"));
            Assert.True(cut.Find("input[aria-label='Title']").HasAttribute("disabled"));
        });
        Assert.True(editor.Session.Pending.IsEmpty);
    }

    [Fact]
    public async Task UnlockingAFieldAndTyping_StagesTheChange_AndTheFieldIsYours()
    {
        var editor = await StartEditorAsync();
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.UnlockField("description");

        cut.Find(".tl-editor-field[data-field-key='description'] textarea").Input("My own synopsis.");

        cut.WaitForAssertion(() =>
        {
            Assert.True(editor.Session.Pending.TryGetField(MovieId, "description", out var staged));
            Assert.Equal("My own synopsis.", staged);
            var changed = cut.Find(".tl-editor-field[data-field-key='description']");
            Assert.Equal("true", changed.GetAttribute("data-changed"));
            Assert.Equal("false", changed.GetAttribute("data-locked"));
            Assert.Equal("yours", cut.LockStateOf("description"));
            Assert.Contains("kept on refresh", changed.TextContent, StringComparison.Ordinal);
            Assert.NotNull(changed.QuerySelector("button.tl-editor-field__reset"));
            Assert.Empty(changed.QuerySelectorAll("button.tl-editor-field__lock-toggle"));
        });
        Assert.Empty(_overrideSaves);
    }

    [Fact]
    public async Task ReLockingAnUnlockedFieldYouDidNotChange_JustRelocksIt()
    {
        var editor = await StartEditorAsync();
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.UnlockField("title");
        // Typing the value it already has takes the staged change back out, so nothing is left waiting.
        cut.Find("input[aria-label='Title']").Input("Dune 2");
        cut.WaitForAssertion(() => Assert.Equal(1, editor.Session.Pending.Count));
        cut.Find("input[aria-label='Title']").Input("Dune: Part One");
        cut.WaitForAssertion(() => Assert.True(editor.Session.Pending.IsEmpty));
        Assert.Equal("unlocked", cut.LockStateOf("title"));

        cut.Find(".tl-editor-field[data-field-key='title'] button.tl-editor-field__lock-toggle").Click();

        cut.WaitForAssertion(() => Assert.Equal("locked", cut.LockStateOf("title")));
        Assert.True(editor.Session.Pending.IsEmpty);
    }

    [Fact]
    public async Task TagsAreTheLibrarysOwn_AndNeverShowALock()
    {
        var editor = await StartEditorAsync();
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));

        var tags = cut.WaitForElement(".tl-editor-field[data-field-key='custom_tags']");

        Assert.Equal("false", tags.GetAttribute("data-locked"));
        Assert.Empty(tags.QuerySelectorAll("button.tl-editor-field__lock-toggle"));
        Assert.Empty(tags.QuerySelectorAll("[data-lock-state]"));
        Assert.NotNull(tags.QuerySelector(".sme-tag-editor__add"));
        Assert.False(tags.QuerySelector(".sme-tag-editor__add")!.HasAttribute("disabled"));
    }

    [Fact]
    public async Task AChangedField_ShowsRevert_WhichPutsTheSavedValueBack_AndLocksItAgain()
    {
        var editor = await StartEditorAsync();
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.UnlockField("title");
        Assert.Empty(cut.Find(".tl-editor-field[data-field-key='title']").QuerySelectorAll("button.tl-editor-field__reset"));

        cut.Find("input[aria-label='Title']").Input("Dune 2");

        cut.WaitForAssertion(() =>
        {
            var changed = cut.Find(".tl-editor-field[data-field-key='title']");
            Assert.Equal("true", changed.GetAttribute("data-changed"));
            Assert.Equal("false", changed.GetAttribute("data-locked"));
            Assert.Equal("Revert", changed.QuerySelector("button.tl-editor-field__reset")!.TextContent.Trim());
        });

        cut.Find(".tl-editor-field[data-field-key='title'] button.tl-editor-field__reset").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.False(editor.Session.Pending.TryGetField(MovieId, "title", out _));
            var back = cut.Find(".tl-editor-field[data-field-key='title']");
            Assert.Equal("false", back.GetAttribute("data-changed"));
            Assert.Equal("true", back.GetAttribute("data-locked"));
            Assert.Equal("Dune: Part One", cut.Find("input[aria-label='Title']").GetAttribute("value"));
        });
    }

    [Fact]
    public async Task ASavedOverride_OpensAsYours_AndRevertSendsItBackToTheProvidersValueAndLocksIt()
    {
        var editor = await StartEditorAsync(MovieWithTitleOverride());
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        var title = cut.WaitForElement(".tl-editor-field[data-field-key='title']");

        Assert.Equal("false", title.GetAttribute("data-locked"));
        Assert.Equal("true", title.GetAttribute("data-changed"));
        Assert.Equal("yours", cut.LockStateOf("title"));

        title.QuerySelector("button.tl-editor-field__reset")!.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(editor.Session.Pending.TryGetField(MovieId, "title", out var staged));
            Assert.True(string.IsNullOrEmpty(staged));
            var relocked = cut.Find(".tl-editor-field[data-field-key='title']");
            Assert.Equal("true", relocked.GetAttribute("data-locked"));
            Assert.Equal("locked", cut.LockStateOf("title"));
        });
    }

    [Fact]
    public async Task ReviewChanges_NamesAChangedProviderField_AsNowYours_ButNotAnOwnField()
    {
        var editor = await StartEditorAsync();
        var cut = RenderBar(editor);
        cut.UnlockField("description");
        cut.Find(".tl-editor-field[data-field-key='description'] textarea").Input("My own synopsis.");
        cut.WaitForAssertion(() => Assert.Equal(1, editor.Session.Pending.Count));
        editor.Session.Pending.SetField(MovieId, "custom_tags", "epic; sandworms");

        cut.Find("button.tl-editor-window__save").Click();

        cut.WaitForElement(".review-changes");
        var rows = cut.FindAll(".review-changes__row");
        var description = Assert.Single(rows, row => row.GetAttribute("data-row")!.EndsWith(":description", StringComparison.Ordinal));
        var tags = Assert.Single(rows, row => row.GetAttribute("data-row")!.EndsWith(":custom_tags", StringComparison.Ordinal));
        Assert.NotNull(description.QuerySelector("[data-now-yours]"));
        Assert.Contains("now yours", description.QuerySelector(".review-changes__what")!.TextContent, StringComparison.Ordinal);
        Assert.Null(tags.QuerySelector("[data-now-yours]"));
    }

    [Fact]
    public async Task AReadOnlyItem_OffersNoUnlock()
    {
        var context = EditingModeTestData.MovieContext();
        context.Scopes[0].CanEditFields = false;
        var editor = await StartEditorAsync(context);
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.WaitForElement(".tl-editor-details");

        Assert.Empty(cut.FindAll("button.tl-editor-field__lock-toggle"));
    }

    // -- Quick Facts (universal editor v4, WP4) ----------------------------------------------------

    [Fact]
    public async Task AFilm_ShowsRuntimeYearSizeAndQuality_BesideTheForm()
    {
        _quickFacts = new ItemQuickFactsDto
        {
            Id = MovieId,
            FileCount = 2,
            SizeBytes = 62_500_000_000,
            RuntimeSeconds = 9300,
            BestQuality = "4K HDR",
        };
        var editor = await StartEditorAsync();
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));

        var facts = cut.WaitForElement(".tl-quick-facts");

        Assert.Equal("film", facts.GetAttribute("data-quick-facts-kind"));
        Assert.Equal(["runtime", "year", "size", "quality"], facts.QuerySelectorAll("[data-fact]").Select(row => row.GetAttribute("data-fact")!).ToArray());
        Assert.Equal("2021", facts.QuerySelector("[data-fact='year'] strong")!.TextContent);
        Assert.Equal("58.2 GB", facts.QuerySelector("[data-fact='size'] strong")!.TextContent);
        Assert.Equal("4K HDR", facts.QuerySelector("[data-fact='quality'] strong")!.TextContent);
        Assert.Empty(facts.QuerySelectorAll("input, textarea, button"));
    }

    [Fact]
    public async Task AReadOnlyLibrary_IsNamedInQuickFacts()
    {
        _quickFacts = new ItemQuickFactsDto { Id = MovieId, FileCount = 1, SizeBytes = 1_000_000_000, ReadOnlyLibrary = true };
        var editor = await StartEditorAsync();
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));

        var facts = cut.WaitForElement(".tl-quick-facts");

        Assert.Equal("Read-only library", facts.QuerySelector("[data-note]")!.TextContent.Trim());
    }

    [Fact]
    public async Task TheDescriptionShowsACharacterCount()
    {
        var editor = await StartEditorAsync();
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));

        var count = cut.WaitForElement(".tl-editor-field[data-field-key='description'] .tl-editor-field__count");

        Assert.EndsWith("characters", count.TextContent.Trim());
    }

    // -- Save changes ----------------------------------------------------------------------------

    [Fact]
    public async Task Save_ChecksFirstThenSaves_RefreshesThePage_AndClosesTheEditor()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        var cut = RenderBar(editor);

        SaveFromReview(cut);

        cut.WaitForAssertion(() => Assert.False(editor.Mode.IsOn));
        Assert.Equal([true, true, false], _sessionCalls);
        Assert.Equal(1, _refreshes);
        Assert.True(editor.Session.Pending.IsEmpty);
        Assert.False(editor.Session.IsActive);
        Assert.Equal(1, editor.Flow.LastSave?.Count);
    }

    [Fact]
    public async Task AfterASave_TheRibbonSaysHowManyChangesWereSaved_AndDismissClearsIt()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        var bar = RenderBar(editor);
        var ribbon = Render<EditSavedRibbon>(parameters => parameters.Add(component => component.Flow, editor.Flow));
        Assert.Empty(ribbon.FindAll(".tl-saved-ribbon"));

        SaveFromReview(bar);

        ribbon.WaitForAssertion(() => Assert.Contains("Saved 1 change at", ribbon.Find(".tl-saved-ribbon").TextContent, StringComparison.Ordinal));
        ribbon.Find(".tl-saved-ribbon button").Click();
        ribbon.WaitForAssertion(() => Assert.Empty(ribbon.FindAll(".tl-saved-ribbon")));
        Assert.Null(editor.Flow.LastSave);
    }

    [Fact]
    public async Task AfterASave_TheRibbonNamesTheProviderWhoseDetailsArriveForTheSavedItem_AndIgnoresOtherItems()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        var bar = RenderBar(editor);
        var ribbon = Render<EditSavedRibbon>(parameters => parameters.Add(component => component.Flow, editor.Flow));
        var orchestrator = Services.GetRequiredService<UIOrchestratorService>();
        SaveFromReview(bar);
        ribbon.WaitForElement(".tl-saved-ribbon");

        await ribbon.InvokeAsync(() => orchestrator.RaiseHydrationStageCompleted(new HydrationStageCompletedEvent(Guid.NewGuid(), 1, 5, "Other")));
        await ribbon.InvokeAsync(() => orchestrator.RaiseHydrationStageCompleted(new HydrationStageCompletedEvent(MovieId, 1, 5, "TMDB")));

        ribbon.WaitForAssertion(() => Assert.Contains("Updated from TMDB.", ribbon.Find(".tl-saved-ribbon").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("Other", ribbon.Find(".tl-saved-ribbon").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_WhenTheCheckRefusesAChange_WritesNothing_AndShowsEachReasonBesideItsField()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        editor.Session.Pending.SetField(MovieId, "rating", "11");
        var changeId = editor.Session.Pending.Changes.Single().Id;
        _answer = request => new EditSessionResult
        {
            PreviewOnly = true,
            Valid = false,
            Changes = [new EditSessionChangeResult { Id = changeId, Kind = EditSessionChangeKinds.Details, EntityId = MovieId, Status = EditSessionChangeStatuses.Invalid, Reason = "This item is no longer in your library." }],
        };
        var cut = RenderBar(editor);

        // The check runs when Review changes opens and the window shows the reason on its row; Save stays off.
        cut.Find("button.tl-editor-window__save").Click();

        cut.WaitForAssertion(() => Assert.Contains("This item is no longer in your library.", cut.Find(".review-changes").TextContent));
        Assert.True(cut.Find(".review-changes__footer button:last-child").HasAttribute("disabled"));
        Assert.Equal([true], _sessionCalls);
        Assert.True(editor.Mode.IsOn);
        Assert.Equal(2, editor.Session.Pending.ChangeCount);
        Assert.Equal(0, _refreshes);
    }

    [Fact]
    public async Task Save_WhenTheEngineCannotBeReached_SaysSoAndKeepsEverything()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        var unreachable = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), _ =>
            Task.FromResult(LibraryBatchEditOutcome<EditSessionResult>.Failed(LibraryBatchEditOutcomeStatus.Unavailable))));
        var flow = new EditSessionFlow(editor.Session, editor.Mode, new EditSessionSaver(unreachable), () => [new EditTrailItem(MovieId, "Dune", "Movie")], () => Task.FromResult(false));

        Assert.False(await flow.SaveAsync());

        Assert.Contains("could not be reached", flow.Message);
        Assert.Equal(1, editor.Session.Pending.Count);
        Assert.True(editor.Mode.IsOn);
    }
}
