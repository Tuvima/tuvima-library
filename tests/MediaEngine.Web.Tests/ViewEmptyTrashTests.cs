using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.View;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Empty Trash in the Dashboard (spec view-personal-file-deletion, WP4): the Trash header and who sees its button, the confirmation
/// dialog and each way it can end, the note on a tile that could not be deleted, and Move to Trash for items in a read-only folder.
/// The Engine is a hand-written stub or handler; nothing is deleted anywhere.
/// </summary>
public sealed class ViewEmptyTrashTests : AsyncBunitContext
{
    private const long FourPointTwoGb = 4_509_715_661; // 4.2 GB in binary units, as the Dashboard formats sizes

    private static readonly Guid LibraryId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    private Func<Task<ViewTrashOutcome<ViewTrashSummaryDto>>> _summary;
    private Func<Task<ViewTrashOutcome<ViewTrashEmptyPreviewDto>>> _preview;
    private Func<Task<ViewTrashOutcome<ViewTrashEmptyResultDto>>> _empty;
    private readonly List<string> _emptyFingerprints = [];
    private readonly List<string> _calls = [];
    private int _previewCalls;

    public ViewEmptyTrashTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _summary = () => Task.FromResult(ViewTrashOutcome<ViewTrashSummaryDto>.Ok(Summary(128)));
        _preview = () => Task.FromResult(ViewTrashOutcome<ViewTrashEmptyPreviewDto>.Ok(Preview()));
        _empty = () => Task.FromResult(ViewTrashOutcome<ViewTrashEmptyResultDto>.Ok(new ViewTrashEmptyResultDto(128, FourPointTwoGb, 0, [])));
        Services.AddSingleton(_device);
        Services.AddSingleton<ViewWorkspaceService>();
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetViewTrashSummaryAsync), _ =>
            {
                _calls.Add("summary");
                return _summary();
            });
            stub.SetHandler(nameof(IEngineApiClient.GetViewTrashEmptyPreviewAsync), _ =>
            {
                _calls.Add("preview");
                _previewCalls++;
                return _preview();
            });
            stub.SetHandler(nameof(IEngineApiClient.EmptyViewTrashAsync), args =>
            {
                _calls.Add("empty");
                _emptyFingerprints.Add((string)args![0]!);
                return _empty();
            });
        }));
    }

    private Task UseAsync(string formFactor, bool touch) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(
            formFactor == "phone" ? "mobile" : "web", formFactor, touch, FinePointer: formFactor == "desktop" && !touch, Hover: formFactor == "desktop" && !touch));

    // -- Builders ------------------------------------------------------------------------------

    private static ViewTrashSummaryDto Summary(int items, long bytes = FourPointTwoGb) => new(items, items, 0, 0, 0, bytes);

    private static ViewTrashEmptyPreviewDto Preview(
        int photos = 112,
        int videos = 9,
        int documents = 7,
        int other = 0,
        long bytes = FourPointTwoGb,
        IEnumerable<ViewTrashPreviewGalleryDto>? galleries = null,
        IEnumerable<ViewTrashPreviewSkippedDto>? skipped = null,
        bool compound = false,
        string fingerprint = "fp-1",
        int? galleryItems = null)
    {
        var list = galleries?.ToList() ?? [];
        return new([], list, galleryItems ?? (list.Count == 0 ? 0 : list.Max(gallery => gallery.ItemCount)), photos + videos + documents + other, photos, videos, documents, other,
            skipped?.ToList() ?? [], bytes, compound, fingerprint);
    }

    private static ViewTrashPreviewGalleryDto Gallery(string name, int items = 1) => new(Guid.NewGuid(), name, items);

    private static ViewTrashPreviewSkippedDto Skipped(string code, int count, string reason = "") => new(code, reason, count);

    private static ViewTrashPreviewSkippedDto AllSkipped(int count) => Skipped(ViewTrashOutcomes.SkipContributionPending, count);

    private static ViewTrashOutcome<ViewTrashEmptyPreviewDto> Ok(ViewTrashEmptyPreviewDto preview) => ViewTrashOutcome<ViewTrashEmptyPreviewDto>.Ok(preview);

    // -- The text -------------------------------------------------------------------------------

    [Fact]
    public void SizeLine_SaysHowManyItemsAndHowMuchSpace()
    {
        Assert.Equal("128 items · 4.2 GB", EmptyTrashText.SizeLine(Summary(128)));
        Assert.Equal("1 item · 4.2 GB", EmptyTrashText.SizeLine(Summary(1)));
        Assert.Equal("3 items", EmptyTrashText.SizeLine(Summary(3, bytes: 0)));
    }

    [Fact]
    public void KindsLine_LeavesOutKindsWithNothingInThemAndUsesSingularForms()
    {
        Assert.Equal("112 photos, 9 videos, 7 documents · 4.2 GB", EmptyTrashText.KindsLine(Preview()));
        Assert.Equal("1 photo, 1 document · 4.2 GB", EmptyTrashText.KindsLine(Preview(photos: 1, videos: 0, documents: 1)));
        Assert.Equal("2 videos, 1 other file", EmptyTrashText.KindsLine(Preview(photos: 0, videos: 2, documents: 0, other: 1, bytes: 0)));
    }

    [Theory]
    [InlineData(128, 4_509_715_661L, 0, "Deleted 128 items. 4.2 GB freed.", false)]
    [InlineData(1, 0L, 0, "Deleted 1 item.", false)]
    [InlineData(126, 4_509_715_661L, 2, "126 deleted, 2 couldn't be deleted and are still in Trash.", true)]
    [InlineData(127, 100L, 1, "127 deleted, 1 couldn't be deleted and is still in Trash.", true)]
    [InlineData(0, 0L, 3, "3 items couldn't be deleted and are still in Trash.", true)]
    [InlineData(0, 0L, 0, "Nothing was deleted.", false)]
    public void Outcome_SaysWhatWentAndWhatStayedInTrash(int deleted, long bytes, int failed, string expected, bool warning)
    {
        var result = new ViewTrashEmptyResultDto(deleted, bytes, 0,
            Enumerable.Range(0, failed).Select(_ => new ViewTrashEmptyFailureDto(Guid.NewGuid(), "The file is in use.")).ToList());

        var (message, isWarning) = EmptyTrashText.Outcome(result);

        Assert.Equal(expected, message);
        Assert.Equal(warning, isWarning);
    }

    [Theory]
    [InlineData("Couldn't delete: the file is in use.", "Couldn't delete: the file is in use.")]
    [InlineData("This file changed since you opened this.", "Couldn't delete: this file changed since you opened this.")]
    [InlineData("In a read-only folder.", "Couldn't delete: in a read-only folder.")]
    public void FailureNote_ReadsAsCouldntDeleteWhateverTheEnginesSentenceStartsWith(string reason, string expected) =>
        Assert.Equal(expected, EmptyTrashText.FailureNote(reason));

    [Fact]
    public void ReadOnlyNote_CountsTheItemsLeftInPlace()
    {
        Assert.Null(EmptyTrashText.ReadOnlyNote(0));
        Assert.Equal("1 item is in a read-only folder and wasn't moved to Trash.", EmptyTrashText.ReadOnlyNote(1));
        Assert.Equal("3 items are in a read-only folder and weren't moved to Trash.", EmptyTrashText.ReadOnlyNote(3));
    }

    // -- The Trash header ----------------------------------------------------------------------

    private IRenderedComponent<ViewTrashHeader> RenderHeader(bool isMine = true, int version = 0, Action? changed = null) =>
        Render<ViewTrashHeader>(parameters => parameters
            .Add(header => header.IsMine, isMine)
            .Add(header => header.Version, version)
            .Add(header => header.OnChanged, EventCallback.Factory.Create(this, () => changed?.Invoke())));

    private static string Text(IRenderedComponent<ViewTrashHeader> cut) =>
        Regex.Replace(cut.Find(".view-trash-header").TextContent, "\\s+", " ").Trim();

    [Fact]
    public async Task Header_OnADesktopShowsTheExplanationTheSizeAndTheRedEmptyTrashButton()
    {
        await UseAsync("desktop", touch: false);

        var cut = RenderHeader();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".view-trash-header")));
        Assert.Equal("Items in Trash stay on your disk until you empty Trash.", cut.Find(".view-trash-header__lead").TextContent.Trim());
        Assert.Equal("128 items · 4.2 GB", cut.Find(".view-trash-header__size").TextContent.Trim());
        Assert.Single(cut.FindAll(".view-trash-header button"), button => button.TextContent.Trim() == "Empty Trash…");
        Assert.DoesNotContain("on a computer", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Header_OnAPhoneShowsTheSizeButSaysToEmptyTrashOnAComputer()
    {
        await UseAsync("phone", touch: true);

        var cut = RenderHeader();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".view-trash-header")));
        Assert.Equal("Items in Trash stay on your disk until you empty Trash.", cut.Find(".view-trash-header__lead").TextContent.Trim());
        Assert.Equal("128 items · 4.2 GB", cut.Find(".view-trash-header__size").TextContent.Trim());
        Assert.Equal("Empty Trash on a computer.", cut.Find(".view-trash-header__computer").TextContent.Trim());
        Assert.Empty(cut.FindAll(".view-trash-header button"));
    }

    [Fact]
    public async Task Header_OnATabletOrATouchLaptopHasNoButtonEither()
    {
        await UseAsync("tablet", touch: true);

        var cut = RenderHeader();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".view-trash-header")));
        Assert.Empty(cut.FindAll(".view-trash-header button"));
        Assert.Contains("Empty Trash on a computer.", Text(cut), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Header_InAnotherScopeShowsNothingAndNeverAsksTheEngine()
    {
        await UseAsync("desktop", touch: false);

        var cut = RenderHeader(isMine: false);

        Assert.Empty(cut.FindAll(".view-trash-header"));
        Assert.Empty(cut.FindAll("button"));
        Assert.DoesNotContain("summary", _calls);
    }

    [Fact]
    public async Task Header_OnAnEmptyTrashSaysSoAndOffersNoButton()
    {
        await UseAsync("desktop", touch: false);
        _summary = () => Task.FromResult(ViewTrashOutcome<ViewTrashSummaryDto>.Ok(new ViewTrashSummaryDto(0, 0, 0, 0, 0, 0)));

        var cut = RenderHeader();

        cut.WaitForAssertion(() => Assert.Equal("Trash is empty.", Text(cut)));
        Assert.Empty(cut.FindAll(".view-trash-header button"));
    }

    [Fact]
    public async Task Header_WhenTheEngineRefusesTheSummaryShowsNothing()
    {
        await UseAsync("desktop", touch: false);
        _summary = () => Task.FromResult(ViewTrashOutcome<ViewTrashSummaryDto>.Failed(ViewTrashOutcomeStatus.Forbidden));

        var cut = RenderHeader();

        cut.WaitForState(() => _calls.Contains("summary"));
        Assert.Empty(cut.FindAll(".view-trash-header"));
    }

    [Fact]
    public async Task Header_ReadsTheSizeAgainWhenThePageSaysTheListChanged()
    {
        await UseAsync("desktop", touch: false);
        var cut = RenderHeader();
        cut.WaitForAssertion(() => Assert.Contains("128 items", Text(cut), StringComparison.Ordinal));

        _summary = () => Task.FromResult(ViewTrashOutcome<ViewTrashSummaryDto>.Ok(Summary(127)));
        cut.Render(parameters => parameters.Add(header => header.IsMine, true).Add(header => header.Version, 1));

        cut.WaitForAssertion(() => Assert.Contains("127 items", Text(cut), StringComparison.Ordinal));
        Assert.Equal(2, _calls.Count(call => call == "summary"));
    }

    [Fact]
    public async Task Header_HandlerRefusesOnAPhoneEvenWhenSomethingElseCallsIt()
    {
        await UseAsync("phone", touch: true);
        var host = Render<AppDialogHost>();
        var cut = RenderHeader();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".view-trash-header")));

        await cut.InvokeAsync(() => cut.Instance.EmptyTrashAsync());

        Assert.DoesNotContain("preview", _calls);
        Assert.Empty(Services.GetRequiredService<AppDialogService>().Dialogs);
        Assert.Equal("Empty Trash on a computer.", Assert.Single(Services.GetRequiredService<AppToastService>().Toasts).Message);
        Assert.Empty(host.FindAll(".empty-trash"));
    }

    [Fact]
    public async Task Header_HandlerRefusesOutsideTheOwnersSpaceEvenOnADesktop()
    {
        await UseAsync("desktop", touch: false);
        Render<AppDialogHost>();
        var cut = RenderHeader(isMine: false);

        await cut.InvokeAsync(() => cut.Instance.EmptyTrashAsync());

        Assert.DoesNotContain("preview", _calls);
        Assert.Empty(Services.GetRequiredService<AppDialogService>().Dialogs);
    }

    [Fact]
    public async Task Header_EmptyingShowsTheToastKeepsTheFailureNotesAndTellsThePageToReload()
    {
        await UseAsync("desktop", touch: false);
        var stuck = Guid.NewGuid();
        _empty = () => Task.FromResult(ViewTrashOutcome<ViewTrashEmptyResultDto>.Ok(
            new ViewTrashEmptyResultDto(126, FourPointTwoGb, 0, [new ViewTrashEmptyFailureDto(stuck, "Couldn't delete: the file is in use.")])));
        var host = Render<AppDialogHost>();
        var changed = 0;
        var cut = RenderHeader(changed: () => changed++);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".view-trash-header button")));

        await cut.InvokeAsync(() => cut.Find(".view-trash-header button").Click());
        host.WaitForElement(".empty-trash__lead");
        _summary = () => Task.FromResult(ViewTrashOutcome<ViewTrashSummaryDto>.Ok(Summary(2)));
        await host.InvokeAsync(() => FooterButton(host, "Delete 128 items from disk").Click());

        cut.WaitForAssertion(() => Assert.Equal(1, changed));
        var toast = Assert.Single(Services.GetRequiredService<AppToastService>().Toasts);
        Assert.Equal(("126 deleted, 1 couldn't be deleted and is still in Trash.", AppSeverity.Warning), (toast.Message, toast.Severity));
        Assert.Equal("Couldn't delete: the file is in use.", Services.GetRequiredService<ViewWorkspaceService>().TrashFailureReason(stuck));
        cut.WaitForAssertion(() => Assert.Contains("2 items", Text(cut), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Header_CancellingTheDialogDeletesNothingAndLeavesThePageAlone()
    {
        await UseAsync("desktop", touch: false);
        var host = Render<AppDialogHost>();
        var changed = 0;
        var cut = RenderHeader(changed: () => changed++);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".view-trash-header button")));

        await cut.InvokeAsync(() => cut.Find(".view-trash-header button").Click());
        host.WaitForElement(".empty-trash__lead");
        await host.InvokeAsync(() => FooterButton(host, "Cancel").Click());

        cut.WaitForAssertion(() => Assert.Empty(Services.GetRequiredService<AppDialogService>().Dialogs));
        Assert.DoesNotContain("empty", _calls);
        Assert.Equal(0, changed);
        Assert.Empty(Services.GetRequiredService<AppToastService>().Toasts);
    }

    [Fact]
    public async Task Header_AnUnconfirmedAnswerStillSendsThePageToLookAtTrashAgainWhenItChanged()
    {
        await UseAsync("desktop", touch: false);
        _empty = () => Task.FromResult(ViewTrashOutcome<ViewTrashEmptyResultDto>.Failed(ViewTrashOutcomeStatus.Unconfirmed));
        var host = Render<AppDialogHost>();
        var changed = 0;
        var cut = RenderHeader(changed: () => changed++);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".view-trash-header button")));

        await cut.InvokeAsync(() => cut.Find(".view-trash-header button").Click());
        host.WaitForElement(".empty-trash__lead");
        await host.InvokeAsync(() => FooterButton(host, "Delete 128 items from disk").Click());
        host.WaitForAssertion(() => Assert.Contains("didn't answer in time", host.Markup, StringComparison.Ordinal));
        _summary = () => Task.FromResult(ViewTrashOutcome<ViewTrashSummaryDto>.Ok(Summary(0)));
        await host.InvokeAsync(() => FooterButton(host, "Close").Click());

        cut.WaitForAssertion(() => Assert.Equal(1, changed));
        Assert.Empty(Services.GetRequiredService<AppToastService>().Toasts);
    }

    // -- The dialog -----------------------------------------------------------------------------

    private async Task<(IRenderedComponent<AppDialogHost> Host, IAppDialogReference Dialog)> OpenAsync()
    {
        await UseAsync("desktop", touch: false);
        var host = Render<AppDialogHost>();
        var dialog = await host.InvokeAsync(() => Services.GetRequiredService<IAppDialogService>().ShowAsync<EmptyTrashDialog>(
            string.Empty,
            new AppDialogParameters<EmptyTrashDialog>(),
            new AppDialogOptions { MaxWidth = AppMaxWidth.Medium, FullWidth = true, NoHeader = true, CloseButton = false, CloseOnEscapeKey = true }));
        return (host, dialog);
    }

    private static string Body(IRenderedComponent<AppDialogHost> host) =>
        Regex.Replace(host.Find(".empty-trash").TextContent, "\\s+", " ").Trim();

    private static IElement FooterButton(IRenderedComponent<AppDialogHost> host, string label) =>
        host.FindAll("footer button").Single(button => button.TextContent.Trim() == label);

    private static IRenderedComponent<AppDialogHost> Ready(IRenderedComponent<AppDialogHost> host)
    {
        host.WaitForElement(".empty-trash__lead");
        return host;
    }

    [Fact]
    public async Task Dialog_ShowsCheckingWhatsInTrashWhileTheSummaryLoads()
    {
        var gate = new TaskCompletionSource<ViewTrashOutcome<ViewTrashEmptyPreviewDto>>();
        _preview = () => gate.Task;

        var (host, _) = await OpenAsync();

        host.WaitForAssertion(() => Assert.Contains("Checking what's in Trash…", host.Markup, StringComparison.Ordinal));
        Assert.Equal("Empty Trash?", host.Find(".app-dialog-shell__title").TextContent.Trim());
        Assert.DoesNotContain(host.FindAll("footer button"), button => button.TextContent.Contains("Delete"));

        gate.SetResult(Ok(Preview()));
        Ready(host);
        Assert.DoesNotContain("Checking what's in Trash", host.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dialog_SaysHowManyItemsWillGoByKindAndHowMuchSpaceComesBack()
    {
        var (host, _) = await OpenAsync();
        Ready(host);

        Assert.Contains("128 items will be deleted from your disk for good: 112 photos, 9 videos, 7 documents · 4.2 GB", Body(host), StringComparison.Ordinal);
        Assert.Equal("Empty Trash?", host.Find(".app-dialog-shell__title").TextContent.Trim());
        Assert.Contains("Photos and documents deleted from your disk can't be recovered. Library has no other copy.", Body(host), StringComparison.Ordinal);
        Assert.DoesNotContain("Tuvima", host.Markup, StringComparison.Ordinal);
        Assert.Equal("Delete 128 items from disk", FooterButton(host, "Delete 128 items from disk").TextContent.Trim());
    }

    [Fact]
    public async Task Dialog_LeavesOutKindsWithNothingInThem()
    {
        _preview = () => Task.FromResult(Ok(Preview(photos: 1, videos: 0, documents: 0, bytes: 2048)));
        var (host, _) = await OpenAsync();
        Ready(host);

        var lead = Regex.Replace(host.Find(".empty-trash__lead").TextContent, "\\s+", " ").Trim();
        Assert.Equal("1 item will be deleted from your disk for good: 1 photo · 2 KB", lead);
        Assert.Empty(host.FindAll("section[aria-label='Galleries']"));
        Assert.Empty(host.FindAll("section[aria-label='Skipped']"));
        Assert.Empty(host.FindAll(".empty-trash__compound"));
    }

    [Fact]
    public async Task Dialog_NamesTheGalleriesTheItemsLeaveAToZ()
    {
        _preview = () => Task.FromResult(Ok(Preview(galleries: [Gallery("Receipts", 4), Gallery("Beach 2025", 9), Gallery("Grandma's 80th", 1)], galleryItems: 14)));
        var (host, _) = await OpenAsync();
        Ready(host);

        var galleries = Regex.Replace(host.Find("section[aria-label='Galleries']").TextContent, "\\s+", " ").Trim();
        Assert.Equal(
            "Galleries 14 of these were in 3 of your Galleries: Beach 2025, Grandma's 80th, Receipts. They'll be taken out for good.",
            galleries);
        Assert.Equal(["Beach 2025", "Grandma's 80th", "Receipts"], host.FindAll("section[aria-label='Galleries'] strong").Select(name => name.TextContent));
    }

    [Fact]
    public async Task Dialog_ASingleGalleryIsNamedPlainly()
    {
        _preview = () => Task.FromResult(Ok(Preview(galleries: [Gallery("Beach 2025", 14)], galleryItems: 14)));
        var (host, _) = await OpenAsync();
        Ready(host);

        Assert.Contains("14 of these were in your Gallery Beach 2025. They'll be taken out for good.",
            Regex.Replace(host.Find("section[aria-label='Galleries']").TextContent, "\\s+", " "), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 1, "1 of these was in your Gallery ")]
    [InlineData(14, 1, "14 of these were in your Gallery ")]
    [InlineData(14, 3, "14 of these were in 3 of your Galleries: ")]
    [InlineData(1, 3, "1 of these was in 3 of your Galleries: ")]
    public void GalleriesLead_CountsDistinctItemsAndUsesSingularForms(int items, int galleries, string expected) =>
        Assert.Equal(expected, EmptyTrashText.GalleriesLead(items, galleries));

    [Fact]
    public async Task Dialog_AnItemInSeveralGalleriesIsCountedOnceNotPerGallery()
    {
        _preview = () => Task.FromResult(Ok(Preview(galleries: [Gallery("A", 2), Gallery("B", 2)], galleryItems: 2)));
        var (host, _) = await OpenAsync();
        Ready(host);

        Assert.Contains("2 of these were in 2 of your Galleries: A, B.",
            Regex.Replace(host.Find("section[aria-label='Galleries']").TextContent, "\\s+", " "), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dialog_AfterFiveGalleriesTheRestAreAndNMoreUntilExpanded()
    {
        _preview = () => Task.FromResult(Ok(Preview(galleries: Enumerable.Range(1, 8).Select(index => Gallery($"Gallery {index}")))));
        var (host, _) = await OpenAsync();
        Ready(host);

        Assert.Equal(["Gallery 1", "Gallery 2", "Gallery 3", "Gallery 4", "Gallery 5"],
            host.FindAll("section[aria-label='Galleries'] p strong").Select(name => name.TextContent));
        var more = host.Find(".empty-trash__more");
        Assert.Equal("and 3 more", more.TextContent.Trim());

        await host.InvokeAsync(() => more.Click());

        Assert.Equal(8, host.FindAll("section[aria-label='Galleries'] p strong").Count);
        Assert.Empty(host.FindAll(".empty-trash__more"));
    }

    [Fact]
    public async Task Dialog_SaysWhichItemsStayInTrashAndWhy()
    {
        _preview = () => Task.FromResult(Ok(Preview(skipped:
        [
            Skipped(ViewTrashOutcomes.SkipContributionPending, 2),
            Skipped(ViewTrashOutcomes.SkipReadOnly, 3),
            Skipped(ViewTrashOutcomes.SkipRefused, 1, "Library isn't allowed to change this file."),
        ])));
        var (host, _) = await OpenAsync();
        Ready(host);

        Assert.Equal(
            [
                "2 items are waiting to be added to the Shared Library. They stay in Trash.",
                "3 items are in a read-only folder. They stay in Trash.",
                "1 item can't be deleted. Library isn't allowed to change this file. It stays in Trash.",
            ],
            host.FindAll(".empty-trash__skipped").Select(line => line.TextContent.Trim()));
    }

    [Fact]
    public async Task Dialog_SaysLivePhotosAndRawSetsGoWithAllTheirFilesOnlyWhenThereAreSome()
    {
        _preview = () => Task.FromResult(Ok(Preview(compound: true)));
        var (host, _) = await OpenAsync();
        Ready(host);

        Assert.Equal("Live Photos and RAW sets count as one item and all their files go together.", host.Find(".empty-trash__compound").TextContent.Trim());
    }

    [Fact]
    public async Task Dialog_WhenEveryItemWouldBeSkippedExplainsWhyAndOffersOnlyClose()
    {
        _preview = () => Task.FromResult(Ok(Preview(photos: 0, videos: 0, documents: 0, bytes: 0, skipped: [AllSkipped(5)])));
        var (host, _) = await OpenAsync();
        Ready(host);

        Assert.Contains("Nothing in Trash can be deleted right now.", Body(host), StringComparison.Ordinal);
        Assert.Contains("5 items are waiting to be added to the Shared Library. They stay in Trash.", Body(host), StringComparison.Ordinal);
        Assert.Equal(["Close"], host.FindAll("footer button").Select(button => button.TextContent.Trim()));
        Assert.DoesNotContain("can't be recovered", Body(host), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dialog_CancelIsTheOneFocusedButtonOnOpenAndWhileLoading()
    {
        var gate = new TaskCompletionSource<ViewTrashOutcome<ViewTrashEmptyPreviewDto>>();
        _preview = () => gate.Task;
        var (host, _) = await OpenAsync();

        Assert.Equal("Cancel", Assert.Single(host.FindAll("[autofocus]")).TextContent.Trim());

        gate.SetResult(Ok(Preview(galleries: Enumerable.Range(1, 8).Select(index => Gallery($"Gallery {index}")))));
        Ready(host);

        // The body holds an "and 3 more" button before the footer; autofocus keeps the focus on Cancel regardless.
        Assert.Equal("Cancel", Assert.Single(host.FindAll("[autofocus]")).TextContent.Trim());
        Assert.Equal(["Cancel", "Delete 128 items from disk"], host.FindAll("footer button").Select(button => button.TextContent.Trim()));
    }

    [Fact]
    public async Task Dialog_EscapeAndCancelBothEndWithoutDeleting()
    {
        var (host, dialog) = await OpenAsync();
        Ready(host);
        var entry = Services.GetRequiredService<AppDialogService>().Dialogs.Single();

        // app-dialog.js reports Escape and a click on the backdrop through the frame's RequestCancel.
        await host.InvokeAsync(() => host.FindComponent<AppDialogFrame>().Instance.RequestCancel());

        Assert.True((await dialog.Result)!.Canceled);
        Assert.True(entry.Options.CloseOnEscapeKey);
        Assert.DoesNotContain("empty", _calls);
    }

    [Fact]
    public async Task Dialog_ConfirmingSendsTheFingerprintItShowedAndClosesWithWhatTheEngineDid()
    {
        var (host, dialog) = await OpenAsync();
        Ready(host);

        await host.InvokeAsync(() => FooterButton(host, "Delete 128 items from disk").Click());

        var result = await dialog.Result;
        Assert.False(result!.Canceled);
        Assert.Equal(128, Assert.IsType<ViewTrashEmptyResultDto>(result.Data).DeletedCount);
        Assert.Equal(["fp-1"], _emptyFingerprints);
    }

    [Fact]
    public async Task Dialog_WhileDeletingShowsAnIndeterminateDeletingAndCannotBeCancelled()
    {
        var gate = new TaskCompletionSource<ViewTrashOutcome<ViewTrashEmptyResultDto>>();
        _empty = () => gate.Task;
        var (host, dialog) = await OpenAsync();
        Ready(host);

        await host.InvokeAsync(() => FooterButton(host, "Delete 128 items from disk").Click());

        host.WaitForAssertion(() => Assert.Contains("Deleting…", host.Find(".empty-trash__progress").TextContent, StringComparison.Ordinal));
        Assert.Single(host.FindAll(".empty-trash__progress [role=progressbar]"));
        Assert.DoesNotMatch("\\d+ of \\d+", host.Find(".empty-trash__progress").TextContent);
        Assert.True(FooterButton(host, "Deleting…").HasAttribute("disabled"));
        Assert.True(FooterButton(host, "Cancel").HasAttribute("disabled"));

        // Escape and the backdrop are blocked while the Engine is working.
        await host.InvokeAsync(() => host.FindComponent<AppDialogFrame>().Instance.RequestCancel());
        Assert.False(dialog.Result.IsCompleted);

        gate.SetResult(ViewTrashOutcome<ViewTrashEmptyResultDto>.Ok(new ViewTrashEmptyResultDto(128, 0, 0, [])));
        Assert.False((await dialog.Result)!.Canceled);
    }

    [Fact]
    public async Task Dialog_WhenSomethingChangedShowsTheUpdatedListAndDeletesNothing()
    {
        _empty = () => Task.FromResult(ViewTrashOutcome<ViewTrashEmptyResultDto>.Conflict(
            Preview(photos: 100, videos: 0, documents: 7, bytes: 1024, fingerprint: "fp-2"), "Trash changed."));
        var (host, dialog) = await OpenAsync();
        Ready(host);

        await host.InvokeAsync(() => FooterButton(host, "Delete 128 items from disk").Click());

        host.WaitForAssertion(() => Assert.Contains("Something changed since you opened this. Here's the updated list.", Body(host), StringComparison.Ordinal));
        Assert.Contains("107 items will be deleted from your disk for good: 100 photos, 7 documents · 1 KB", Body(host), StringComparison.Ordinal);
        Assert.False(dialog.Result.IsCompleted);
        Assert.Equal(1, _previewCalls);

        // Confirming again sends the new summary's fingerprint.
        _empty = () => Task.FromResult(ViewTrashOutcome<ViewTrashEmptyResultDto>.Ok(new ViewTrashEmptyResultDto(107, 1024, 0, [])));
        await host.InvokeAsync(() => FooterButton(host, "Delete 107 items from disk").Click());
        await dialog.Result;
        Assert.Equal(["fp-1", "fp-2"], _emptyFingerprints);
    }

    [Fact]
    public async Task Dialog_WhenTheEngineCantBeReachedSaysNothingWasDeletedAndOffersTryAgain()
    {
        _empty = () => Task.FromResult(ViewTrashOutcome<ViewTrashEmptyResultDto>.Failed(ViewTrashOutcomeStatus.Unavailable));
        var (host, _) = await OpenAsync();
        Ready(host);

        await host.InvokeAsync(() => FooterButton(host, "Delete 128 items from disk").Click());

        host.WaitForAssertion(() => Assert.Contains("Couldn't reach the Engine. Nothing was deleted.", Body(host), StringComparison.Ordinal));
        Assert.Equal(["Close", "Try again"], host.FindAll("footer button").Select(button => button.TextContent.Trim()));

        // Try again asks for a fresh summary, so the person confirms what is true now.
        await host.InvokeAsync(() => FooterButton(host, "Try again").Click());
        Ready(host);
        Assert.Equal(2, _previewCalls);
    }

    [Fact]
    public async Task Dialog_WhenThePreviewCantBeLoadedOffersTryAgain()
    {
        _preview = () => Task.FromResult(ViewTrashOutcome<ViewTrashEmptyPreviewDto>.Failed(ViewTrashOutcomeStatus.Unavailable));
        var (host, _) = await OpenAsync();

        host.WaitForAssertion(() => Assert.Contains("Couldn't reach the Engine. Nothing was deleted.", Body(host), StringComparison.Ordinal));
        Assert.Equal(["Close", "Try again"], host.FindAll("footer button").Select(button => button.TextContent.Trim()));
    }

    [Fact]
    public async Task Dialog_WhenTheAnswerNeverComesDoesNotClaimNothingWasDeletedAndOffersNoRetry()
    {
        _empty = () => Task.FromResult(ViewTrashOutcome<ViewTrashEmptyResultDto>.Failed(ViewTrashOutcomeStatus.Unconfirmed));
        var (host, _) = await OpenAsync();
        Ready(host);

        await host.InvokeAsync(() => FooterButton(host, "Delete 128 items from disk").Click());

        host.WaitForAssertion(() => Assert.Contains("The Engine didn't answer in time. Check your Trash before trying again.", Body(host), StringComparison.Ordinal));
        Assert.DoesNotContain("Nothing was deleted", host.Markup, StringComparison.Ordinal);
        Assert.Equal(["Close"], host.FindAll("footer button").Select(button => button.TextContent.Trim()));
    }

    [Fact]
    public async Task Dialog_WhenNotAllowedSaysSoPlainly()
    {
        _preview = () => Task.FromResult(ViewTrashOutcome<ViewTrashEmptyPreviewDto>.Failed(ViewTrashOutcomeStatus.Forbidden));
        var (host, _) = await OpenAsync();

        host.WaitForAssertion(() => Assert.Contains("You can't empty this Trash.", Body(host), StringComparison.Ordinal));
        Assert.Equal(["Close"], host.FindAll("footer button").Select(button => button.TextContent.Trim()));
    }

    // -- Photos: read-only items and the Trash view ---------------------------------------------

    private static string PhotoJson(Guid id, string name, bool canTrash = true) =>
        "{\"id\":\"" + id + "\",\"library_id\":\"" + LibraryId + "\",\"media_kind\":\"image\",\"title\":\"" + name
        + "\",\"file_name\":\"" + name + ".jpg\",\"mime_type\":\"image/jpeg\",\"created_at\":\"2026-10-01T10:00:00+00:00\",\"captured_at\":\"2026-10-01T10:00:00+00:00\","
        + "\"width\":1600,\"height\":1200,\"source_count\":1,\"files\":[],\"tags\":[],\"thumbnail_url\":\"/t\",\"content_url\":\"/c\",\"favorite\":false,\"hidden\":false,"
        + "\"can_trash\":" + (canTrash ? "true" : "false") + "}";

    private static LocalAssetDto Photo(string name, bool canTrash = true) => JsonSerializer.Deserialize<LocalAssetDto>(PhotoJson(Guid.NewGuid(), name, canTrash))!;

    private PhotosHandler RegisterPhotosPage(params LocalAssetDto[] photos)
    {
        var handler = new PhotosHandler(photos);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance);
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddScoped<ActiveProfileAccessor>();
        var session = new ActiveProfileSessionService(JSInterop.JSRuntime, api);
        Services.AddSingleton(session);
        Services.AddSingleton(new UIOrchestratorService(api, new UniverseStateContainer(), session, new ConfigurationBuilder().Build(), NullLogger<UIOrchestratorService>.Instance));
        Services.AddSingleton<ViewAssetDragService>();
        Services.AddSingleton(new ViewMediaGrantService(new byte[32], TimeSpan.FromMinutes(1)));
        return handler;
    }

    private static Task SelectAsync(IRenderedComponent<ViewPage> cut, int index) =>
        cut.InvokeAsync(() => cut.FindAll(".view-tile__select-control")[index].Click());

    private static Task ClickToolbarAsync(IRenderedComponent<ViewPage> cut, string label) =>
        cut.InvokeAsync(() => cut.FindAll(".view-selection-toolbar__action").Single(button => button.TextContent.Trim() == label).Click());

    private static Task OpenTrashFilterAsync(IRenderedComponent<ViewPage> cut) =>
        cut.InvokeAsync(() =>
        {
            cut.Find("button[aria-label='More photo filters']").Click();
            cut.FindAll(".app-discovery-filter__tab").Single(button => button.TextContent.Trim() == "Trash").Click();
        });

    [Fact]
    public async Task Photos_AMixedSelectionTrashesTheOthersAndSaysHowManyAreInAReadOnlyFolder()
    {
        await UseAsync("desktop", touch: false);
        var photos = new[] { Photo("one"), Photo("two", canTrash: false), Photo("three"), Photo("four", canTrash: false) };
        var handler = RegisterPhotosPage(photos);
        var cut = Render<ViewPage>();
        cut.WaitForAssertion(() => Assert.Equal(4, cut.FindAll("article.view-tile").Count));
        for (var index = 0; index < 4; index++)
        {
            await SelectAsync(cut, index);
        }

        await ClickToolbarAsync(cut, "Trash");

        cut.WaitForAssertion(() => Assert.Equal(2, handler.Trashed.Count));
        Assert.Equal(photos.Where(photo => photo.CanTrash).Select(photo => photo.Id).Order(), handler.Trashed.Order());
        var messages = Services.GetRequiredService<AppToastService>().Toasts.Select(toast => toast.Message).ToList();
        Assert.Contains("Moved selected items to Trash.", messages);
        Assert.Contains("2 items are in a read-only folder and weren't moved to Trash.", messages);
    }

    [Fact]
    public async Task Photos_WhenEveryPickedItemIsReadOnlyThereIsNoTrashButtonAndNothingIsSent()
    {
        await UseAsync("desktop", touch: false);
        var handler = RegisterPhotosPage(Photo("one", canTrash: false), Photo("two", canTrash: false), Photo("three"));
        var cut = Render<ViewPage>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("article.view-tile").Count));

        await SelectAsync(cut, 0);
        await SelectAsync(cut, 1);

        var actions = cut.FindAll(".view-selection-toolbar__action").Select(button => button.TextContent.Trim()).ToList();
        Assert.DoesNotContain("Trash", actions);
        Assert.Contains("Archive", actions);
        Assert.Contains("Favorite", actions);
        Assert.Empty(handler.Trashed);

        // Adding a photo that can be trashed brings the button back.
        await SelectAsync(cut, 2);
        Assert.Contains("Trash", cut.FindAll(".view-selection-toolbar__action").Select(button => button.TextContent.Trim()));
    }

    [Fact]
    public async Task Photos_TheViewerOffersNoMoveToTrashForAnItemInAReadOnlyFolderButKeepsRestore()
    {
        await UseAsync("desktop", touch: false);
        Services.AddSingleton<IUserPlaybackPreferencesAccessor, NoPlaybackPreferences>();
        Services.AddSingleton(provider => new PlaybackSessionController(
            null!, null!, preferences: provider.GetRequiredService<IUserPlaybackPreferencesAccessor>()));

        IRenderedComponent<ViewImmersiveViewer> Viewer(LocalAssetDto item) => Render<ViewImmersiveViewer>(parameters => parameters
            .Add(viewer => viewer.Item, item)
            .Add(viewer => viewer.CanManage, true)
            .Add(viewer => viewer.CanEdit, true)
            .Add(viewer => viewer.ContentUrl, "/preview")
            .Add(viewer => viewer.OriginalUrl, "/original")
            .Add(viewer => viewer.ThumbnailUrl, "/thumb"));

        var managed = Viewer(Photo("managed"));
        Assert.Single(managed.FindAll("button[aria-label='Move item to Trash']"));

        var readOnly = Viewer(Photo("linked", canTrash: false));
        Assert.Empty(readOnly.FindAll("button[aria-label='Move item to Trash']"));
        Assert.Single(readOnly.FindAll("button[aria-label='Archive item']"));

        var trashedReadOnly = Viewer(JsonSerializer.Deserialize<LocalAssetDto>(
            PhotoJson(Guid.NewGuid(), "linked", canTrash: false).Replace("\"hidden\":false", "\"hidden\":false,\"trashed_at\":\"2026-10-02T10:00:00+00:00\""))!);
        Assert.Empty(trashedReadOnly.FindAll("button[aria-label='Move item to Trash']"));
        Assert.Single(trashedReadOnly.FindAll("button[aria-label='Restore item']"));
    }

    [Fact]
    public async Task Photos_TheTrashFilterShowsTheHeaderAndTheNoteOnATileThatCouldNotBeDeleted()
    {
        await UseAsync("desktop", touch: false);
        var photos = new[] { Photo("one"), Photo("two") };
        RegisterPhotosPage(photos);
        Services.GetRequiredService<ViewWorkspaceService>().ReplaceTrashFailures(
            [new ViewTrashEmptyFailureDto(photos[1].Id, "Couldn't delete: the file is in use.")]);
        var cut = Render<ViewPage>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("article.view-tile").Count));
        Assert.Empty(cut.FindAll(".view-trash-header"));
        Assert.Empty(cut.FindAll(".view-tile__note"));

        await OpenTrashFilterAsync(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".view-trash-header")));
        Assert.Contains("128 items · 4.2 GB", cut.Find(".view-trash-header").TextContent, StringComparison.Ordinal);
        Assert.Contains("Empty Trash…", cut.Find(".view-trash-header").TextContent, StringComparison.Ordinal);
        Assert.Equal("Couldn't delete: the file is in use.", Assert.Single(cut.FindAll(".view-tile__note")).TextContent.Trim());
    }

    private sealed class NoPlaybackPreferences : IUserPlaybackPreferencesAccessor
    {
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(null);
        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }

    private sealed class PhotosHandler(IReadOnlyList<LocalAssetDto> photos) : HttpMessageHandler
    {
        public List<Guid> Trashed { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/trash", StringComparison.Ordinal) && path.StartsWith("/view/items/", StringComparison.Ordinal))
            {
                var id = Guid.Parse(path["/view/items/".Length..^"/trash".Length]);
                lock (Trashed)
                {
                    Trashed.Add(id);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            var json = path switch
            {
                "/profiles" => "[]",
                "/view/preferences" => "{\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"scope\":1,\"scope_profile_id\":null,\"timeline_density\":1,\"viewer_info_open\":true,\"updated_at\":null}",
                "/view/scopes" => "{\"scope\":{\"kind\":1,\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"was_fallback\":false},\"available_scopes\":[{\"kind\":1,\"profile_id\":\"00000000-0000-0000-0000-000000000001\",\"label\":\"Mine\",\"avatar_color\":null,\"avatar_url\":null}],\"can_browse_other_people\":false,\"can_view_documents\":true}",
                "/view/assets" => JsonSerializer.Serialize(new ViewAssetTimelinePageDto(photos, null, false)),
                "/view/assets/timeline-index" => "{\"buckets\":[]}",
                "/view/trash/summary" => JsonSerializer.Serialize(Summary(128)),
                "/view/galleries" => "{\"owned\":[],\"shared_with_you\":[]}",
                _ => "{\"owned\":[],\"shared_with_you\":[]}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
