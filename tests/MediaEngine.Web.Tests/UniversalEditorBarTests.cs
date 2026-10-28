using Bunit;
using MediaEngine.Contracts.Details;
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
/// The universal editor's bar and right-hand panel (spec WP4, WP5): what the bar shows before and after something is staged, which
/// tools it offers, the "Unsaved changes" question, the panel closing without ending the edit, the Details tool staging instead of
/// saving, provider facts staying read-only, and Save changes checking before it saves and reporting each change that failed.
/// </summary>
public sealed class UniversalEditorBarTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<bool> _sessionCalls = [];
    private readonly List<string> _overrideSaves = [];
    private Func<EditSessionRequest, EditSessionResult> _answer = request => Ok(request);
    private int _refreshes;

    public UniversalEditorBarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Render<AppPopoverHost>();
    }

    private Task UseDesktopAsync() =>
        _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

    private IEngineApiClient Api(MediaEditorContextDto? context = null)
    {
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(context ?? EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.SaveItemDisplayOverridesAsync), _ =>
            {
                _overrideSaves.Add("saved");
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
        var api = Api(context);
        var fields = new ItemFieldEditSession(api, _device);
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

    private IRenderedComponent<EditingBar> RenderBar(Editor editor, bool canDelete = false) =>
        Render<EditingBar>(parameters => parameters
            .Add(bar => bar.Mode, editor.Mode)
            .Add(bar => bar.Flow, editor.Flow)
            .Add(bar => bar.CanDelete, canDelete));

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

    // -- The bar's states ----------------------------------------------------------------------

    [Fact]
    public async Task WithNothingPending_SaveIsOffAndSaysNoChangesYet()
    {
        var editor = await StartEditorAsync();

        var cut = RenderBar(editor);

        var save = cut.Find("button.tl-editing-bar__save");
        Assert.True(save.HasAttribute("disabled"));
        Assert.Equal("Save changes", save.TextContent.Trim());
        Assert.Equal("No changes yet", cut.Find(".tl-editing-bar__end [data-playback-tooltip]").GetAttribute("data-playback-tooltip"));
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
            var save = cut.Find("button.tl-editing-bar__save");
            Assert.False(save.HasAttribute("disabled"));
            Assert.Equal("Save 3 changes", save.TextContent.Trim());
            Assert.Empty(cut.FindAll(".tl-editing-bar__end [data-playback-tooltip]:not([data-playback-tooltip=''])"));
        });
    }

    [Fact]
    public async Task TheBarOffersDetailsArtworkAndMatchForAMovie_AndTheTrailNamesTheItem()
    {
        var editor = await StartEditorAsync();

        var cut = RenderBar(editor);

        Assert.Equal(["Details", "Artwork", "Match"], cut.FindAll(".tl-editing-bar__tool").Select(tool => tool.TextContent.Trim()));
        Assert.Contains("Dune: Part One", cut.Find(".tl-editing-bar__trail").TextContent);
    }

    [Fact]
    public async Task MatchIsHiddenWhereTheEditorCannotMatch_AndArtworkWhereItCannotChangeArtwork()
    {
        var noMatch = TvContext("group");
        var editor = await StartEditorAsync(noMatch);
        Assert.Equal(["Details", "Artwork"], RenderBar(editor).FindAll(".tl-editing-bar__tool").Select(tool => tool.TextContent.Trim()));

        var noArtwork = EditingModeTestData.MovieContext();
        noArtwork.Scopes[0].CanEditArtwork = false;
        var second = await StartEditorAsync(noArtwork);
        Assert.Equal(["Details", "Match"], RenderBar(second).FindAll(".tl-editing-bar__tool").Select(tool => tool.TextContent.Trim()));
    }

    [Fact]
    public async Task TheBarIsNotDrawnWhileEditingIsOff_OrOnADeviceThatCannotEdit()
    {
        var off = await StartEditorAsync(turnOn: false);
        Assert.Empty(RenderBar(off).FindAll(".tl-editing-bar"));

        var on = await StartEditorAsync();
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "tablet", true));
        Assert.Empty(RenderBar(on).FindAll(".tl-editing-bar"));
    }

    [Fact]
    public async Task TheDotsMenuKeepsDeleteFromLibraryOnly_WhenTheAdministratorMayDelete()
    {
        var editor = await StartEditorAsync();

        Assert.Empty(RenderBar(editor).FindAll("button.tl-editing-bar__more"));
        Assert.Single(RenderBar(editor, canDelete: true).FindAll("button.tl-editing-bar__more"));
    }

    // -- Cancel and "Unsaved changes" ------------------------------------------------------------

    [Fact]
    public async Task CancelWithNothingPending_LeavesEditModeWithoutAsking()
    {
        var editor = await StartEditorAsync();
        var cut = RenderBar(editor);

        cut.Find("button.tl-editing-bar__cancel").Click();

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

        cut.Find("button.tl-editing-bar__cancel").Click();

        cut.WaitForAssertion(() => Assert.Single(editor.Asked));
        Assert.True(editor.Mode.IsOn);
        Assert.Equal(1, editor.Session.Pending.Count);
    }

    [Fact]
    public async Task CancelWithPendingChanges_DiscardingThemLeavesEditModeWithAnEmptyList()
    {
        var editor = await StartEditorAsync(discardWhenAsked: true);
        editor.Session.Pending.SetField(MovieId, "title", "Dune");
        var cut = RenderBar(editor);

        cut.Find("button.tl-editing-bar__cancel").Click();

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
    public async Task ThePanelIsClosedUntilAToolIsChosen_AndDetailsIsNarrowerThanMatch()
    {
        var editor = await StartEditorAsync();
        var cut = RenderPanel(editor);
        Assert.Empty(cut.FindAll(".tl-editor-panel"));

        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-editor-panel.tl-editor-panel--narrow")));
        Assert.Equal("Details", cut.Find(".tl-editor-panel__title").TextContent.Trim());

        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Match));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-editor-panel.tl-editor-panel--wide")));
    }

    [Fact]
    public async Task ClosingThePanel_KeepsEditModeAndThePendingChanges_WithoutAsking()
    {
        var editor = await StartEditorAsync();
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".tl-editor-details")));
        editor.Session.Pending.SetField(MovieId, "title", "Dune");

        cut.Find("button.tl-editor-panel__close").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-editor-panel")));
        Assert.True(editor.Mode.IsOn);
        Assert.True(editor.Session.IsActive);
        Assert.Equal(1, editor.Session.Pending.Count);
        Assert.Empty(editor.Asked);
    }

    [Fact]
    public async Task EscClosesTheOpenPanelFirst_ThenAsksOnlyWhenChangesArePending()
    {
        var editor = await StartEditorAsync(discardWhenAsked: false);
        await editor.Flow.SelectToolAsync(EditTool.Details);
        editor.Session.Pending.SetField(MovieId, "title", "Dune");

        Assert.True(await editor.Flow.HandleEscapeAsync());
        Assert.Null(editor.Session.ActiveTool);
        Assert.Empty(editor.Asked);

        Assert.True(await editor.Flow.HandleEscapeAsync());
        Assert.Single(editor.Asked);
        Assert.True(editor.Mode.IsOn);
    }

    [Fact]
    public async Task EscWithNothingPendingAndNoPanel_IsNotUsed_SoTheOverlayCanClose()
    {
        var editor = await StartEditorAsync();

        Assert.False(await editor.Flow.HandleEscapeAsync());
    }

    // -- The Details tool ------------------------------------------------------------------------

    [Fact]
    public async Task TypingInADetailsField_StagesTheChange_AndWritesNothingUntilSave()
    {
        var editor = await StartEditorAsync();
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));

        cut.WaitForElement("input[aria-label='Title']").Input("Dune 2");

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
        var title = cut.WaitForElement("input[aria-label='Title']");

        title.Input("Dune 2");
        cut.WaitForAssertion(() => Assert.Equal(1, editor.Session.Pending.Count));
        cut.Find("input[aria-label='Title']").Input("Dune: Part One");

        cut.WaitForAssertion(() => Assert.True(editor.Session.Pending.IsEmpty));
    }

    [Fact]
    public async Task ProviderFacts_AreReadOnlyWithALockAndTheProvidersName()
    {
        var context = EditingModeTestData.MovieContext();
        context.Scopes[0].FieldSnapshot.AllowedOverrideKeys = ["title", "description"];
        var editor = await StartEditorAsync(context);
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        cut.WaitForElement(".tl-editor-details");

        var rating = cut.Find(".tl-editor-field--locked[data-field-key='rating']");

        Assert.Empty(rating.QuerySelectorAll("input, textarea"));
        Assert.Contains("8.1", rating.TextContent);
        Assert.NotNull(rating.QuerySelector("[aria-label*='locked']"));
        Assert.Contains("TMDB", rating.TextContent);
        Assert.Empty(cut.FindAll(".tl-editor-field--locked input"));
        Assert.Single(cut.FindAll("input[aria-label='Title']"));
        Assert.Empty(cut.FindAll(".tl-editor-field[data-field-key='rating']:not(.tl-editor-field--locked)"));
    }

    [Fact]
    public async Task AnOverriddenField_ShowsAPurpleDot_AndResetStagesAnEmptyValue()
    {
        var editor = await StartEditorAsync(MovieWithTitleOverride());
        var cut = RenderPanel(editor);
        await cut.InvokeAsync(() => editor.Flow.SelectToolAsync(EditTool.Details));
        var title = cut.WaitForElement(".tl-editor-field[data-field-key='title']");
        Assert.NotNull(title.QuerySelector(".tl-editor-field__dot"));

        title.QuerySelectorAll("button").Single(button => button.TextContent.Contains("Reset to provider value")).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.True(editor.Session.Pending.TryGetField(MovieId, "title", out var staged));
            Assert.True(string.IsNullOrEmpty(staged));
            Assert.Contains("provider's value", cut.Find(".tl-editor-field[data-field-key='title'] .tl-editor-field__note").TextContent);
        });
        Assert.Empty(_overrideSaves);
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
    public async Task Save_ChecksFirstThenSaves_RefreshesThePage_AndLeavesEditMode()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        var cut = RenderBar(editor);

        cut.Find("button.tl-editing-bar__save").Click();

        cut.WaitForAssertion(() => Assert.False(editor.Mode.IsOn));
        Assert.Equal([true, false], _sessionCalls);
        Assert.Equal(1, _refreshes);
        Assert.True(editor.Session.Pending.IsEmpty);
        Assert.False(editor.Session.IsActive);
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

        cut.Find("button.tl-editing-bar__save").Click();

        cut.WaitForAssertion(() =>
        {
            var notice = cut.Find(".tl-editing-bar__notice");
            Assert.Contains("Some changes could not be saved", notice.TextContent);
            Assert.Contains("This item is no longer in your library.", notice.TextContent);
            Assert.Contains("Title, Rating", notice.TextContent);
        });
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
