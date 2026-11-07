using AngleSharp.Dom;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The supporting-file editor (universal editor v5 WP7, acceptance A9 to A12): the Details form of one file, Suggest details from the
/// document, Belongs to with its folder-move check, Shown alongside, the Preview tab, switching between files without losing staged changes,
/// and Delete. The Engine is a hand-written stub; no file is read, moved or removed anywhere.
/// </summary>
public sealed class EditorSupportingFilePanelTests : AsyncBunitContext
{
    private static readonly Guid TitleId = SupportingFileEditorTestData.TitleId;
    private static readonly Guid FileId = SupportingFileEditorTestData.FileId;
    private static readonly Guid OtherFileId = SupportingFileEditorTestData.OtherFileId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly DashboardSessionAccessor _session = new();
    private readonly List<SupportingFileBulkUpdateRequest> _checks = [];
    private readonly List<IReadOnlyList<string>> _deletes = [];
    private EditorSupportingFileDto _file = SupportingFileEditorTestData.File();
    private EditorSuggestDetailsDto? _suggestion;
    private Func<SupportingFileBulkUpdateRequest, LibraryBatchEditOutcome<SupportingFileBulkUpdateResultDto>> _check = _ =>
        LibraryBatchEditOutcome<SupportingFileBulkUpdateResultDto>.Ok(new SupportingFileBulkUpdateResultDto { Preview = true });
    private ExtraViewDto? _view;
    private Func<SupportingFileBulkUpdateRequest, Task<LibraryBatchEditOutcome<SupportingFileBulkUpdateResultDto>>>? _checkAsync;
    private readonly List<(Guid Title, Guid File)> _reads = [];

    public EditorSupportingFilePanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(_session);
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetSupportingFileEditorAsync), args =>
            {
                var requested = (Guid)args![1]!;
                _reads.Add(((Guid)args[0]!, requested));
                return Task.FromResult<EditorSupportingFileDto?>(requested == OtherFileId
                    ? SupportingFileEditorTestData.File(OtherFileId, ownTitle: "Transcript")
                    : _file);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetSupportingFileSuggestDetailsAsync), _ => Task.FromResult(_suggestion));
            stub.SetHandler(nameof(IEngineApiClient.PreviewSupportingFilesChangeAsync), args =>
            {
                var request = (SupportingFileBulkUpdateRequest)args![1]!;
                _checks.Add(request);
                return _checkAsync?.Invoke(request) ?? Task.FromResult(_check(request));
            });
            stub.SetHandler(nameof(IEngineApiClient.DeleteSupportingFilesAsync), args =>
            {
                var request = (SupportingFileBulkDeleteRequest)args![1]!;
                _deletes.Add(request.Ids);
                return Task.FromResult(LibraryBatchEditOutcome<SupportingFileBulkDeleteResultDto>.Ok(new SupportingFileBulkDeleteResultDto
                {
                    Removed = 1,
                    Results = [new SupportingFileDeleteItemResultDto { Id = request.Ids[0], Status = SupportingFileBulkStatuses.Removed, Disk = SupportingFileDiskOutcomes.Recycled }],
                }));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetExtraAsync), _ => Task.FromResult(_view));
        }));
        Render<AppPopoverHost>();
    }

    private async Task<EditorHost> OpenFileAsync(bool administrator = false)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        if (administrator)
        {
            var id = Guid.NewGuid();
            _session.Set("test-session-token", id, id, Guid.NewGuid(),
                new DashboardAuthorityResponse(id, id, true, true, 1, 1, true, true, null, 1, [], [], []));
        }

        var host = new EditorHost(Services.GetRequiredService<IEngineApiClient>(), _device, new ServiceCollection().BuildServiceProvider(), NullLogger<EditorHost>.Instance);
        var binding = new EditorBinding(_device)
        {
            EntityId = TitleId,
            HasPermission = true,
            ShowHistory = true,
            Trail = () => [new EditTrailItem(TitleId, SupportingFileEditorTestData.TitleName, EditTrailKinds.Show)],
            OpenItem = _ => Task.FromResult(true),
        };
        Assert.True(await host.OpenAsync(binding, TitleId));
        await host.HeaderHooks.OpenSupportingFile!(FileId);
        Assert.Equal(FileId, host.Session.ActiveItem!.EntityId);
        return host;
    }

    private IRenderedComponent<EditorSupportingFilePanel> Panel(EditorHost host) =>
        Render<EditorSupportingFilePanel>(parameters => parameters.Add(panel => panel.Mode, host.Mode).Add(panel => panel.Flow, host.Flow));

    private static AppSelect Select(IRenderedComponent<EditorSupportingFilePanel> cut, string ariaLabel) =>
        cut.FindComponents<AppSelect>().Single(select => select.Instance.AriaLabel == ariaLabel).Instance;

    private static Task Pick(IRenderedComponent<EditorSupportingFilePanel> cut, string ariaLabel, string value) =>
        cut.InvokeAsync(() => Select(cut, ariaLabel).ValueChanged.InvokeAsync(value));

    private const string AlongsideLabel = "Shown alongside · optional";

    private IReadOnlyList<string> ToastLines => [.. Services.GetRequiredService<AppToastService>().Toasts.Select(toast => toast.Message)];

    // -- Opening a file -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ChoosingAFileInTheHeader_OpensItInTheEditor_WithItsOwnersTrailAndTheFileAsTheLastStep()
    {
        var host = await OpenFileAsync();

        Assert.Equal(FileId, host.Session.ActiveItem!.EntityId);
        Assert.Equal(EditTrailKinds.SupportingFile, host.Session.ActiveItem.Kind);
        Assert.Equal([TitleId, FileId], host.Session.Trail.Select(item => item.EntityId));
        Assert.NotNull(host.Mode.Fields.SupportingFile);
        Assert.False(host.Mode.Fields.CanMatch);
    }

    [Fact]
    public async Task TheForm_ShowsTheFilesValues_ItsFolderHint_AndQuickFactsWithAPreviewButton()
    {
        var host = await OpenFileAsync();

        var cut = Panel(host);

        Assert.Equal("Workbook", cut.Find("[data-field-key=title] input").GetAttribute("value"));
        Assert.Equal(["Workbook", "Guide", "Other"], Select(cut, "Role").Options!.Select(option => option.Label));
        Assert.Equal("workbook", Select(cut, "Role").Value);
        Assert.Equal("root", Select(cut, "Belongs to").Value);
        Assert.Equal("From its folder location (Season 02 / Documents).", cut.Find("[data-hint=scope]").TextContent.Trim());
        Assert.Contains("PDF document", cut.Find("aside").TextContent, StringComparison.Ordinal);
        Assert.Contains("Workbook.pdf", cut.Find("aside").TextContent, StringComparison.Ordinal);
        Assert.Equal("Preview PDF", cut.Find("[data-quick-facts-preview]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-use-folder-location]"));
    }

    // -- Typing and Suggest details -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TypingATitle_StagesOneChangeForTheFile_AndNothingIsSentToTheEngine()
    {
        var host = await OpenFileAsync();
        var cut = Panel(host);

        cut.Find("[data-field-key=title] input").Input("Shooting workbook ");

        var change = Assert.Single(host.Session.Pending.Changes);
        Assert.Equal(EditSessionChangeKinds.SupportingFiles, change.Kind);
        Assert.Equal("Shooting workbook", change.SupportingFiles!.Changes.Title);
        Assert.Equal(1, host.Session.Pending.ChangeCount);
        Assert.Empty(_checks);
        // The box keeps what was typed, trailing space included, while the staged value is trimmed.
        Assert.Equal("Shooting workbook ", cut.Find("[data-field-key=title] input").GetAttribute("value"));
    }

    [Fact]
    public async Task SuggestDetails_FillsTheTitleAndDescriptionFromTheDocument_StagedLikeTyping()
    {
        _suggestion = new EditorSuggestDetailsDto
        {
            SupportingFileId = FileId,
            Source = EditorSuggestDetailsSources.Pdf,
            Title = "Shooting Fundamentals Workbook",
            Description = "Drills for form, footwork and release.",
        };
        var host = await OpenFileAsync();
        var cut = Panel(host);

        cut.Find("[data-suggest-details]").Click();

        cut.WaitForAssertion(() => Assert.Equal("Shooting Fundamentals Workbook", cut.Find("[data-field-key=title] input").GetAttribute("value")));
        Assert.Equal("Drills for form, footwork and release.", cut.Find("[data-field-key=description] textarea").TextContent.Trim());
        var stage = host.Session.Pending.FindSupportingFile(FileId)!;
        Assert.Equal(["Title", "Description"], stage.Rows.Select(row => row.Label));
        Assert.Contains("Nothing is kept until you save", cut.Find("[data-hint=suggest]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuggestDetails_OnADocumentWithNothingToSuggest_SaysSo_AndChangesNothing()
    {
        _suggestion = new EditorSuggestDetailsDto { SupportingFileId = FileId, Source = EditorSuggestDetailsSources.None };
        var host = await OpenFileAsync();
        var cut = Panel(host);

        cut.Find("[data-suggest-details]").Click();

        cut.WaitForAssertion(() => Assert.Contains("Nothing was changed", cut.Find("[data-hint=suggest]").TextContent, StringComparison.Ordinal));
        Assert.True(host.Session.Pending.IsEmpty);
    }

    [Fact]
    public async Task SuggestDetails_IsOfferedForDocumentsOnly()
    {
        _file = SupportingFileEditorTestData.File(type: SupportingFileListDefaults.TypeClip, typeLabel: "Video");
        var host = await OpenFileAsync();

        var cut = Panel(host);

        Assert.Empty(cut.FindAll("[data-suggest-details]"));
    }

    // -- Belongs to and Shown alongside ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ChoosingAnotherPlace_InAManagedFolder_AsksTheEngineWhetherTheFileMoves_AndTheFooterWarns()
    {
        _check = _ => LibraryBatchEditOutcome<SupportingFileBulkUpdateResultDto>.Ok(new SupportingFileBulkUpdateResultDto
        {
            Preview = true,
            Moves = [new SupportingFilePlannedMoveDto { Id = FileId.ToString("D"), From = "Season 02/Documents/Workbook.pdf", To = "Season 01/Documents/Workbook.pdf" }],
        });
        var host = await OpenFileAsync();
        var cut = Panel(host);

        await Pick(cut, "Belongs to", "season:1");

        var asked = Assert.Single(_checks);
        Assert.Equal("season:1", asked.Changes.ScopeKey);
        var stage = host.Session.Pending.FindSupportingFile(FileId)!;
        Assert.True(stage.HasMove);
        var footer = EditorFooterRules.For(FileWriteSetting.Unknown, host.Session.Pending);
        Assert.Equal(EditorFooterStatus.WillMove, footer.Status);
        Assert.Equal("Season 01/Documents/Workbook.pdf", footer.MoveTo);
        Assert.Equal("Set by you.", cut.Find("[data-hint=scope]").TextContent.Trim());
        Assert.Contains("moves to the folder for its new place", cut.Find("[data-hint=move]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChoosingAnotherPlace_InAReadOnlyFolder_NeverAsksAboutAMove_AndTheFileStaysWhereItIs()
    {
        _file = SupportingFileEditorTestData.File(readOnly: true);
        var host = await OpenFileAsync();
        var cut = Panel(host);

        await Pick(cut, "Belongs to", "season:1");

        Assert.Empty(_checks);
        Assert.False(host.Session.Pending.FindSupportingFile(FileId)!.HasMove);
        Assert.Equal("Set by you. The file stays where it is.", cut.Find("[data-hint=scope]").TextContent.Trim());
        Assert.Contains(EditorQuickFactsBuilder.SupportingFileReadOnlyNote, cut.Find("aside").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UseFolderLocation_IsOfferedOnlyForAPlaceThePersonSet_AndGoesBackToTheFolder()
    {
        _file = SupportingFileEditorTestData.File(scopeKey: "season:1", scopeSource: "you");
        var host = await OpenFileAsync();
        var cut = Panel(host);

        cut.Find("[data-use-folder-location]").Click();

        var stage = host.Session.Pending.FindSupportingFile(FileId)!;
        Assert.True(stage.UseFolderLocation);
        Assert.Equal("From its folder location (Season 02 / Documents).", cut.Find("[data-hint=scope]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-use-folder-location]"));
    }

    [Fact]
    public async Task ShownAlongside_OffersTheEpisodesUnderThePlace_AndIsHiddenOnceTheFileBelongsToAnEpisode()
    {
        _file = SupportingFileEditorTestData.File(scopeKey: "season:2", inferredAlongsideKey: "episode:s2e3", alongsideKey: "episode:s2e3");
        var host = await OpenFileAsync();
        var cut = Panel(host);

        var options = Select(cut, AlongsideLabel).Options!.Select(option => option.Label).ToList();
        Assert.Equal(["None", "S02E01 Warm-up", "S02E03 Footwork"], options);
        Assert.Equal("Guessed from the file name. It is listed with that item.", cut.Find("[data-hint=alongside]").TextContent.Trim());

        await Pick(cut, "Belongs to", "episode:s2e3");

        Assert.Empty(cut.FindAll("[data-field-key=alongside]"));
    }

    [Fact]
    public async Task ShownAlongside_ReadsAsOptional_WithAPlaceholderAndHintThatNameWhatTheTitleCallsItsItems()
    {
        _file = SupportingFileEditorTestData.File(scopeKey: "season:2");
        var host = await OpenFileAsync();
        var cut = Panel(host);

        Assert.Equal("Choose an episode", Select(cut, AlongsideLabel).Placeholder);
        Assert.Contains("Choose an episode", cut.Find("[data-field-key=alongside] .tl-select-value").TextContent, StringComparison.Ordinal);
        Assert.Equal(AlongsideLabel, cut.Find("[data-field-key=alongside] .tl-editor-field__label").TextContent.Trim());

        await Pick(cut, AlongsideLabel, "episode:s2e1");

        Assert.Equal("Also listed on that episode's Extras.", cut.Find("[data-hint=alongside]").TextContent.Trim());
    }

    [Fact]
    public async Task ShownAlongside_OnACourse_TalksOfLessons()
    {
        _file = SupportingFileEditorTestData.File(scopeKey: "season:2", alongsideKey: "episode:s2e1", mediaType: "Videos");
        var host = await OpenFileAsync();
        var cut = Panel(host);

        Assert.Equal("Choose a lesson", Select(cut, AlongsideLabel).Placeholder);
        Assert.Equal("Also listed on that lesson's Extras.", cut.Find("[data-hint=alongside]").TextContent.Trim());
    }

    [Fact]
    public async Task MovingBelongsToAnEpisode_ClearsTheStaleShownAlongside_AndMovingBackKeepsIt()
    {
        _file = SupportingFileEditorTestData.File(scopeKey: "season:2", alongsideKey: "episode:s2e3");
        var host = await OpenFileAsync();
        var cut = Panel(host);

        await Pick(cut, "Belongs to", "episode:s2e1");

        Assert.Empty(cut.FindAll("[data-field-key=alongside]"));
        var stage = host.Session.Pending.FindSupportingFile(FileId)!;
        Assert.NotNull(stage.Row(SupportingFileFields.Alongside));
        Assert.True(stage.ToRequest().Changes.UseGuessedAlongside);

        await Pick(cut, "Belongs to", "season:2");

        Assert.NotNull(cut.Find("[data-field-key=alongside]"));
        Assert.True(host.Session.Pending.IsEmpty);
    }

    [Fact]
    public async Task TheDescription_StopsAtTheLimitFromTheEditorsConfiguration_WithNoCounter()
    {
        _file = SupportingFileEditorTestData.File(descriptionMaxCharacters: 120);
        var host = await OpenFileAsync();
        var cut = Panel(host);

        Assert.Equal("120", cut.Find("[data-field-key=description] textarea").GetAttribute("maxlength"));
        Assert.Empty(cut.FindAll(".tl-editor-field__count"));
    }

    [Fact]
    public async Task AGroupReadFromAFolder_CanBeTakenOff_ThroughANoGroupChoice()
    {
        _file = SupportingFileEditorTestData.File(group: "Documents", groupSource: "folder");
        var host = await OpenFileAsync();
        var cut = Panel(host);
        var group = cut.FindComponent<AppAutocomplete<string>>().Instance;

        var offered = (await group.SearchFunc!("", CancellationToken.None)).ToList();
        Assert.Equal("\u0001none", offered[0]);
        await cut.InvokeAsync(() => group.ValueChanged.InvokeAsync(offered[0]));

        var stage = host.Session.Pending.FindSupportingFile(FileId)!;
        Assert.True(stage.NoGroup);
        Assert.True(stage.ToRequest().Changes.NoGroup);
        Assert.Equal("No group", stage.Row(SupportingFileFields.Group)!.After);
    }

    [Fact]
    public async Task TheGroupHint_ForAFolderGroup_SaysPickingAnotherChangesIt_NotThatItMovesTheFile()
    {
        _file = SupportingFileEditorTestData.File(group: "Documents", groupSource: "folder");
        var host = await OpenFileAsync();
        var cut = Panel(host);

        Assert.Equal("From its folder. Pick another group to change it.", cut.Find("[data-field-key=group] [data-hint=group]").TextContent.Trim());
    }

    [Fact]
    public async Task AnAnswerForAnEarlierBelongsTo_ArrivingLate_DoesNotChangeTheMoveOfTheChoiceStagedNow()
    {
        var first = new TaskCompletionSource<LibraryBatchEditOutcome<SupportingFileBulkUpdateResultDto>>();
        _checkAsync = request => request.Changes.ScopeKey == "season:1"
            ? first.Task
            : Task.FromResult(LibraryBatchEditOutcome<SupportingFileBulkUpdateResultDto>.Ok(new SupportingFileBulkUpdateResultDto
            {
                Preview = true,
                Moves = [new SupportingFilePlannedMoveDto { Id = FileId.ToString("D"), From = "Season 02/Workbook.pdf", To = "Season 03/Workbook.pdf" }],
            }));
        _file = SupportingFileEditorTestData.File(scopeKey: "season:2", scopeSource: "you");
        var host = await OpenFileAsync();
        var cut = Panel(host);

        var slow = Pick(cut, "Belongs to", "season:1");
        await Pick(cut, "Belongs to", "root");
        first.SetResult(LibraryBatchEditOutcome<SupportingFileBulkUpdateResultDto>.Ok(new SupportingFileBulkUpdateResultDto
        {
            Preview = true,
            Moves = [new SupportingFilePlannedMoveDto { Id = FileId.ToString("D"), From = "Season 02/Workbook.pdf", To = "Season 01/Workbook.pdf" }],
        }));
        await slow;

        var stage = host.Session.Pending.FindSupportingFile(FileId)!;
        Assert.Equal("root", stage.ScopeKey);
        Assert.Equal("Season 03/Workbook.pdf", stage.MoveTo);
    }

    [Fact]
    public async Task AFailedAnswerForAnEarlierBelongsTo_ArrivingLate_DoesNotPutAWarningOnTheChoiceStagedNow()
    {
        var first = new TaskCompletionSource<LibraryBatchEditOutcome<SupportingFileBulkUpdateResultDto>>();
        _checkAsync = request => request.Changes.ScopeKey == "season:1"
            ? first.Task
            : Task.FromResult(LibraryBatchEditOutcome<SupportingFileBulkUpdateResultDto>.Ok(new SupportingFileBulkUpdateResultDto { Preview = true }));
        var host = await OpenFileAsync();
        var cut = Panel(host);

        var slow = Pick(cut, "Belongs to", "season:1");
        await Pick(cut, "Belongs to", "season:3");
        first.SetResult(LibraryBatchEditOutcome<SupportingFileBulkUpdateResultDto>.Failed(LibraryBatchEditOutcomeStatus.Unavailable, "not now"));
        await slow;

        Assert.Empty(cut.FindAll("[data-hint=move]"));
    }

    [Fact]
    public async Task OnADevicesThatCannotEdit_TheDetailsAreOnlyRead()
    {
        var host = await OpenFileAsync();
        host.Mode.HasPermission = false;

        var cut = Panel(host);

        Assert.NotNull(cut.Find("[data-field-key=title] input").GetAttribute("readonly"));
        Assert.Empty(cut.FindAll("[data-suggest-details]"));
    }

    // -- Switching files ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SwitchingToAnotherFile_KeepsWhatWasStagedOnTheFirst()
    {
        var host = await OpenFileAsync();
        var cut = Panel(host);
        cut.Find("[data-field-key=title] input").Input("Shooting workbook");

        await host.HeaderHooks.OpenSupportingFile!(OtherFileId);

        Assert.Equal(OtherFileId, host.Session.ActiveItem!.EntityId);
        Assert.Equal("Shooting workbook", host.Session.Pending.FindSupportingFile(FileId)!.Title);
        Assert.Equal(1, host.Session.Pending.ChangeCount);
        // The form now shows the other file, not the one that was left.
        cut.WaitForAssertion(() => Assert.Equal("Transcript", cut.Find("[data-field-key=title] input").GetAttribute("value")));
    }

    [Fact]
    public async Task Discard_ThrowsAwayWhatWasStagedOnAFile()
    {
        var host = await OpenFileAsync();
        host.ConfirmDiscard = () => Task.FromResult(true);
        var cut = Panel(host);
        cut.Find("[data-field-key=title] input").Input("Shooting workbook");
        Assert.NotNull(host.Session.Pending.FindSupportingFile(FileId));

        var left = await host.Flow.LeaveAsync();

        Assert.True(left);
        Assert.Null(host.Session.Pending.FindSupportingFile(FileId));
        Assert.True(host.Session.Pending.IsEmpty);
    }

    [Fact]
    public async Task ReviewsEditOnAFileOfAnotherTitle_OpensTheFileUnderItsOwnTitle()
    {
        var host = await OpenFileAsync();
        var cut = Panel(host);
        cut.Find("[data-field-key=title] input").Input("Shooting workbook");
        var otherTitle = Guid.NewGuid();
        host.Session.SwitchItem([new EditTrailItem(otherTitle, "Another course", EditTrailKinds.Show)]);
        var row = EditReviewRows.Build(host.Session.Pending, host.Session, host.Mode.Fields).Single();
        _reads.Clear();

        await host.Flow.JumpToAsync(row);

        Assert.Equal(FileId, host.Session.ActiveItem!.EntityId);
        Assert.Contains((TitleId, FileId), _reads);
        Assert.DoesNotContain(_reads, read => read.Title == otherTitle);
    }

    [Fact]
    public async Task ReviewsEditOnAFilesRow_OpensThatFile_AndItsRemoveTakesOnlyThatFieldOut()
    {
        var host = await OpenFileAsync();
        var cut = Panel(host);
        cut.Find("[data-field-key=title] input").Input("Shooting workbook");
        await host.HeaderHooks.OpenSupportingFile!(OtherFileId);
        var row = EditReviewRows.Build(host.Session.Pending, host.Session, host.Mode.Fields).Single();

        Assert.True(host.Flow.CanJumpTo(row));
        await host.Flow.JumpToAsync(row);

        Assert.Equal(FileId, host.Session.ActiveItem!.EntityId);
        Assert.True(host.Flow.RemoveReviewRow(row));
        Assert.True(host.Session.Pending.IsEmpty);
    }

    // -- Preview --------------------------------------------------------------------------------------------------------------------

    private IRenderedComponent<EditorPreviewTab> Preview(EditorHost host) =>
        Render<EditorPreviewTab>(parameters => parameters.Add(tab => tab.Fields, host.Mode.Fields));

    [Fact]
    public async Task Preview_OfAPdf_ReadsItInTheBrowsersOwnReader_WithDownload()
    {
        _view = new ExtraViewDto
        {
            Id = FileId.ToString("D"),
            Kind = DetailExtraCardKinds.Document,
            Title = "Workbook",
            Items = [new ExtraViewItemDto { Id = FileId.ToString("D"), Title = "Workbook", FileName = "Workbook.pdf", MimeType = "application/pdf" }],
        };
        var host = await OpenFileAsync();

        var cut = Preview(host);

        cut.WaitForAssertion(() => Assert.Equal($"/engine-extra/{FileId:D}/file", cut.Find("iframe[data-testid=extra-document]").GetAttribute("src")));
        Assert.Equal("Workbook.pdf", cut.Find("[data-preview-download]").GetAttribute("download"));
    }

    [Fact]
    public async Task Preview_OfAFileTheBrowserCannotShow_SaysSo_AndOffersDownload()
    {
        _view = new ExtraViewDto
        {
            Id = FileId.ToString("D"),
            Kind = DetailExtraCardKinds.Document,
            Title = "Guidebook",
            Items = [new ExtraViewItemDto { Id = FileId.ToString("D"), Title = "Guidebook", FileName = "Guidebook.epub", MimeType = "application/epub+zip" }],
        };
        var host = await OpenFileAsync();

        var cut = Preview(host);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=extra-download-only]")));
        Assert.Contains("Guidebook.epub", cut.Find(".tl-extra-viewer__download").TextContent, StringComparison.Ordinal);
        // The viewer already carries the Download, so the bar above it does not add a second one.
        Assert.Empty(cut.FindAll("[data-preview-download]"));
        Assert.Single(cut.FindAll("a[download]"));
    }

    [Fact]
    public async Task Preview_OfAPictureFolder_ShowsAGalleryOfThumbnails()
    {
        _view = new ExtraViewDto
        {
            Id = FileId.ToString("D"),
            Kind = DetailExtraCardKinds.Pictures,
            Title = "Photos",
            Items =
            [
                new ExtraViewItemDto { Id = "p1", Title = "One", FileName = "1.jpg", MimeType = "image/jpeg" },
                new ExtraViewItemDto { Id = "p2", Title = "Two", FileName = "2.jpg", MimeType = "image/jpeg" },
            ],
        };
        var host = await OpenFileAsync();

        var cut = Preview(host);

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".tl-extra-viewer__thumb").Count));
    }

    [Fact]
    public async Task Preview_OfAClip_PlaysItInAPlainPlayer()
    {
        _view = new ExtraViewDto
        {
            Id = FileId.ToString("D"),
            Kind = DetailExtraCardKinds.Clip,
            Title = "Making of",
            Items = [new ExtraViewItemDto { Id = FileId.ToString("D"), Title = "Making of", FileName = "making-of.mp4", MimeType = "video/mp4" }],
        };
        var host = await OpenFileAsync();

        var cut = Preview(host);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("video.tl-extra-viewer__video")));
    }

    [Fact]
    public async Task Preview_OfAFileTheEngineCannotFind_SaysItIsUnavailable()
    {
        _view = null;
        var host = await OpenFileAsync();

        var cut = Preview(host);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=extra-unavailable]")));
    }

    // -- The header -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheHeader_NamesTheOwnersKind_TheFilesTitle_ItsFacts_AndTheSupportingFileChip()
    {
        var host = await OpenFileAsync();

        var cut = Render<EditorItemHeader>(parameters => parameters
            .Add(header => header.Flow, host.Flow)
            .Add(header => header.Fields, host.Mode.Fields)
            .Add(header => header.Family, host.Family)
            .Add(header => header.Hooks, host.HeaderHooks));

        Assert.Equal("Course", cut.Find(".tl-editor-header__label").TextContent.Trim());
        Assert.Equal("Workbook", cut.Find(".tl-editor-header__title").TextContent.Trim());
        Assert.Equal("PDF · 18 pages · 2.4 MB", cut.Find(".tl-editor-header__facts").TextContent.Trim());
        Assert.Single(cut.FindAll("[data-supporting-file-chip]"));
        Assert.Empty(cut.FindAll(".tl-editor-header__chips .tl-status-badge[data-tone=success]"));
    }

    // -- Delete ---------------------------------------------------------------------------------------------------------------------

    private IRenderedComponent<AppDialogHost> RenderDialogs() => Render<AppDialogHost>();

    // The box's two buttons are found by their words, so the test does not depend on which side each sits.
    private static IElement DialogButton(IRenderedComponent<AppDialogHost> dialogs, string text)
    {
        dialogs.WaitForAssertion(() => Assert.Equal(2, dialogs.FindAll("footer button").Count));
        return dialogs.FindAll("footer button").Single(button => button.TextContent.Trim() == text);
    }

    [Fact]
    public async Task Delete_AsksOnceNamingTheFile_ThenRemovesItAtOnce_DropsItsStagedChanges_AndGoesBackToTheTitle()
    {
        var host = await OpenFileAsync(administrator: true);
        host.Session.Pending.SetSupportingFile(SupportingFileStaging.SetTitle(_file, SupportingFileStaging.Start(_file), "Unsaved title"));
        var dialogs = RenderDialogs();
        Assert.True(EditorSupportingFileFlow.CanDelete(host, _device, _session));

        var deleting = dialogs.InvokeAsync(() => EditorSupportingFileFlow.DeleteAsync(
            host, Services.GetRequiredService<IAppDialogService>(), Services.GetRequiredService<IAppToastService>(), _device, _session));
        dialogs.WaitForAssertion(() => Assert.Contains("Delete Workbook.pdf?", dialogs.Markup, StringComparison.Ordinal));
        Assert.Empty(_deletes);
        DialogButton(dialogs, "Delete").Click();

        Assert.True(await deleting);
        Assert.Equal([FileId.ToString("D")], Assert.Single(_deletes));
        Assert.True(host.Session.Pending.IsEmpty);
        Assert.Equal(TitleId, host.Session.ActiveItem!.EntityId);
        Assert.Contains(ToastLines, line => line.Contains("Deleted Workbook.pdf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_SayingNo_RemovesNothing()
    {
        var host = await OpenFileAsync(administrator: true);
        var dialogs = RenderDialogs();

        var deleting = dialogs.InvokeAsync(() => EditorSupportingFileFlow.DeleteAsync(
            host, Services.GetRequiredService<IAppDialogService>(), Services.GetRequiredService<IAppToastService>(), _device, _session));
        DialogButton(dialogs, "Keep").Click();

        Assert.False(await deleting);
        Assert.Empty(_deletes);
        Assert.Equal(FileId, host.Session.ActiveItem!.EntityId);
    }

    [Fact]
    public async Task Delete_IsNotOfferedToSomeoneWhoMayNotDelete_NorOnATitle()
    {
        var host = await OpenFileAsync(administrator: false);

        Assert.False(EditorSupportingFileFlow.CanDelete(host, _device, _session));
        Assert.False(EditorDeleteFlow.CanDeleteActive(host, _device, _session));
    }
}
