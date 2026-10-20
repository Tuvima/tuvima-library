using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using MediaEngine.Contracts.Items;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Delete from library confirmation: what it shows for the Engine's preview, which button it offers, and how it answers each
/// outcome of the delete. The Engine is a hand-written stub; nothing is deleted anywhere.
/// </summary>
public sealed class DeleteFromLibraryDialogTests : AsyncBunitContext
{
    private const long SixtyOneGb = 65_926_043_034; // 61.4 GB in binary units, as the Dashboard formats sizes

    private static readonly Guid WorkId = Guid.Parse("11111111-aaaa-bbbb-cccc-000000000001");

    private Func<Task<LibraryItemDeletionOutcome<LibraryItemDeletionPreview>>> _preview;
    private Func<Task<LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>>> _delete;
    private readonly List<(IReadOnlyList<Guid> Ids, string Fingerprint)> _deleteCalls = [];
    private int _previewCalls;

    public DeleteFromLibraryDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _preview = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(Movie()));
        _delete = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>.Ok(new LibraryItemDeletionResultDto()));
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryItemDeletionPreviewAsync), _ =>
            {
                _previewCalls++;
                return _preview();
            });
            stub.SetHandler(nameof(IEngineApiClient.DeleteLibraryItemsAsync), args =>
            {
                _deleteCalls.Add(((IReadOnlyList<Guid>)args![0]!, (string)args[1]!));
                return _delete();
            });
        }));
    }

    // -- Preview builders ----------------------------------------------------------------------

    private static LibraryItemDeletionPreview Movie(
        string fingerprint = "fp-1",
        int progress = 0,
        bool processing = false,
        IEnumerable<LibraryItemDeletionFileDto>? files = null,
        IEnumerable<LibraryItemDeletionCollectionDto>? collections = null) => new()
        {
            Items = [new() { WorkId = WorkId, Title = "Dune: Part One", MediaType = "Movies", FileCount = 2 }],
            Files = files?.ToList() ??
        [
            File("Movies › Dune Part One (2021)", "Dune.mkv", LibraryItemDeletionOutcomes.Delete, 60L * 1024 * 1024 * 1024),
            File("Movies › Dune Part One (2021)", "Dune.en.srt", LibraryItemDeletionOutcomes.Delete, SixtyOneGb - 60L * 1024 * 1024 * 1024, sidecar: true),
        ],
            CuratedCollections = collections?.ToList() ?? [],
            ProfilesWithProgress = progress,
            ProcessingRunning = processing,
            BytesFreed = files is null ? SixtyOneGb : files.Where(file => file.Outcome == LibraryItemDeletionOutcomes.Delete).Sum(file => file.SizeBytes ?? 0),
            Fingerprint = fingerprint,
        };

    private static LibraryItemDeletionFileDto File(string folder, string name, string outcome, long? size = 1024, string? reason = null, bool sidecar = false) => new()
    {
        WorkId = WorkId,
        AssetId = sidecar ? null : Guid.NewGuid(),
        FolderLabel = folder,
        FileName = name,
        SizeBytes = size,
        Outcome = outcome,
        Reason = reason,
        IsSidecar = sidecar,
    };

    private static LibraryItemDeletionCollectionDto Collection(string name, int inRequest = 1, bool deleted = false) =>
        new() { CollectionId = Guid.NewGuid(), Name = name, ItemsInRequest = inRequest, WillBeDeleted = deleted };

    // -- Opening and reading the dialog --------------------------------------------------------

    private async Task<(IRenderedComponent<AppDialogHost> Host, IAppDialogReference Dialog)> OpenAsync(
        IReadOnlyList<Guid>? ids = null,
        string? title = "Dune: Part One")
    {
        var host = Render<AppDialogHost>();
        var dialog = await host.InvokeAsync(() => Services.GetRequiredService<IAppDialogService>().ShowAsync<DeleteFromLibraryDialog>(
            string.Empty,
            new AppDialogParameters<DeleteFromLibraryDialog>
            {
                { component => component.WorkIds, ids ?? [WorkId] },
                { component => component.ItemTitle, title },
            },
            new AppDialogOptions { MaxWidth = AppMaxWidth.Medium, FullWidth = true, NoHeader = true, CloseButton = false }));
        return (host, dialog);
    }

    private static string Body(IRenderedComponent<AppDialogHost> host) =>
        Regex.Replace(host.Find(".delete-library").TextContent, "\\s+", " ").Trim();

    private static string Heading(IRenderedComponent<AppDialogHost> host) =>
        host.Find(".app-dialog-shell__title").TextContent.Trim();

    private static IElement Button(IRenderedComponent<AppDialogHost> host, string label) =>
        host.FindAll("footer button").Single(button => button.TextContent.Trim() == label);

    private static IRenderedComponent<AppDialogHost> Ready(IRenderedComponent<AppDialogHost> host)
    {
        host.WaitForElement(".delete-library__section");
        return host;
    }

    // -- Loading --------------------------------------------------------------------------------

    [Fact]
    public async Task Opening_ShowsCheckingWhatWillBeDeletedWhileThePreviewLoads()
    {
        var gate = new TaskCompletionSource<LibraryItemDeletionOutcome<LibraryItemDeletionPreview>>();
        _preview = () => gate.Task;

        var (host, _) = await OpenAsync();

        host.WaitForAssertion(() => Assert.Contains("Checking what will be deleted…", host.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(host.FindAll("footer button"), button => button.TextContent.Contains("Delete from library"));
        Assert.Equal("Delete \"Dune: Part One\" from your library?", Heading(host));

        gate.SetResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(Movie()));
        Ready(host);
        Assert.DoesNotContain("Checking what will be deleted", host.Markup, StringComparison.Ordinal);
    }

    // -- Files ----------------------------------------------------------------------------------

    [Fact]
    public async Task Files_AreGroupedByFolderWithCountSizeAndWhatHappensToEach()
    {
        var files = new[]
        {
            File("Movies › Dune Part One (2021)", "Dune.mkv", LibraryItemDeletionOutcomes.Delete, 60L * 1024 * 1024 * 1024),
            File("Movies › Dune Part One (2021)", "Dune.en.srt", LibraryItemDeletionOutcomes.Delete, 1_400_000_000, sidecar: true),
            File("Old Rips", "Dune.2021.mkv", LibraryItemDeletionOutcomes.KeepAsIs),
            File("Imports", "Dune.mkv", LibraryItemDeletionOutcomes.KeepProtected, reason: "kept as the original of a merge"),
            File("Backups", "Dune.bak.mkv", LibraryItemDeletionOutcomes.KeepMissing, size: null),
        };
        _preview = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(Movie(files: files)));

        var (host, _) = await OpenAsync();
        Ready(host);

        var rows = host.FindAll(".delete-library__file").Select(row => Regex.Replace(row.TextContent, "\\s+", " ").Trim()).ToList();
        Assert.Equal(4, rows.Count);
        Assert.StartsWith("Movies › Dune Part One (2021) — 2 files, 61.3 GB — will be deleted from disk", rows[0], StringComparison.Ordinal);
        Assert.Equal("Old Rips › Dune.2021.mkv — stays on disk: Tuvima only reads this folder", rows[1]);
        Assert.Equal("Imports › Dune.mkv — stays on disk: kept as the original of a merge", rows[2]);
        Assert.Equal("Backups › Dune.bak.mkv — already gone from disk", rows[3]);
    }

    [Fact]
    public async Task SeveralSongsInOneFolder_ReadAsOneRowWithTotals()
    {
        var folder = "Music › Artist › Album";
        var files = Enumerable.Range(1, 4)
            .Select(index => File(folder, $"{index:00}.flac", LibraryItemDeletionOutcomes.Delete, 100L * 1024 * 1024))
            .ToArray();
        _preview = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(Movie(files: files)));

        var (host, _) = await OpenAsync();
        Ready(host);

        var row = Assert.Single(host.FindAll(".delete-library__file"));
        Assert.Equal("Music › Artist › Album — 4 files, 400 MB — will be deleted from disk", Regex.Replace(row.TextContent, "\\s+", " ").Trim());
    }

    // -- Hand-picked collections ----------------------------------------------------------------

    [Fact]
    public async Task Collections_AreSortedAToZIgnoringCaseAndAfterFiveAndNMoreExpandsTheRest()
    {
        var collections = new[] { "road trip", "Zebra", "Comfort Films", "alpha", "Movie Night", "Éclair", "Weekend" }
            .Select(name => Collection(name)).ToArray();
        _preview = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(Movie(collections: collections)));

        var (host, _) = await OpenAsync();
        Ready(host);

        var section = host.Find("section[aria-label='Hand-picked collections']");
        Assert.Equal(["alpha", "Comfort Films", "Éclair", "Movie Night", "road trip"], section.QuerySelectorAll("p:first-of-type strong").Select(name => name.TextContent));
        Assert.StartsWith("This is in 7 collections you made:", Regex.Replace(section.QuerySelector("p")!.TextContent, "\\s+", " ").Trim(), StringComparison.Ordinal);
        Assert.EndsWith("and 2 more. It will be taken out of them.", Regex.Replace(section.QuerySelector("p")!.TextContent, "\\s+", " ").Trim(), StringComparison.Ordinal);

        host.Find(".delete-library__more").Click();

        host.WaitForAssertion(() =>
        {
            var expanded = host.Find("section[aria-label='Hand-picked collections']");
            Assert.Equal(["alpha", "Comfort Films", "Éclair", "Movie Night", "road trip", "Weekend", "Zebra"],
                expanded.QuerySelectorAll("p:first-of-type strong").Select(name => name.TextContent));
            Assert.Empty(host.FindAll(".delete-library__more"));
        });
    }

    [Fact]
    public async Task ACollectionLeftEmpty_IsNamedAsDeletedTooInTheSingularAndPlural()
    {
        _preview = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(
            Movie(collections: [Collection("Comfort Films"), Collection("Road Trip", deleted: true)])));
        var (host, _) = await OpenAsync();
        Ready(host);

        Assert.Contains("This is in 2 collections you made: Comfort Films, Road Trip. It will be taken out of them.", Body(host), StringComparison.Ordinal);
        Assert.Contains("Road Trip has nothing else in it, so it will be deleted too.", Body(host), StringComparison.Ordinal);
        Assert.Equal(["Road Trip"], host.FindAll(".delete-library__emptied strong").Select(name => name.TextContent));
    }

    [Fact]
    public async Task SeveralCollectionsLeftEmpty_AreNamedTogether()
    {
        _preview = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(
            Movie(collections: [Collection("Road Trip", deleted: true), Collection("Date Night", deleted: true), Collection("Keepers")])));
        var (host, _) = await OpenAsync();
        Ready(host);

        Assert.Contains("Date Night and Road Trip have nothing else in them, so they will be deleted too.", Body(host), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutHandPickedCollections_TheSectionIsLeftOut()
    {
        var (host, _) = await OpenAsync();
        Ready(host);

        Assert.Empty(host.FindAll("section[aria-label='Hand-picked collections']"));
        Assert.DoesNotContain("collection", Body(host), StringComparison.OrdinalIgnoreCase);
    }

    // -- Progress and processing ----------------------------------------------------------------

    [Theory]
    [InlineData(1, "1 person has progress on this. It will be removed.")]
    [InlineData(3, "3 people have progress on this. It will be removed.")]
    public async Task Progress_SaysHowManyPeopleHaveSomeAndThatItWillBeRemoved(int people, string sentence)
    {
        _preview = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(Movie(progress: people)));
        var (host, _) = await OpenAsync();
        Ready(host);

        Assert.Contains(sentence, Body(host), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoProgress_TheProgressSectionIsLeftOut()
    {
        var (host, _) = await OpenAsync();
        Ready(host);

        Assert.Empty(host.FindAll("section[aria-label='Progress']"));
    }

    [Fact]
    public async Task WhenProcessingIsRunning_TheDialogSaysItWillBeStopped()
    {
        _preview = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(Movie(processing: true)));
        var (host, _) = await OpenAsync();
        Ready(host);

        Assert.Contains("Processing for this title will be stopped.", Body(host), StringComparison.Ordinal);
    }

    // -- The button and the "can't be recovered" line ----------------------------------------------

    [Fact]
    public async Task WhenAFileWillBeDeleted_TheButtonSaysAndDiskAndTheDialogWarnsItCannotBeRecovered()
    {
        var (host, _) = await OpenAsync();
        Ready(host);

        Assert.NotNull(Button(host, "Delete from library and disk"));
        Assert.DoesNotContain(host.FindAll("footer button"), button => button.TextContent.Trim() == "Delete from library");
        Assert.Contains("Deleting frees 61.4 GB. Files deleted from disk can't be recovered.", Body(host), StringComparison.Ordinal);
        Assert.Contains("app-tone--error", string.Join(' ', Button(host, "Delete from library and disk").ClassList), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenEveryFileStays_TheButtonJustSaysDeleteFromLibraryAndThereIsNoRecoveryWarning()
    {
        var files = new[]
        {
            File("Old Rips", "Dune.2021.mkv", LibraryItemDeletionOutcomes.KeepAsIs),
            File("Imports", "Dune.mkv", LibraryItemDeletionOutcomes.KeepProtected, reason: "kept as the original of a merge"),
        };
        _preview = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(Movie(files: files)));
        var (host, _) = await OpenAsync();
        Ready(host);

        Assert.NotNull(Button(host, "Delete from library"));
        Assert.DoesNotContain(host.FindAll("footer button"), button => button.TextContent.Contains("and disk"));
        Assert.DoesNotContain("can't be recovered", Body(host), StringComparison.Ordinal);
        Assert.DoesNotContain("frees", Body(host), StringComparison.Ordinal);
        Assert.Contains("will skip them in future scans", Body(host), StringComparison.Ordinal);
    }

    // -- Focus ---------------------------------------------------------------------------------

    [Fact]
    public async Task Cancel_IsTheFirstButtonAndTheOneTheBrowserFocusesWhenTheDialogOpens()
    {
        _preview = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(
            Movie(collections: Enumerable.Range(1, 8).Select(index => Collection($"Collection {index}")))));
        var (host, _) = await OpenAsync();
        Ready(host);

        // The body holds an "and 3 more" button before the footer, so the native dialog's default (first focusable) would pick it.
        // autofocus is what makes the dialog focus Cancel instead; there is exactly one.
        var autofocus = Assert.Single(host.FindAll("[autofocus]"));
        Assert.Equal("Cancel", autofocus.TextContent.Trim());
        Assert.Equal("Cancel", host.FindAll("footer button").First().TextContent.Trim());
    }

    [Fact]
    public async Task Cancel_IsFocusedWhileThePreviewIsStillLoadingToo()
    {
        var gate = new TaskCompletionSource<LibraryItemDeletionOutcome<LibraryItemDeletionPreview>>();
        _preview = () => gate.Task;
        var (host, _) = await OpenAsync();

        Assert.Equal("Cancel", Assert.Single(host.FindAll("[autofocus]")).TextContent.Trim());
        gate.SetResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(Movie()));
    }

    [Fact]
    public async Task Cancel_ClosesTheDialogWithoutDeleting()
    {
        var (host, dialog) = await OpenAsync();
        Ready(host);

        Button(host, "Cancel").Click();

        var result = await dialog.Result;
        Assert.True(result!.Canceled);
        Assert.Empty(_deleteCalls);
    }

    // -- Confirming ----------------------------------------------------------------------------

    [Fact]
    public async Task Confirming_ShowsDeletingWithBothButtonsDisabledThenClosesWithWhatWasDeleted()
    {
        var gate = new TaskCompletionSource<LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>>();
        _delete = () => gate.Task;
        var (host, dialog) = await OpenAsync();
        Ready(host);

        Button(host, "Delete from library and disk").Click();

        host.WaitForAssertion(() =>
        {
            Assert.True(Button(host, "Deleting…").HasAttribute("disabled"));
            Assert.True(Button(host, "Cancel").HasAttribute("disabled"));
        });
        Assert.False(dialog.Result.IsCompleted);

        var deleted = new LibraryItemDeletionResultDto { DeletedWorkIds = [WorkId], Titles = ["Dune: Part One"], FilesDeleted = 2, BytesFreed = SixtyOneGb };
        gate.SetResult(LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>.Ok(deleted));

        var result = await dialog.Result;
        var answer = Assert.IsType<LibraryItemDeletionDialogResult>(result!.Data);
        Assert.Same(deleted, answer.Deleted);
        Assert.False(answer.NoLongerInLibrary);
        var call = Assert.Single(_deleteCalls);
        Assert.Equal([WorkId], call.Ids);
        Assert.Equal("fp-1", call.Fingerprint);
    }

    [Fact]
    public async Task WhenSomethingChanged_TheDialogShowsTheFreshPreviewAndTheMessageAndDeletesNothing()
    {
        var fresh = Movie(fingerprint: "fp-2", collections: [Collection("Road Trip", deleted: true)]);
        var answers = new Queue<LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>>(
        [
            LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>.Conflict(fresh, "changed"),
            LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>.Ok(new LibraryItemDeletionResultDto { DeletedWorkIds = [WorkId], Titles = ["Dune: Part One"] }),
        ]);
        _delete = () => Task.FromResult(answers.Dequeue());
        var (host, dialog) = await OpenAsync();
        Ready(host);

        Button(host, "Delete from library and disk").Click();

        host.WaitForAssertion(() => Assert.Contains("Something changed since you opened this. Here's the updated list.", Body(host), StringComparison.Ordinal));
        Assert.Contains("Road Trip has nothing else in it, so it will be deleted too.", Body(host), StringComparison.Ordinal);
        Assert.False(dialog.Result.IsCompleted);

        // Confirming again sends the new plan's fingerprint, not the one the person first saw.
        Button(host, "Delete from library and disk").Click();
        await dialog.Result;
        Assert.Equal(["fp-1", "fp-2"], _deleteCalls.Select(call => call.Fingerprint));
    }

    [Fact]
    public async Task WhenTheEngineCannotBeReachedForThePreview_TheDialogSaysNothingWasDeletedAndTryAgainAsksAgain()
    {
        var up = false;
        _preview = () => Task.FromResult(up
            ? LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(Movie())
            : LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Failed(LibraryItemDeletionOutcomeStatus.Unavailable));
        var (host, _) = await OpenAsync();

        host.WaitForAssertion(() => Assert.Contains("Couldn't reach the Engine. Nothing was deleted.", host.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(host.FindAll("footer button"), button => button.TextContent.Contains("Delete from library"));

        up = true;
        Button(host, "Try again").Click();

        Ready(host);
        Assert.Equal(2, _previewCalls);
        Assert.NotNull(Button(host, "Delete from library and disk"));
    }

    [Fact]
    public async Task WhenTheEngineCannotBeReachedForTheDelete_TheDialogSaysNothingWasDeletedAndOffersTryAgain()
    {
        _delete = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>.Failed(LibraryItemDeletionOutcomeStatus.Unavailable));
        var (host, dialog) = await OpenAsync();
        Ready(host);

        Button(host, "Delete from library and disk").Click();

        host.WaitForAssertion(() => Assert.Contains("Couldn't reach the Engine. Nothing was deleted.", host.Markup, StringComparison.Ordinal));
        Assert.NotNull(Button(host, "Try again"));
        Assert.False(dialog.Result.IsCompleted);
    }

    [Fact]
    public async Task WhenNotAnAdministrator_TheDialogSaysSoAndOffersNoDelete()
    {
        _preview = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Failed(LibraryItemDeletionOutcomeStatus.Forbidden));
        var (host, _) = await OpenAsync();

        host.WaitForAssertion(() => Assert.Contains("You need administrator access to delete items.", host.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(host.FindAll("footer button"), button => button.TextContent.Contains("Delete from library"));
        Assert.DoesNotContain(host.FindAll("footer button"), button => button.TextContent.Contains("Try again"));
    }

    [Fact]
    public async Task WhenTheTitleIsAlreadyGone_TheDialogClosesAndTellsTheCaller()
    {
        _delete = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>.Failed(LibraryItemDeletionOutcomeStatus.NotFound));
        var (host, dialog) = await OpenAsync();
        Ready(host);

        Button(host, "Delete from library and disk").Click();

        var answer = Assert.IsType<LibraryItemDeletionDialogResult>((await dialog.Result)!.Data);
        Assert.True(answer.NoLongerInLibrary);
        Assert.Null(answer.Deleted);
    }

    [Fact]
    public async Task WhenTheEngineRefusesTheRequest_TheDialogShowsTheEnginesMessage()
    {
        _preview = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Failed(
            LibraryItemDeletionOutcomeStatus.Invalid, "Delete a show's episodes one at a time."));
        var (host, _) = await OpenAsync();

        host.WaitForAssertion(() => Assert.Contains("Delete a show's episodes one at a time.", host.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenTheDeleteGotNoAnswer_TheDialogDoesNotClaimNothingWasDeletedAndOffersCloseNotTryAgain()
    {
        _delete = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>.Failed(LibraryItemDeletionOutcomeStatus.Unconfirmed));
        var (host, dialog) = await OpenAsync();
        Ready(host);

        Button(host, "Delete from library and disk").Click();

        host.WaitForAssertion(() => Assert.Contains("The Engine didn't answer in time. Check your library before trying again.", host.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Nothing was deleted", host.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(host.FindAll("footer button"), button => button.TextContent.Trim() is "Try again" or "Delete from library and disk");
        Assert.Equal(1, _previewCalls);

        Button(host, "Close").Click();
        Assert.True((await dialog.Result)!.Canceled);
        Assert.Single(_deleteCalls);
    }

    [Fact]
    public async Task ATitleWithNoFiles_SaysThisTitleHasNoFilesInTheSingular()
    {
        _preview = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(Movie(files: [])));
        var (host, _) = await OpenAsync();
        Ready(host);

        Assert.Contains("This title has no files on disk.", Body(host), StringComparison.Ordinal);
    }

    // -- Several songs -------------------------------------------------------------------------

    [Fact]
    public async Task SeveralSongs_AreNamedByCountAndEachCollectionShowsHowManyOfThemItHolds()
    {
        var ids = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToList();
        _preview = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(new LibraryItemDeletionPreview
        {
            Items = ids.Select((id, index) => new LibraryItemDeletionItemDto { WorkId = id, Title = $"Song {index}", MediaType = "Music", FileCount = 1 }).ToList(),
            Files = [File("Music › Artist › Album", "01.flac", LibraryItemDeletionOutcomes.Delete, 1024)],
            CuratedCollections = [Collection("Road Trip", inRequest: 4)],
            BytesFreed = 1024,
            Fingerprint = "fp-songs",
        }));

        var (host, _) = await OpenAsync(ids, title: null);
        Ready(host);

        Assert.Equal("Delete 12 songs from your library?", Heading(host));
        Assert.Contains("These are in 1 collection you made: Road Trip (4 of these). It will be taken out of it.", Body(host), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeveralTitlesThatAreNotAllMusic_AreCalledTitles()
    {
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        _preview = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(new LibraryItemDeletionPreview
        {
            Items = [new() { WorkId = ids[0], Title = "A", MediaType = "Music" }, new() { WorkId = ids[1], Title = "B", MediaType = "Movies" }, new() { WorkId = ids[2], Title = "C", MediaType = "Books" }],
            Fingerprint = "fp-mixed",
        }));

        var (host, _) = await OpenAsync(ids, title: null);
        Ready(host);

        Assert.Equal("Delete 3 titles from your library?", Heading(host));
    }

    // -- What the caller says afterwards --------------------------------------------------------

    [Fact]
    public void SuccessToast_NamesTheTitleAndHowMuchSpaceCameBack()
    {
        var result = new LibraryItemDeletionResultDto { DeletedWorkIds = [WorkId], Titles = ["Dune: Part One"], BytesFreed = SixtyOneGb };

        Assert.Equal("Deleted \"Dune: Part One\". 61.4 GB freed.", LibraryItemDeletionText.Success(result, "Dune: Part One", "titles"));
    }

    [Fact]
    public void SuccessToast_ForSeveralSongsCountsThemAndLeavesOutSpaceWhenNothingCameBack()
    {
        var ids = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToList();
        var freed = new LibraryItemDeletionResultDto { DeletedWorkIds = ids, Titles = ids.Select(id => id.ToString()).ToList(), BytesFreed = 1_288_490_189 };
        var none = new LibraryItemDeletionResultDto { DeletedWorkIds = ids, Titles = ids.Select(id => id.ToString()).ToList(), BytesFreed = 0 };

        Assert.Equal("Deleted 12 songs. 1.2 GB freed.", LibraryItemDeletionText.Success(freed, null, "songs"));
        Assert.Equal("Deleted 12 songs.", LibraryItemDeletionText.Success(none, null, "songs"));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "1 file couldn't be removed and was left on disk.")]
    [InlineData(3, "3 files couldn't be removed and were left on disk.")]
    public void FailedFilesToast_IsOnlyForFilesThatCouldNotBeRemoved(int failed, string? expected)
    {
        var result = new LibraryItemDeletionResultDto
        {
            FilesFailed = Enumerable.Range(0, failed).Select(index => new LibraryItemDeletionFailedFileDto { FileName = $"{index}.mkv", Reason = "The file is in use" }).ToList(),
        };

        Assert.Equal(expected, LibraryItemDeletionText.FailedFiles(result));
    }

    [Fact]
    public async Task TheFlow_ShowsTheSuccessToastAndAWarningWhenAFileCouldNotBeRemoved()
    {
        var result = new LibraryItemDeletionResultDto
        {
            DeletedWorkIds = [WorkId],
            Titles = ["Dune: Part One"],
            BytesFreed = SixtyOneGb,
            FilesFailed = [new() { FileName = "Dune.mkv", FolderLabel = "Movies", Reason = "The file is in use" }],
        };
        _delete = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>.Ok(result));
        var host = Render<AppDialogHost>();
        var toasts = Services.GetRequiredService<AppToastService>();

        var run = host.InvokeAsync(() => LibraryItemDeletionFlow.RunAsync(
            Services.GetRequiredService<IAppDialogService>(), toasts, [WorkId], "Dune: Part One"));
        Ready(host);
        Button(host, "Delete from library and disk").Click();

        Assert.Equal(LibraryItemDeletionFlowOutcome.Deleted, await run);
        Assert.Equal(
            [("Deleted \"Dune: Part One\". 61.4 GB freed.", AppSeverity.Success), ("1 file couldn't be removed and was left on disk.", AppSeverity.Warning)],
            toasts.Toasts.Select(toast => (toast.Message, toast.Severity)));
    }

    [Fact]
    public async Task TheFlow_TellsTheCallerWhenThePersonCancelledAndShowsNoToast()
    {
        var host = Render<AppDialogHost>();
        var toasts = Services.GetRequiredService<AppToastService>();

        var run = host.InvokeAsync(() => LibraryItemDeletionFlow.RunAsync(
            Services.GetRequiredService<IAppDialogService>(), toasts, [WorkId], "Dune: Part One"));
        Ready(host);
        Button(host, "Cancel").Click();

        Assert.Equal(LibraryItemDeletionFlowOutcome.Canceled, await run);
        Assert.Empty(toasts.Toasts);
    }

    [Fact]
    public async Task TheFlow_SaysTheItemIsNoLongerInTheLibraryWhenItWasAlreadyGone()
    {
        _delete = () => Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>.Failed(LibraryItemDeletionOutcomeStatus.NotFound));
        var host = Render<AppDialogHost>();
        var toasts = Services.GetRequiredService<AppToastService>();

        var run = host.InvokeAsync(() => LibraryItemDeletionFlow.RunAsync(
            Services.GetRequiredService<IAppDialogService>(), toasts, [WorkId], "Dune: Part One"));
        Ready(host);
        Button(host, "Delete from library and disk").Click();

        Assert.Equal(LibraryItemDeletionFlowOutcome.NoLongerInLibrary, await run);
        Assert.Equal("This item is no longer in your library.", Assert.Single(toasts.Toasts).Message);
    }
}
