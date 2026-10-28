using Bunit;
using MediaEngine.Contracts.Editing;
using MediaEngine.Contracts.Matching;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.WriteBack;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Review changes (universal editor spec D12, WP8): Save changes opens it instead of saving, it groups the pending list, lets a change be
/// sent back to its tool or removed, blocks Save while the Engine refuses a change, saves on its own button, and keeps showing a row-by-row
/// result when a save only partly went through.
/// </summary>
public sealed class ReviewChangesDialogTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;
    private static readonly Guid EpisodeId = Guid.Parse("11111111-aaaa-bbbb-cccc-000000000002");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly List<bool> _sessionCalls = [];
    private readonly IRenderedComponent<AppPopoverHost> _popovers;
    private readonly IRenderedComponent<AppDialogHost> _dialogs;
    private Func<EditSessionRequest, EditSessionResult> _answer = request => Answer(request);
    private bool? _writeBack = true;
    private int _refreshes;

    public ReviewChangesDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(Api());
        _popovers = Render<AppPopoverHost>();
        _dialogs = Render<AppDialogHost>();
    }

    private IEngineApiClient Api() => EngineApiClientStub.Create(stub =>
    {
        stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
        stub.SetHandler(nameof(IEngineApiClient.GetItemEditorSuggestionsAsync), _ => Task.FromResult<IReadOnlyList<string>>([]));
        stub.SetHandler(nameof(IEngineApiClient.GetFileWriteScheduleAsync), _ =>
            Task.FromResult(_writeBack is { } enabled ? new FileWriteScheduleDto { Enabled = enabled } : null));
        stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), args =>
        {
            var request = (EditSessionRequest)args![0]!;
            _sessionCalls.Add(request.PreviewOnly);
            return Task.FromResult(LibraryBatchEditOutcome<EditSessionResult>.Ok(_answer(request)));
        });
    });

    private static EditSessionResult Answer(EditSessionRequest request, Func<EditSessionChange, (string Status, string? Reason)>? statusOf = null)
    {
        var results = request.Changes.Select(change =>
        {
            var (status, reason) = statusOf?.Invoke(change) ?? (EditSessionChangeStatuses.Ok, null);
            return new EditSessionChangeResult { Id = change.Id, Kind = change.Kind, EntityId = change.EntityId, Status = status, Reason = reason };
        }).ToList();
        var valid = results.All(result => result.Status == EditSessionChangeStatuses.Ok);
        return new EditSessionResult
        {
            PreviewOnly = request.PreviewOnly,
            Valid = valid,
            DataSaved = !request.PreviewOnly && results.Where(result => result.Kind != EditSessionChangeKinds.FileMove).All(result => result.Status == EditSessionChangeStatuses.Ok),
            Changes = results,
        };
    }

    private sealed record Editor(EditingModeContext Mode, EditSessionState Session, EditSessionFlow Flow, List<string> Asked);

    private async Task<Editor> StartEditorAsync(bool discardWhenAsked = false)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var api = Services.GetRequiredService<IEngineApiClient>();
        var fields = new ItemFieldEditSession(api, _device);
        var mode = new EditingModeContext(_device, fields) { HasPermission = true };
        mode.Loader = () => fields.LoadAsync(MovieId, "Movies", "movie", null);
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
                builder.AddComponentParameter(3, nameof(EditorPanelHost.ChildContent), (RenderFragment<EditTool>)(_ => _ => { }));
                builder.CloseComponent();
            }));

    private static void StageEverything(PendingEditList pending)
    {
        // Staged out of order on purpose: Review changes shows them in its own order.
        pending.SetTarget(EditSessionChangeKinds.FileMove, MovieId, null);
        pending.SetTarget(EditSessionChangeKinds.Relationship, MovieId, Guid.NewGuid().ToString("D"), label: "Dune Collection");
        pending.SetArtwork(MovieId, "CoverArt", "asset-1");
        pending.SetTarget(
            EditSessionChangeKinds.Identity,
            MovieId,
            EditSessionIdentityTarget.Serialize(new ReplaceRetailMatchRequestDto { ProviderName = "tmdb", ProviderItemId = "438631" }),
            label: "Dune (tmdb)");
        pending.SetField(MovieId, "title", "Dune 2");
    }

    private IRenderedComponent<EditingBar> OpenReview(Editor editor)
    {
        var bar = RenderBar(editor);
        bar.Find("button.tl-editing-bar__save").Click();
        _dialogs.WaitForElement(".review-changes");
        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".review-changes__state")));
        return bar;
    }

    private AngleSharp.Dom.IElement FooterButton(string label) =>
        _dialogs.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == label);

    private AngleSharp.Dom.IElement SaveButton => _dialogs.Find(".app-dialog-shell__footer button:last-child");

    private AngleSharp.Dom.IElement Row(string key) => _dialogs.Find($".review-changes__row[data-row='{key}']");

    // -- Opening and grouping ---------------------------------------------------------------------

    [Fact]
    public async Task SaveChanges_OpensReviewInsteadOfSaving_AndChecksTheListWithoutSavingIt()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");

        OpenReview(editor);

        Assert.Equal([true], _sessionCalls);
        Assert.True(editor.Mode.IsOn);
        Assert.Equal(0, _refreshes);
        Assert.Contains("Provider records are never modified.", _dialogs.Find(".review-changes").TextContent);
    }

    [Fact]
    public async Task Review_GroupsChangesAsMetadataIdentityArtworkRelationshipsAndPhysicalFiles()
    {
        var editor = await StartEditorAsync();
        StageEverything(editor.Session.Pending);

        OpenReview(editor);

        Assert.Equal(
            ["Metadata", "Identity", "Artwork", "Relationships", "Physical files"],
            _dialogs.FindAll(".review-changes__group").Select(group => group.GetAttribute("aria-label")));
        Assert.Equal(5, _dialogs.FindAll(".review-changes__row").Count);
        Assert.Equal("Save 5 changes", SaveButton.TextContent.Trim());
    }

    [Fact]
    public async Task EachRowNamesTheItem_WithItsBeforeAndAfterAndTheTitlesOfTargetsWhereKnown()
    {
        var editor = await StartEditorAsync();
        StageEverything(editor.Session.Pending);

        OpenReview(editor);

        var title = Row($"{editor.Session.Pending.Changes.Single(c => c.Kind == EditSessionChangeKinds.Details).Id}:title");
        Assert.Contains("Dune: Part One", title.TextContent);
        Assert.Contains("Before", title.TextContent);
        Assert.Contains("Dune: Part One", title.QuerySelector(".review-changes__before")!.TextContent);
        Assert.Contains("Dune 2", title.QuerySelector(".review-changes__after")!.TextContent);
        Assert.Contains("Dune (tmdb)", _dialogs.Find("[aria-label='Identity']").TextContent);
        Assert.Contains("Dune Collection", _dialogs.Find("[aria-label='Relationships']").TextContent);
        Assert.Contains("Cover art", _dialogs.Find("[aria-label='Artwork']").TextContent);
    }

    [Fact]
    public async Task ADetailsChangeWithSeveralFields_BecomesOneRowPerField()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        editor.Session.Pending.SetField(MovieId, "tagline", "Fear is the mind-killer");

        OpenReview(editor);

        Assert.Equal(2, _dialogs.FindAll(".review-changes__row").Count);
        Assert.Equal("Save 2 changes", SaveButton.TextContent.Trim());
    }

    [Fact]
    public async Task AFileMove_CarriesAnAmberWarning()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetTarget(EditSessionChangeKinds.FileMove, MovieId, null);

        OpenReview(editor);

        var warning = _dialogs.Find("[aria-label='Physical files'] .review-changes__warning");
        Assert.Contains("cannot be undone", warning.TextContent);
        Assert.Contains("tl-alert-warning", warning.ClassName + " " + warning.OuterHtml);
    }

    [Fact]
    public async Task TheFilesLine_ReadsTheWriteBackSetting_AndIsLeftOutWhenItCannotBeRead()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        _writeBack = false;

        OpenReview(editor);

        _dialogs.WaitForAssertion(() => Assert.Equal("Files will be updated: Off", _dialogs.Find(".review-changes__files").TextContent.Trim()));
    }

    [Fact]
    public async Task TheFilesLine_IsNotDrawn_WhenTheSettingIsUnknown()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        _writeBack = null;

        OpenReview(editor);

        Assert.Empty(_dialogs.FindAll(".review-changes__files"));
    }

    // -- Remove, Edit, Back -----------------------------------------------------------------------

    [Fact]
    public async Task RemovingAChangeFromItsMenu_TakesItOutOfThePendingList_AndTheCountDrops()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        editor.Session.Pending.SetTarget(EditSessionChangeKinds.Relationship, MovieId, Guid.NewGuid().ToString("D"), label: "Dune Collection");
        OpenReview(editor);
        Assert.Equal("Save 2 changes", SaveButton.TextContent.Trim());

        var relationship = _dialogs.Find("[aria-label='Relationships'] .app-row-menu__trigger");
        relationship.Click();
        var remove = FindMenuItem("Remove this change");
        remove.Click();

        _dialogs.WaitForAssertion(() => Assert.Equal("Save 1 change", SaveButton.TextContent.Trim()));
        Assert.Empty(_dialogs.FindAll("[aria-label='Relationships']"));
        Assert.Equal(EditSessionChangeKinds.Details, editor.Session.Pending.Changes.Single().Kind);
        Assert.True(editor.Mode.IsOn);
    }

    [Fact]
    public async Task RemovingTheLastChange_ShowsPlainTextAndTurnsSaveOff()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        OpenReview(editor);

        _dialogs.Find(".review-changes__row .app-row-menu__trigger").Click();
        FindMenuItem("Remove this change").Click();

        _dialogs.WaitForAssertion(() => Assert.Contains("There are no changes to review.", _dialogs.Find(".review-changes").TextContent));
        Assert.True(SaveButton.HasAttribute("disabled"));
    }

    [Fact]
    public async Task Edit_ClosesTheDialog_AndOpensTheToolThatHoldsTheChange_KeepingEverythingPending()
    {
        var editor = await StartEditorAsync();
        var panel = RenderPanel(editor);
        editor.Session.Pending.SetArtwork(MovieId, "CoverArt", "asset-1");
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        OpenReview(editor);

        _dialogs.Find("[aria-label='Artwork'] .review-changes__actions button").Click();

        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".review-changes")));
        Assert.Equal(EditTool.Artwork, editor.Session.ActiveTool);
        Assert.Equal(2, editor.Session.Pending.Count);
        Assert.True(editor.Mode.IsOn);
        Assert.NotNull(panel);
    }

    [Fact]
    public async Task EditOnAnotherItemsChange_SwitchesToThatItemFirst()
    {
        var editor = await StartEditorAsync();
        var panel = RenderPanel(editor);
        editor.Session.SwitchItem([new EditTrailItem(MovieId, "Dune: Part One", "Movie"), new EditTrailItem(EpisodeId, "Chani", "Episode")]);
        editor.Session.SwitchItem([new EditTrailItem(MovieId, "Dune: Part One", "Movie")]);
        editor.Session.Pending.SetField(EpisodeId, "title", "Chani, again");
        OpenReview(editor);
        Assert.Contains("Chani", _dialogs.Find("[aria-label='Metadata']").TextContent);

        _dialogs.Find("[aria-label='Metadata'] .review-changes__actions button").Click();

        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".review-changes")));
        Assert.Equal(EpisodeId, editor.Session.ActiveItem!.EntityId);
        Assert.Equal(EditTool.Details, editor.Session.ActiveTool);
        Assert.Equal(1, editor.Session.Pending.Count);
        Assert.NotNull(panel);
    }

    [Fact]
    public async Task EditIsOffered_ForEveryStagedItem_EvenOneThePageNeverOpened_AndEveryRowCanBeRemoved()
    {
        var editor = await StartEditorAsync();
        var neverOpened = Guid.NewGuid();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        editor.Session.Pending.SetField(neverOpened, "title", "An episode ticked in a list");
        OpenReview(editor);

        var rows = _dialogs.FindAll("[aria-label='Metadata'] .review-changes__row");
        Assert.Equal(2, rows.Count);
        var edits = rows.Select(row => row.QuerySelectorAll(".review-changes__actions button")
            .Count(button => button.TextContent.Trim() == "Edit")).ToList();
        Assert.Equal(2, edits.Sum());
        Assert.All(rows, row => Assert.NotNull(row.QuerySelector(".review-changes__actions")));
        Assert.True(editor.Flow.CanJumpTo(EditReviewRows.Build(editor.Session.Pending, editor.Session, editor.Mode.Fields)
            .Single(row => row.EntityId == neverOpened)));
    }

    [Fact]
    public async Task BackToEditing_ClosesTheDialog_KeepsEditModeAndThePendingList_AndSavesNothing()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        OpenReview(editor);

        FooterButton("Back to editing").Click();

        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".review-changes")));
        Assert.True(editor.Mode.IsOn);
        Assert.Equal(1, editor.Session.Pending.Count);
        Assert.Equal([true], _sessionCalls);
        Assert.Equal(0, _refreshes);
    }

    // -- Discard ----------------------------------------------------------------------------------

    [Fact]
    public async Task DiscardChanges_AsksFirst_AndStayingKeepsTheDialogAndEveryChange()
    {
        var editor = await StartEditorAsync(discardWhenAsked: false);
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        OpenReview(editor);

        FooterButton("Discard changes").Click();

        _dialogs.WaitForAssertion(() => Assert.Single(editor.Asked));
        Assert.NotEmpty(_dialogs.FindAll(".review-changes"));
        Assert.Equal(1, editor.Session.Pending.Count);
        Assert.True(editor.Mode.IsOn);
    }

    [Fact]
    public async Task DiscardChanges_WhenConfirmed_ThrowsEverythingAway_AndClosesTheDialog()
    {
        var editor = await StartEditorAsync(discardWhenAsked: true);
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        OpenReview(editor);

        FooterButton("Discard changes").Click();

        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".review-changes")));
        Assert.False(editor.Mode.IsOn);
        Assert.True(editor.Session.Pending.IsEmpty);
        Assert.Equal(0, _refreshes);
    }

    // -- Checking and refusing --------------------------------------------------------------------

    [Fact]
    public async Task ARefusedChange_ShowsTheEnginesReasonOnItsRow_AndSaveStaysOffUntilItIsRemoved()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        editor.Session.Pending.SetTarget(EditSessionChangeKinds.Relationship, MovieId, Guid.NewGuid().ToString("D"), label: "Dune Collection");
        var relationshipId = editor.Session.Pending.Changes.Single(c => c.Kind == EditSessionChangeKinds.Relationship).Id;
        _answer = request => Answer(request, change => change.Id == relationshipId
            ? (EditSessionChangeStatuses.Invalid, "That place is already taken.")
            : (EditSessionChangeStatuses.Ok, null));

        OpenReview(editor);

        Assert.Contains("That place is already taken.", Row(relationshipId).TextContent);
        Assert.True(SaveButton.HasAttribute("disabled"));

        Row(relationshipId).QuerySelector(".app-row-menu__trigger")!.Click();
        FindMenuItem("Remove this change").Click();

        _dialogs.WaitForAssertion(() => Assert.False(SaveButton.HasAttribute("disabled")));
        Assert.Equal("Save 1 change", SaveButton.TextContent.Trim());
    }

    [Fact]
    public async Task WhenTheChangesCannotBeChecked_SaysSoInPlainText_AndSaveStaysOff()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        var unreachable = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.RunEditSessionAsync), _ =>
            Task.FromResult(LibraryBatchEditOutcome<EditSessionResult>.Failed(LibraryBatchEditOutcomeStatus.Unavailable))));
        var flow = new EditSessionFlow(editor.Session, editor.Mode, new EditSessionSaver(unreachable), () => [new EditTrailItem(MovieId, "Dune", "Movie")], () => Task.FromResult(false));

        ShowDialog(flow);

        _dialogs.WaitForAssertion(() => Assert.Contains("could not be reached", _dialogs.Find(".review-changes").TextContent));
        Assert.True(SaveButton.HasAttribute("disabled"));
    }

    [Fact]
    public async Task WithNothingPending_SaysThereIsNothingToReview()
    {
        var editor = await StartEditorAsync();

        ShowDialog(editor.Flow);

        Assert.Contains("There are no changes to review.", _dialogs.Find(".review-changes").TextContent);
        Assert.True(SaveButton.HasAttribute("disabled"));
        Assert.Empty(_sessionCalls);
    }

    // -- Saving -----------------------------------------------------------------------------------

    [Fact]
    public async Task Save_WhenEverythingGoesThrough_ClosesTheDialog_RefreshesThePage_AndEndsEditMode()
    {
        var editor = await StartEditorAsync();
        StageEverything(editor.Session.Pending);
        OpenReview(editor);

        SaveButton.Click();

        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".review-changes")));
        Assert.False(editor.Mode.IsOn);
        Assert.False(editor.Session.IsActive);
        Assert.True(editor.Session.Pending.IsEmpty);
        Assert.Equal(1, _refreshes);
        Assert.Equal([true, true, false], _sessionCalls);
    }

    [Fact]
    public async Task Save_WhenAFileMoveFails_KeepsTheDialogOpen_ShowsEachRowsResult_AndSaysTheDataWasSaved()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        editor.Session.Pending.SetTarget(EditSessionChangeKinds.FileMove, MovieId, null);
        var moveId = editor.Session.Pending.Changes.Single(c => c.Kind == EditSessionChangeKinds.FileMove).Id;
        _answer = request => request.PreviewOnly
            ? Answer(request)
            : Answer(request, change => change.Id == moveId
                ? (EditSessionChangeStatuses.Failed, "The file is open in another program.")
                : (EditSessionChangeStatuses.Ok, null));
        OpenReview(editor);

        SaveButton.Click();

        _dialogs.WaitForAssertion(() => Assert.Contains("Your changes were saved; 1 file move could not be completed.", _dialogs.Find(".review-changes").TextContent));
        var titleRow = _dialogs.Find(".review-changes__row[data-state='ok']");
        Assert.Contains("Saved", titleRow.TextContent);
        Assert.Contains("The file is open in another program.", Row(moveId).TextContent);
        Assert.Equal(["Close"], _dialogs.FindAll(".app-dialog-shell__footer button").Select(button => button.TextContent.Trim()));
        Assert.Empty(_dialogs.FindAll(".review-changes__actions"));
        Assert.Equal(1, _refreshes);
        // What is left is the failed move; the saved title is no longer pending.
        Assert.Equal(EditSessionChangeKinds.FileMove, editor.Session.Pending.Changes.Single().Kind);

        FooterButton("Close").Click();

        _dialogs.WaitForAssertion(() => Assert.Empty(_dialogs.FindAll(".review-changes")));
        Assert.True(editor.Mode.IsOn);
    }

    [Fact]
    public async Task Save_WhenTheCheckAtSaveTimeRefusesAChange_WritesNothing_AndMarksTheRows()
    {
        var editor = await StartEditorAsync();
        editor.Session.Pending.SetField(MovieId, "title", "Dune 2");
        editor.Session.Pending.SetTarget(EditSessionChangeKinds.Relationship, MovieId, Guid.NewGuid().ToString("D"), label: "Dune Collection");
        var relationshipId = editor.Session.Pending.Changes.Single(c => c.Kind == EditSessionChangeKinds.Relationship).Id;
        var checks = 0;
        _answer = request =>
        {
            // The first check (opening the dialog) passes; the library changed by the time Save checks again.
            checks++;
            return checks == 1
                ? Answer(request)
                : Answer(request, change => change.Id == relationshipId
                    ? (EditSessionChangeStatuses.Invalid, "That album no longer exists.")
                    : (EditSessionChangeStatuses.Ok, null));
        };
        OpenReview(editor);

        SaveButton.Click();

        _dialogs.WaitForAssertion(() => Assert.Contains("That album no longer exists.", Row(relationshipId).TextContent));
        Assert.Contains("Not applied", _dialogs.Find(".review-changes__row[data-state='not_applied']").TextContent);
        Assert.NotEmpty(_dialogs.FindAll(".review-changes"));
        Assert.True(editor.Mode.IsOn);
        Assert.Equal(2, editor.Session.Pending.Count);
        Assert.Equal(0, _refreshes);
    }

    private void ShowDialog(EditSessionFlow flow)
    {
        var dialogs = Services.GetRequiredService<MediaEngine.Web.Services.Ui.IAppDialogService>();
        _ = ReviewChangesDialog.ShowAsync(dialogs, flow);
        _dialogs.WaitForElement(".review-changes");
    }

    private AngleSharp.Dom.IElement FindMenuItem(string label)
    {
        AngleSharp.Dom.IElement? found = null;
        _dialogs.WaitForAssertion(() =>
        {
            found = _dialogs.FindAll("[role='menuitem']").Concat(_popovers.FindAll("[role='menuitem']"))
                .FirstOrDefault(item => item.TextContent.Contains(label, StringComparison.Ordinal));
            Assert.NotNull(found);
        });
        return found!;
    }
}
