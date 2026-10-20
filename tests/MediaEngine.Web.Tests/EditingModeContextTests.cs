using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>
/// How Editing Mode begins, opens one section at a time and ends (details overlay spec D7, D9): Done, Esc and Back settle
/// unsaved input with "Save or discard?", Esc closes a field before a section before the overlay, and nothing typed is ever
/// dropped without an answer. The question and the section's editor are hand-written spies.
/// </summary>
public sealed class EditingModeContextTests
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<Dictionary<string, string>> _saves = [];
    private bool _saveResult = true;

    private IEngineApiClient Api() => EngineApiClientStub.Create(stub =>
    {
        stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
        stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
        stub.SetHandler(nameof(IEngineApiClient.SaveItemDisplayOverridesAsync), args =>
        {
            _saves.Add((Dictionary<string, string>)args![1]!);
            return Task.FromResult(_saveResult);
        });
        stub.SetHandler("get_LastError", _ => "HTTP 500: could not write");
    });

    private async Task<(EditingModeContext Mode, AskSpy Ask)> StartedAsync(params SaveOrDiscardChoice[] answers)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var session = new ItemFieldEditSession(Api(), _device);
        var ask = new AskSpy(answers);
        var mode = new EditingModeContext(_device, session)
        {
            HasPermission = true,
            AskSaveOrDiscard = ask.AskAsync,
        };
        mode.Loader = () => session.LoadAsync(MovieId, "Movies", "movie", null);
        Assert.True(await mode.EnterAsync());
        return (mode, ask);
    }

    private static void TypeIntoTheTitle(EditingModeContext mode, string value)
    {
        Assert.True(mode.Fields.Begin(mode.Fields.Find("title")!, "hero"));
        mode.Fields.UpdateDraft(value);
    }

    // -- Done ----------------------------------------------------------------------------------

    [Fact]
    public async Task Done_WithNothingUnsaved_LeavesEditingModeWithoutAsking()
    {
        var (mode, ask) = await StartedAsync();

        Assert.True(await mode.DoneAsync());

        Assert.False(mode.IsOn);
        Assert.Equal(0, ask.Asked);
    }

    [Fact]
    public async Task Done_WithAFieldOpenButNotChanged_LeavesWithoutAsking()
    {
        var (mode, ask) = await StartedAsync();
        Assert.True(mode.Fields.Begin(mode.Fields.Find("title")!, "hero"));

        Assert.True(await mode.DoneAsync());

        Assert.False(mode.IsOn);
        Assert.Equal(0, ask.Asked);
    }

    [Fact]
    public async Task Done_WithUnsavedFieldInput_Save_SavesTheFieldAndThenLeaves()
    {
        var (mode, ask) = await StartedAsync(SaveOrDiscardChoice.Save);
        TypeIntoTheTitle(mode, "Saved on the way out");

        Assert.True(await mode.DoneAsync());

        Assert.Equal(1, ask.Asked);
        Assert.Equal("Saved on the way out", Assert.Single(_saves)["title"]);
        Assert.False(mode.IsOn);
    }

    [Fact]
    public async Task Done_WithUnsavedFieldInput_Discard_DropsTheInputAndLeaves()
    {
        var (mode, _) = await StartedAsync(SaveOrDiscardChoice.Discard);
        TypeIntoTheTitle(mode, "Typed but not saved");

        Assert.True(await mode.DoneAsync());

        Assert.Empty(_saves);
        Assert.False(mode.IsOn);
        Assert.False(mode.Fields.HasOpenDraft);
    }

    [Fact]
    public async Task Done_WithUnsavedFieldInput_Cancel_StaysInEditingModeWithTheInputIntact()
    {
        var (mode, _) = await StartedAsync(SaveOrDiscardChoice.Cancel);
        TypeIntoTheTitle(mode, "Typed but not saved");

        Assert.False(await mode.DoneAsync());

        Assert.True(mode.IsOn);
        Assert.Equal("Typed but not saved", mode.Fields.Draft);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task Done_WhenTheSaveFails_StaysInEditingModeWithTheFieldOpenAndItsError()
    {
        _saveResult = false;
        var (mode, _) = await StartedAsync(SaveOrDiscardChoice.Save);
        TypeIntoTheTitle(mode, "Typed and kept");

        Assert.False(await mode.DoneAsync());

        Assert.True(mode.IsOn);
        Assert.True(mode.Fields.HasOpenDraft);
        Assert.Equal("Typed and kept", mode.Fields.Draft);
        Assert.Contains("could not write", mode.Fields.Error);
    }

    [Fact]
    public async Task Done_WithUnsavedSectionInput_Save_AsksTheSectionToSaveAndThenLeaves()
    {
        var (mode, _) = await StartedAsync(SaveOrDiscardChoice.Save);
        var host = new SectionHostSpy();
        mode.SectionHost = host;
        mode.OpenSection(EditingSection.Artwork);
        mode.SetSectionDirty(true);

        Assert.True(await mode.DoneAsync());

        Assert.Equal(1, host.Saved);
        Assert.False(mode.IsOn);
        Assert.False(mode.SectionOpen);
    }

    [Fact]
    public async Task Done_WithUnsavedSectionInput_Discard_LeavesWithoutSaving()
    {
        var (mode, _) = await StartedAsync(SaveOrDiscardChoice.Discard);
        var host = new SectionHostSpy();
        mode.SectionHost = host;
        mode.OpenSection(EditingSection.Artwork);
        mode.SetSectionDirty(true);

        Assert.True(await mode.DoneAsync());

        Assert.Equal(0, host.Saved);
        Assert.False(mode.IsOn);
        Assert.False(mode.SectionDirty);
    }

    [Fact]
    public async Task Done_WhenTheSectionCannotSave_StaysInEditingModeWithTheSectionOpen()
    {
        var (mode, _) = await StartedAsync(SaveOrDiscardChoice.Save);
        mode.SectionHost = new SectionHostSpy { SaveSucceeds = false };
        mode.OpenSection(EditingSection.Artwork);
        mode.SetSectionDirty(true);

        Assert.False(await mode.DoneAsync());

        Assert.True(mode.IsOn);
        Assert.True(mode.SectionOpen);
        Assert.True(mode.SectionDirty);
    }

    [Fact]
    public async Task Done_WithNoQuestionWired_NeverDropsUnsavedInputSilently()
    {
        var (mode, _) = await StartedAsync();
        mode.AskSaveOrDiscard = null;
        TypeIntoTheTitle(mode, "Typed but not saved");

        Assert.False(await mode.DoneAsync());

        Assert.True(mode.IsOn);
        Assert.Equal("Typed but not saved", mode.Fields.Draft);
    }

    // -- Esc, in order -------------------------------------------------------------------------

    [Fact]
    public async Task Escape_ClosesACleanOpenFieldBeforeAnythingElse_WithoutAsking_AndKeepsTheSectionAndTheMode()
    {
        var (mode, ask) = await StartedAsync();
        mode.OpenSection(EditingSection.History);
        Assert.True(mode.Fields.Begin(mode.Fields.Find("title")!, "hero"));

        // The section was opened first; a field opened beside it is the first thing Esc closes.
        Assert.True(await mode.HandleEscapeAsync());

        Assert.False(mode.Fields.HasOpenDraft);
        Assert.True(mode.SectionOpen);
        Assert.True(mode.IsOn);
        Assert.Equal(0, ask.Asked);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task Escape_FromOutsideAFieldWithTypedInput_AsksSaveOrDiscard_AndCancelKeepsTheInput()
    {
        var (mode, ask) = await StartedAsync(SaveOrDiscardChoice.Cancel);
        mode.OpenSection(EditingSection.History);
        TypeIntoTheTitle(mode, "Typed but not saved");

        Assert.True(await mode.HandleEscapeAsync());

        Assert.Equal(1, ask.Asked);
        Assert.True(mode.Fields.HasOpenDraft);
        Assert.Equal("Typed but not saved", mode.Fields.Draft);
        Assert.True(mode.SectionOpen);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task Escape_FromOutsideAFieldWithTypedInput_Discard_ClosesTheFieldWithoutSavingAndStaysInEditingMode()
    {
        var (mode, _) = await StartedAsync(SaveOrDiscardChoice.Discard);
        TypeIntoTheTitle(mode, "Typed but not saved");

        Assert.True(await mode.HandleEscapeAsync());

        Assert.False(mode.Fields.HasOpenDraft);
        Assert.True(mode.IsOn);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task Escape_FromOutsideAFieldWithTypedInput_Save_SavesTheFieldAndClosesIt()
    {
        var (mode, _) = await StartedAsync(SaveOrDiscardChoice.Save);
        TypeIntoTheTitle(mode, "Saved from the dialog");

        Assert.True(await mode.HandleEscapeAsync());

        Assert.Equal("Saved from the dialog", Assert.Single(_saves)["title"]);
        Assert.False(mode.Fields.HasOpenDraft);
        Assert.True(mode.IsOn);
    }

    [Fact]
    public async Task Escape_FromOutsideAFieldWhoseSaveFails_KeepsTheFieldOpenWithItsErrorAndTheTypedValue()
    {
        _saveResult = false;
        var (mode, _) = await StartedAsync(SaveOrDiscardChoice.Save);
        TypeIntoTheTitle(mode, "Typed and kept");

        Assert.True(await mode.HandleEscapeAsync());

        Assert.True(mode.Fields.HasOpenDraft);
        Assert.Equal("Typed and kept", mode.Fields.Draft);
        Assert.Contains("could not write", mode.Fields.Error);
        Assert.True(mode.IsOn);
    }

    [Fact]
    public async Task EscInsideTheFocusedFieldStillCancelsItAsTheFieldsOwnHandlerDoes()
    {
        var (mode, ask) = await StartedAsync();
        TypeIntoTheTitle(mode, "Typed but not saved");

        // The field's own Esc handler (EditableFieldPencil, EditorDetailsInlineField) calls the session directly, as before.
        mode.Fields.Cancel();

        Assert.False(mode.Fields.HasOpenDraft);
        Assert.Equal(0, ask.Asked);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task Escape_WithAnUnsavedSection_AsksSaveOrDiscard_AndCancelKeepsEverythingOpen()
    {
        var (mode, ask) = await StartedAsync(SaveOrDiscardChoice.Cancel);
        mode.SectionHost = new SectionHostSpy();
        mode.OpenSection(EditingSection.Artwork);
        mode.SetSectionDirty(true);

        Assert.True(await mode.HandleEscapeAsync());

        Assert.Equal(1, ask.Asked);
        Assert.True(mode.SectionOpen);
        Assert.True(mode.SectionDirty);
        Assert.True(mode.IsOn);
    }

    [Fact]
    public async Task Escape_WithAnUnsavedSection_Discard_DropsItsInputButStaysInEditingMode()
    {
        var (mode, _) = await StartedAsync(SaveOrDiscardChoice.Discard);
        var host = new SectionHostSpy();
        mode.SectionHost = host;
        mode.OpenSection(EditingSection.Artwork);
        mode.SetSectionDirty(true);

        Assert.True(await mode.HandleEscapeAsync());

        Assert.Equal(1, host.Discarded);
        Assert.False(mode.SectionOpen);
        Assert.True(mode.IsOn);
    }

    [Fact]
    public async Task Escape_WithACleanSection_ClosesItWithoutAskingAndStaysInEditingMode()
    {
        var (mode, ask) = await StartedAsync();
        mode.OpenSection(EditingSection.History);

        Assert.True(await mode.HandleEscapeAsync());

        Assert.False(mode.SectionOpen);
        Assert.True(mode.IsOn);
        Assert.Equal(0, ask.Asked);
    }

    [Fact]
    public async Task Escape_WithNothingOpen_IsNotUsed_SoTheOverlayGoesOnToDismiss()
    {
        var (mode, _) = await StartedAsync();

        Assert.False(await mode.HandleEscapeAsync());

        Assert.True(mode.IsOn);
    }

    [Fact]
    public async Task Escape_OutsideEditingMode_IsNeverUsed()
    {
        var (mode, _) = await StartedAsync();
        mode.Leave();

        Assert.False(await mode.HandleEscapeAsync());
    }

    // -- Leaving the overlay (close, Esc with nothing open, the backdrop, Back) -----------------

    [Fact]
    public async Task Dismissing_LeavesEditingModeFirst_ThroughTheSameSaveOrDiscardCheck()
    {
        var (mode, _) = await StartedAsync(SaveOrDiscardChoice.Discard);
        TypeIntoTheTitle(mode, "Typed but not saved");

        Assert.True(await mode.CanDismissAsync());

        Assert.False(mode.IsOn);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task Dismissing_IsRefusedWhenTheQuestionIsCancelled_AndEditingGoesOn()
    {
        var (mode, _) = await StartedAsync(SaveOrDiscardChoice.Cancel);
        TypeIntoTheTitle(mode, "Typed but not saved");

        Assert.False(await mode.CanDismissAsync());

        Assert.True(mode.IsOn);
        Assert.Equal("Typed but not saved", mode.Fields.Draft);
    }

    [Fact]
    public async Task Dismissing_WithNothingUnsaved_JustLeavesEditingMode()
    {
        var (mode, ask) = await StartedAsync();

        Assert.True(await mode.CanDismissAsync());

        Assert.False(mode.IsOn);
        Assert.Equal(0, ask.Asked);
    }

    [Fact]
    public async Task Dismissing_WhenNotEditing_IsAllowed()
    {
        var (mode, _) = await StartedAsync();
        mode.Leave();

        Assert.True(await mode.CanDismissAsync());
    }

    // -- One section at a time -----------------------------------------------------------------

    [Fact]
    public async Task OnlyOneSectionIsOpenAtATime_AndOpeningAnotherReplacesIt()
    {
        var (mode, _) = await StartedAsync();

        Assert.True(mode.OpenSection(EditingSection.Artwork));
        Assert.Equal(EditingSection.Artwork, mode.OpenSectionKind);
        Assert.True(await mode.ShowSectionAsync(EditingSection.History));

        Assert.Equal(EditingSection.History, mode.OpenSectionKind);
    }

    [Fact]
    public async Task PressingTheOpenSectionsButtonAgain_ClosesIt()
    {
        var (mode, _) = await StartedAsync();

        Assert.True(await mode.ToggleSectionAsync(EditingSection.Match));
        Assert.Equal(EditingSection.Match, mode.OpenSectionKind);
        Assert.False(await mode.ToggleSectionAsync(EditingSection.Match));

        Assert.False(mode.SectionOpen);
    }

    [Fact]
    public async Task SwappingSections_SettlesTheOpenSectionsUnsavedInputFirst()
    {
        var (mode, ask) = await StartedAsync(SaveOrDiscardChoice.Cancel);
        mode.SectionHost = new SectionHostSpy();
        mode.OpenSection(EditingSection.Artwork);
        mode.SetSectionDirty(true);

        Assert.False(await mode.ShowSectionAsync(EditingSection.History));

        Assert.Equal(1, ask.Asked);
        Assert.Equal(EditingSection.Artwork, mode.OpenSectionKind);
    }

    [Fact]
    public async Task AskingForTheOpenSectionAgain_OnlyReappliesTheArtworkSlot()
    {
        var (mode, ask) = await StartedAsync();
        mode.OpenSection(EditingSection.Artwork, "Background");
        var version = mode.SectionRequestVersion;
        mode.SectionHost = new SectionHostSpy();
        mode.SetSectionDirty(true);

        Assert.True(await mode.ShowSectionAsync(EditingSection.Artwork, "Logo"));

        Assert.Equal("Logo", mode.OpenSectionArtworkSlot);
        Assert.Equal(version + 1, mode.SectionRequestVersion);
        Assert.Equal(0, ask.Asked);
        Assert.True(mode.SectionDirty);
    }

    [Fact]
    public async Task OnlyTheArtworkSectionKeepsAnArtworkSlot()
    {
        var (mode, _) = await StartedAsync();

        mode.OpenSection(EditingSection.History, "Logo");

        Assert.Null(mode.OpenSectionArtworkSlot);
    }

    [Fact]
    public async Task ASectionIsRefusedWhileAFieldIsOpen()
    {
        var (mode, _) = await StartedAsync();
        Assert.True(mode.Fields.Begin(mode.Fields.Find("title")!, "hero"));

        Assert.False(mode.OpenSection(EditingSection.Artwork));
        Assert.False(await mode.ShowSectionAsync(EditingSection.History));

        Assert.False(mode.SectionOpen);
    }

    [Fact]
    public async Task LeavingEditingMode_ClosesTheOpenSection()
    {
        var (mode, _) = await StartedAsync();
        mode.OpenSection(EditingSection.History);

        mode.Leave();

        Assert.False(mode.SectionOpen);
        Assert.False(mode.SectionDirty);
    }

    // -- Only one settling at a time -----------------------------------------------------------

    [Fact]
    public async Task WhileSaveOrDiscardIsBeingAnswered_ADoneAnEscAndABackAreRefusedAndNothingAsksTwice()
    {
        var (mode, _) = await StartedAsync();
        var gate = new TaskCompletionSource<SaveOrDiscardChoice>();
        var asked = 0;
        mode.AskSaveOrDiscard = () =>
        {
            asked++;
            return gate.Task;
        };
        TypeIntoTheTitle(mode, "Typed but not saved");

        var first = mode.DoneAsync();
        Assert.True(mode.IsSettling);

        Assert.False(await mode.DoneAsync());
        Assert.False(await mode.CanDismissAsync());
        Assert.True(await mode.HandleEscapeAsync());
        Assert.Equal(1, asked);
        Assert.True(mode.IsOn);

        gate.SetResult(SaveOrDiscardChoice.Discard);
        Assert.True(await first);

        Assert.False(mode.IsOn);
        Assert.False(mode.IsSettling);
        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task ADiscardCannotRunWhileASaveFromTheDialogIsStillInFlight()
    {
        var (mode, _) = await StartedAsync(SaveOrDiscardChoice.Save);
        var host = new SlowSectionHost();
        mode.SectionHost = host;
        mode.OpenSection(EditingSection.Artwork);
        mode.SetSectionDirty(true);

        var first = mode.DoneAsync();
        await host.SaveStarted.Task;
        mode.AskSaveOrDiscard = () => Task.FromResult(SaveOrDiscardChoice.Discard);

        // A second Done (or X, or Esc) arriving mid-save is refused, so its Discard never runs and Leave() waits for the save.
        Assert.False(await mode.DoneAsync());
        Assert.True(await mode.HandleEscapeAsync());
        Assert.True(mode.IsOn);
        Assert.Equal(0, host.Discarded);

        host.FinishSave.SetResult(true);
        Assert.True(await first);

        Assert.Equal(1, host.Saved);
        Assert.Equal(0, host.Discarded);
        Assert.False(mode.IsOn);
    }

    [Fact]
    public async Task DoneIsRefusedWhileAFieldIsSaving()
    {
        var gate = new TaskCompletionSource<bool>();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
            stub.SetHandler(nameof(IEngineApiClient.SaveItemDisplayOverridesAsync), _ => gate.Task);
        });
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var session = new ItemFieldEditSession(api, _device);
        var ask = new AskSpy([SaveOrDiscardChoice.Discard]);
        var mode = new EditingModeContext(_device, session) { HasPermission = true, AskSaveOrDiscard = ask.AskAsync };
        mode.Loader = () => session.LoadAsync(MovieId, "Movies", "movie", null);
        Assert.True(await mode.EnterAsync());
        TypeIntoTheTitle(mode, "Typed");
        var saving = session.SaveAsync();
        Assert.True(session.Saving);

        Assert.False(await mode.DoneAsync());
        Assert.True(await mode.HandleEscapeAsync());

        Assert.Equal(0, ask.Asked);
        Assert.True(mode.IsOn);
        gate.SetResult(true);
        await saving;
    }

    [Fact]
    public async Task WhileTheOpenSectionsOwnSaveRuns_DoneAndEscAreRefusedAndNeverAskOrSave()
    {
        var (mode, ask) = await StartedAsync(SaveOrDiscardChoice.Save);
        var host = new SectionHostSpy();
        mode.SectionHost = host;
        mode.OpenSection(EditingSection.Artwork);
        mode.SetSectionDirty(true);
        mode.SetSectionSaving(true);

        Assert.False(await mode.DoneAsync());
        Assert.False(await mode.CanDismissAsync());
        Assert.True(await mode.HandleEscapeAsync());

        Assert.Equal(0, ask.Asked);
        Assert.Equal(0, host.Saved);
        Assert.True(mode.SectionOpen);

        mode.SetSectionSaving(false);
        Assert.True(await mode.DoneAsync());
        Assert.Equal(1, host.Saved);
    }

    // -- The mode ends while "Save or discard?" is open (the title was removed) ----------------

    [Theory]
    [InlineData(SaveOrDiscardChoice.Save)]
    [InlineData(SaveOrDiscardChoice.Discard)]
    public async Task Done_WhenEditingEndsWhileTheQuestionIsOpen_DoesNotDismissOrSaveWhateverIsChosen(SaveOrDiscardChoice choice)
    {
        var (mode, _) = await StartedAsync();
        var gate = new TaskCompletionSource<SaveOrDiscardChoice>();
        mode.AskSaveOrDiscard = () => gate.Task;
        var host = new SectionHostSpy();
        mode.SectionHost = host;
        mode.OpenSection(EditingSection.Artwork);
        mode.SetSectionDirty(true);
        TypeIntoTheTitle(mode, "Typed but not saved");

        var done = mode.DoneAsync();
        mode.Leave();
        gate.SetResult(choice);

        Assert.False(await done);
        Assert.False(mode.IsOn);
        Assert.False(mode.IsSettling);
        Assert.Equal(0, host.Saved);
        Assert.Empty(_saves);
    }

    [Fact]
    public async Task CloseSection_WhenEditingEndsWhileTheQuestionIsOpen_DoesNotSaveIntoTheRemovedItem()
    {
        var (mode, _) = await StartedAsync();
        var gate = new TaskCompletionSource<SaveOrDiscardChoice>();
        mode.AskSaveOrDiscard = () => gate.Task;
        var host = new SectionHostSpy();
        mode.SectionHost = host;
        mode.OpenSection(EditingSection.Match);
        mode.SetSectionDirty(true);

        var closing = mode.CloseSectionAsync();
        mode.Leave();
        gate.SetResult(SaveOrDiscardChoice.Save);

        Assert.False(await closing);
        Assert.Equal(0, host.Saved);
        Assert.Equal(0, host.Discarded);
    }

    [Fact]
    public async Task Escape_WhenEditingEndsWhileAFieldQuestionIsOpen_DoesNotSaveTheFieldIntoTheRemovedItem()
    {
        var (mode, _) = await StartedAsync();
        var gate = new TaskCompletionSource<SaveOrDiscardChoice>();
        mode.AskSaveOrDiscard = () => gate.Task;
        TypeIntoTheTitle(mode, "Typed but not saved");

        var escape = mode.HandleEscapeAsync();
        mode.Leave();
        gate.SetResult(SaveOrDiscardChoice.Save);

        Assert.True(await escape);
        Assert.False(mode.IsOn);
        Assert.Empty(_saves);
    }

    // -- Spies ---------------------------------------------------------------------------------

    private sealed class AskSpy(IEnumerable<SaveOrDiscardChoice> answers)
    {
        private readonly Queue<SaveOrDiscardChoice> _answers = new(answers);

        public int Asked { get; private set; }

        public Task<SaveOrDiscardChoice> AskAsync()
        {
            Asked++;
            return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : SaveOrDiscardChoice.Cancel);
        }
    }

    private sealed class SlowSectionHost : IEditingSectionHost
    {
        public TaskCompletionSource SaveStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> FinishSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Saved { get; private set; }

        public int Discarded { get; private set; }

        public async Task<bool> SaveAsync()
        {
            Saved++;
            SaveStarted.TrySetResult();
            return await FinishSave.Task;
        }

        public Task DiscardAsync()
        {
            Discarded++;
            return Task.CompletedTask;
        }
    }

    private sealed class SectionHostSpy : IEditingSectionHost
    {
        public bool SaveSucceeds { get; init; } = true;

        public int Saved { get; private set; }

        public int Discarded { get; private set; }

        public Task<bool> SaveAsync()
        {
            Saved++;
            return Task.FromResult(SaveSucceeds);
        }

        public Task DiscardAsync()
        {
            Discarded++;
            return Task.CompletedTask;
        }
    }
}
