using AngleSharp.Dom;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Metadata;
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
/// Delete from the universal editor (v4 D13, WP10, acceptance A8): an episode (or a season) from the header's "...", ticked rows in Contents and
/// one file in Files each ask once and happen at once, without waiting for Save. The row leaves the list, a short line says what was freed, a
/// failure shows its reason and removes nothing, unsaved changes on the deleted item are dropped while other items stay staged, and the
/// editor moves to the parent of an item that went. The Engine is a hand-written stub; nothing is deleted anywhere.
/// </summary>
public sealed class EditorDeleteTests : AsyncBunitContext
{
    private static readonly Guid ShowId = EditTrailTestData.ShowId;
    private static readonly Guid SeasonOne = EditTrailTestData.SeasonOneId;
    private static readonly Guid EpisodeOne = EditTrailTestData.EpisodeOneId;
    private static readonly Guid EpisodeTwo = EditTrailTestData.EpisodeTwoId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly DashboardSessionAccessor _session = new();
    private readonly List<IReadOnlyList<Guid>> _previewTitles = [];
    private readonly List<(IReadOnlyList<Guid> Ids, string Fingerprint)> _titleDeletes = [];
    private readonly List<(Guid Work, Guid Asset)> _filePreviews = [];
    private readonly List<(Guid Work, Guid Asset, string Fingerprint)> _fileDeletes = [];
    private Func<IReadOnlyList<Guid>, LibraryItemDeletionResultDto> _titleResult = ids => new() { DeletedWorkIds = [.. ids], Titles = ["Episode"], BytesFreed = 2_147_483_648 };
    private LibraryItemDeletionResultDto _fileResult = new() { FilesDeleted = 1, BytesFreed = 400_000_000 };
    private LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>? _failure;
    private bool _itemRemainsAfterFile = true;

    public EditorDeleteTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(_device);
        Services.AddSingleton(_session);
    }

    private void UseApi(Action<EngineApiClientStub>? more = null)
    {
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
        {
            EditTrailTestData.RegisterFamily(stub);
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryItemDeletionPreviewAsync), args =>
            {
                var ids = (IReadOnlyList<Guid>)args![0]!;
                _previewTitles.Add(ids);
                return Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(new LibraryItemDeletionPreview
                {
                    Items = [.. ids.Select(id => new LibraryItemDeletionItemDto { WorkId = id, Title = "Episode", MediaType = "TV", FileCount = 1 })],
                    Files = [.. ids.Select(id => new LibraryItemDeletionFileDto { WorkId = id, AssetId = Guid.NewGuid(), FolderLabel = "TV", FileName = "Episode.mkv", SizeBytes = 1_073_741_824, Outcome = LibraryItemDeletionOutcomes.Delete })],
                    BytesFreed = 1_073_741_824L * ids.Count,
                    Fingerprint = "fp-titles",
                }));
            });
            stub.SetHandler(nameof(IEngineApiClient.DeleteLibraryItemsAsync), args =>
            {
                var ids = (IReadOnlyList<Guid>)args![0]!;
                _titleDeletes.Add((ids, (string)args[1]!));
                return Task.FromResult(_failure ?? LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>.Ok(_titleResult(ids)));
            });
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryItemFileDeletionPreviewAsync), args =>
            {
                var work = (Guid)args![0]!;
                var asset = (Guid)args[1]!;
                _filePreviews.Add((work, asset));
                return Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(new LibraryItemDeletionPreview
                {
                    Items = [new LibraryItemDeletionItemDto { WorkId = work, Title = "Dune", MediaType = "Audiobooks", FileCount = 2 }],
                    Files = [new LibraryItemDeletionFileDto { WorkId = work, AssetId = asset, FolderLabel = "Audiobooks", FileName = "Part 1.m4b", SizeBytes = 400_000_000, Outcome = LibraryItemDeletionOutcomes.Delete }],
                    BytesFreed = 400_000_000,
                    Fingerprint = "fp-file",
                    FileScope = new LibraryItemDeletionFileScopeDto { WorkId = work, AssetId = asset, FileName = "Part 1.m4b", ItemRemains = _itemRemainsAfterFile, RemainingFiles = _itemRemainsAfterFile ? 1 : 0 },
                }));
            });
            stub.SetHandler(nameof(IEngineApiClient.DeleteLibraryItemFileAsync), args =>
            {
                _fileDeletes.Add(((Guid)args![0]!, (Guid)args[1]!, (string)args[2]!));
                return Task.FromResult(_failure ?? LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>.Ok(_fileResult));
            });
            more?.Invoke(stub);
        }));
    }

    private void SetAdministrator(bool administrator = true)
    {
        var id = Guid.NewGuid();
        _session.Set("test-session-token", id, id, Guid.NewGuid(),
            new DashboardAuthorityResponse(id, id, true, true, 1, 1, administrator, administrator, null, 1, [], [], []));
    }

    private async Task<EditorHost> OpenAsync(Guid entity, params EditTrailItem[] trail)
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var api = Services.GetRequiredService<IEngineApiClient>();
        var host = new EditorHost(api, _device, new ServiceCollection().BuildServiceProvider(), NullLogger<EditorHost>.Instance);
        var binding = new EditorBinding(_device)
        {
            EntityId = entity,
            HasPermission = true,
            ShowHistory = true,
            MediaTypes = () => ["TV"],
            Trail = () => trail,
            OpenItem = _ => Task.FromResult(true),
        };
        Assert.True(await host.OpenAsync(binding, entity));
        await host.Family.EnsureAsync(entity, [ShowId]);
        return host;
    }

    private static EditTrailItem Show => new(ShowId, "Le Continental", EditTrailKinds.Show);

    private static EditTrailItem Season => new(SeasonOne, "Season 1", EditTrailKinds.Season);

    private static EditTrailItem Episode => new(EpisodeOne, "Episode 1", EditTrailKinds.Episode);

    private IRenderedComponent<AppDialogHost> RenderDialogs() => Render<AppDialogHost>();

    private static IElement RedButton(IRenderedComponent<AppDialogHost> dialogs) => dialogs.FindAll("footer button").Last();

    // Waits until the box has asked the Engine and shows its red button, so a click is a confirmation and not a click on Cancel.
    private static IElement WaitForConfirm(IRenderedComponent<AppDialogHost> dialogs)
    {
        dialogs.WaitForAssertion(() => Assert.Contains("Delete", dialogs.FindAll("footer button").Last().TextContent));
        dialogs.WaitForAssertion(() => Assert.Equal(2, dialogs.FindAll("footer button").Count));
        return RedButton(dialogs);
    }

    private IReadOnlyList<string> ToastLines => [.. Services.GetRequiredService<AppToastService>().Toasts.Select(toast => toast.Message)];

    // -- The header's "..." -----------------------------------------------------------------------

    [Fact]
    public async Task TheHeaderMenu_OffersDeleteWithTheItemsOwnWords_AndRaisesIt()
    {
        UseApi();
        SetAdministrator();
        var host = await OpenAsync(EpisodeOne, Show, Season, Episode);
        var raised = 0;

        var header = Render<EditorItemHeader>(parameters => parameters
            .Add(h => h.Flow, host.Flow)
            .Add(h => h.Fields, host.Mode.Fields)
            .Add(h => h.Family, host.Family)
            .Add(h => h.CanDelete, true)
            .Add(h => h.DeleteLabel, "Delete episode…")
            .Add(h => h.OnDelete, Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, () => raised++)));

        header.Find("button[aria-label='More actions for this item']").Click();
        header.WaitForAssertion(() => Assert.Contains("Delete episode…", header.FindAll("[role='menuitem']").Select(item => item.TextContent.Trim())));
        header.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Delete episode…").Click();

        header.WaitForAssertion(() => Assert.Equal(1, raised));
    }

    [Fact]
    public async Task TheHeaderMenu_IsNotDrawn_WhenTheItemCannotBeDeletedHere()
    {
        UseApi();
        var host = await OpenAsync(EpisodeOne, Show, Season, Episode);

        var header = Render<EditorItemHeader>(parameters => parameters
            .Add(h => h.Flow, host.Flow)
            .Add(h => h.Fields, host.Mode.Fields)
            .Add(h => h.Family, host.Family));

        Assert.Empty(header.FindAll("button[aria-label='More actions for this item']"));
    }

    [Fact]
    public async Task DeletingTheEpisodeBeingEdited_AsksOnce_DeletesAtOnce_AndMovesToTheSeason()
    {
        UseApi();
        SetAdministrator();
        var host = await OpenAsync(EpisodeOne, Show, Season, Episode);
        host.Session.Pending.SetField(EpisodeOne, "title", "Unsaved title on the deleted episode");
        host.Session.Pending.SetField(ShowId, "title", "Staged on the show");
        var dialogs = RenderDialogs();

        var deleting = dialogs.InvokeAsync(() => EditorDeleteFlow.DeleteActiveAsync(host, Services.GetRequiredService<IAppDialogService>(), Services.GetRequiredService<IAppToastService>(), _device, _session));
        var confirm = WaitForConfirm(dialogs);
        Assert.Single(_previewTitles);
        Assert.Empty(_titleDeletes);
        confirm.Click();

        Assert.True(await deleting);
        var (ids, fingerprint) = Assert.Single(_titleDeletes);
        Assert.Equal([EpisodeOne], ids);
        Assert.Equal("fp-titles", fingerprint);
        Assert.Equal(SeasonOne, host.Session.ActiveItem!.EntityId);
        Assert.True(host.IsOpen);
        Assert.Equal(0, host.Session.Pending.CountForItem(EpisodeOne));
        Assert.Equal(1, host.Session.Pending.CountForItem(ShowId));
        Assert.Contains(ToastLines, line => line.Contains("freed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SayingNo_DeletesNothing_AndStaysOnTheEpisode()
    {
        UseApi();
        SetAdministrator();
        var host = await OpenAsync(EpisodeOne, Show, Season, Episode);
        var dialogs = RenderDialogs();

        var deleting = dialogs.InvokeAsync(() => EditorDeleteFlow.DeleteActiveAsync(host, Services.GetRequiredService<IAppDialogService>(), Services.GetRequiredService<IAppToastService>(), _device, _session));
        WaitForConfirm(dialogs);
        dialogs.FindAll("footer button").First().Click();

        Assert.False(await deleting);
        Assert.Empty(_titleDeletes);
        Assert.Equal(EpisodeOne, host.Session.ActiveItem!.EntityId);
    }

    [Fact]
    public async Task ADeleteThePersonMayNotMake_IsRefused_BeforeAnythingIsAsked()
    {
        UseApi();
        SetAdministrator(administrator: false);
        var host = await OpenAsync(EpisodeOne, Show, Season, Episode);
        var dialogs = RenderDialogs();

        var deleted = await dialogs.InvokeAsync(() => EditorDeleteFlow.DeleteActiveAsync(host, Services.GetRequiredService<IAppDialogService>(), Services.GetRequiredService<IAppToastService>(), _device, _session));

        Assert.False(deleted);
        Assert.Empty(_previewTitles);
        Assert.Contains(LibraryItemDeletionText.Forbidden, ToastLines);
    }

    [Fact]
    public async Task AFailedDelete_ShowsTheReason_AndTheEditorStaysWhereItWas()
    {
        UseApi();
        SetAdministrator();
        _failure = LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>.Failed(LibraryItemDeletionOutcomeStatus.Invalid, "The folder is locked by another program.");
        var host = await OpenAsync(EpisodeOne, Show, Season, Episode);
        var dialogs = RenderDialogs();

        var deleting = dialogs.InvokeAsync(() => EditorDeleteFlow.DeleteActiveAsync(host, Services.GetRequiredService<IAppDialogService>(), Services.GetRequiredService<IAppToastService>(), _device, _session));
        WaitForConfirm(dialogs).Click();

        dialogs.WaitForAssertion(() => Assert.Contains("The folder is locked by another program.", dialogs.Markup));
        Assert.Equal(EpisodeOne, host.Session.ActiveItem!.EntityId);
        Assert.False(deleting.IsCompleted);
        dialogs.FindAll("footer button").First().Click();
        Assert.False(await deleting);
        Assert.Equal(EpisodeOne, host.Session.ActiveItem!.EntityId);
    }

    // -- A whole season, and what is above it -------------------------------------------------------

    [Fact]
    public void TheMenuWords_NameWhatIsBeingDeleted()
    {
        var season = EditorDeleteRules.GroupFor(SeasonOne, "Season 1", "season", "TV");
        var show = EditorDeleteRules.GroupFor(ShowId, "Le Continental", "series", "TV");
        var series = EditorDeleteRules.GroupFor(Guid.NewGuid(), "Dune Novels", "series", "Books");
        var album = EditorDeleteRules.GroupFor(Guid.NewGuid(), "OK Computer", "album", "Music");

        Assert.Equal(LibraryGroupKinds.Season, season!.Kind);
        Assert.Equal(LibraryGroupKinds.Show, show!.Kind);
        Assert.Equal(LibraryGroupKinds.Series, series!.Kind);
        Assert.Equal(LibraryGroupKinds.Album, album!.Kind);
        Assert.Null(EditorDeleteRules.GroupFor(EpisodeOne, "Episode 1", "episode", "TV"));
        Assert.Equal("Delete season…", EditorDeleteRules.MenuLabel(season, "season"));
        Assert.Equal("Delete show…", EditorDeleteRules.MenuLabel(show, "series"));
        Assert.Equal("Delete episode…", EditorDeleteRules.MenuLabel(null, "episode"));
        Assert.Equal("Delete song…", EditorDeleteRules.MenuLabel(null, "track"));
        Assert.Equal("Delete from library…", EditorDeleteRules.MenuLabel(null, "movie"));
    }

    [Fact]
    public async Task DeletingTheSeasonBeingEdited_MovesTheEditorToItsShow_AndKeepsOtherStagedChanges()
    {
        UseApi();
        SetAdministrator();
        var host = await OpenAsync(SeasonOne, Show, Season);
        host.Session.Pending.SetField(SeasonOne, "title", "Unsaved on the season");
        host.Session.Pending.SetField(ShowId, "title", "Staged on the show");

        await host.ItemsDeletedAsync([SeasonOne, EpisodeOne, EpisodeTwo]);

        Assert.True(host.IsOpen);
        Assert.Equal(ShowId, host.Session.ActiveItem!.EntityId);
        Assert.Equal(0, host.Session.Pending.CountForItem(SeasonOne));
        Assert.Equal(1, host.Session.Pending.CountForItem(ShowId));
        Assert.Null(host.Family.NameOf(SeasonOne));
        Assert.Null(host.Family.NameOf(EpisodeOne));
    }

    [Fact]
    public async Task WhenTheItemHasNoParent_TheEditorCloses()
    {
        UseApi();
        SetAdministrator();
        var host = await OpenAsync(ShowId, Show);

        await host.ItemsDeletedAsync([ShowId]);

        Assert.False(host.IsOpen);
    }

    [Fact]
    public async Task AnItemElsewhereDeleted_LeavesTheEditorOnItsItem()
    {
        UseApi();
        SetAdministrator();
        var host = await OpenAsync(EpisodeOne, Show, Season, Episode);

        await host.ItemsDeletedAsync([EpisodeTwo]);

        Assert.Equal(EpisodeOne, host.Session.ActiveItem!.EntityId);
        Assert.True(host.IsOpen);
    }

    // -- Ticked rows in Contents --------------------------------------------------------------------

    [Fact]
    public async Task DeletingTickedRows_AsksOnce_DeletesAtOnce_AndTheRowsLeaveTheList()
    {
        UseApi();
        SetAdministrator();
        var host = await OpenAsync(SeasonOne, Show, Season);
        var dialogs = RenderDialogs();
        var tab = Render<EditorContentsTab>(parameters => parameters
            .Add(t => t.Flow, host.Flow)
            .Add(t => t.Family, host.Family)
            .Add(t => t.Batch, host.Batch)
            .Add(t => t.ParentId, SeasonOne)
            .Add(t => t.CanEdit, true)
            .Add(t => t.OnDeleted, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<IReadOnlyCollection<Guid>>(this, host.ItemsDeletedAsync)));
        tab.WaitForAssertion(() => Assert.Equal(2, tab.FindAll("tr.tl-contents__row").Count));
        tab.FindAll("tr.tl-contents__row")[0].QuerySelector("input[type=checkbox]")!.Change(true);
        tab.FindAll("tr.tl-contents__row")[1].QuerySelector("input[type=checkbox]")!.Change(true);
        tab.WaitForAssertion(() => Assert.Equal("Delete 2 episodes", tab.Find(".tl-contents__delete").TextContent.Trim()));
        host.Session.Pending.SetField(EpisodeOne, "title", "Unsaved on a ticked row");
        host.Session.Pending.SetField(ShowId, "title", "Staged on the show");

        tab.Find(".tl-contents__delete").Click();
        var confirm = WaitForConfirm(dialogs);
        Assert.Equal([EpisodeOne, EpisodeTwo], Assert.Single(_previewTitles));
        Assert.Empty(_titleDeletes);
        confirm.Click();

        tab.WaitForAssertion(() => Assert.Empty(tab.FindAll("tr.tl-contents__row")));
        var (ids, _) = Assert.Single(_titleDeletes);
        Assert.Equal([EpisodeOne, EpisodeTwo], ids);
        Assert.Equal(0, host.Batch.Count);
        Assert.Equal(0, host.Session.Pending.CountForItem(EpisodeOne));
        Assert.Equal(1, host.Session.Pending.CountForItem(ShowId));
        Assert.Equal(SeasonOne, host.Session.ActiveItem!.EntityId);
    }

    [Fact]
    public async Task AFailedDeleteOfTickedRows_KeepsEveryRow()
    {
        UseApi();
        SetAdministrator();
        _failure = LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>.Failed(LibraryItemDeletionOutcomeStatus.Invalid, "A file is in use.");
        var host = await OpenAsync(SeasonOne, Show, Season);
        var dialogs = RenderDialogs();
        var tab = Render<EditorContentsTab>(parameters => parameters
            .Add(t => t.Flow, host.Flow)
            .Add(t => t.Family, host.Family)
            .Add(t => t.Batch, host.Batch)
            .Add(t => t.ParentId, SeasonOne)
            .Add(t => t.CanEdit, true)
            .Add(t => t.OnDeleted, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<IReadOnlyCollection<Guid>>(this, host.ItemsDeletedAsync)));
        tab.WaitForAssertion(() => Assert.Equal(2, tab.FindAll("tr.tl-contents__row").Count));
        tab.FindAll("tr.tl-contents__row")[0].QuerySelector("input[type=checkbox]")!.Change(true);
        tab.WaitForAssertion(() => Assert.Equal("Delete 1 episode", tab.Find(".tl-contents__delete").TextContent.Trim()));

        tab.Find(".tl-contents__delete").Click();
        WaitForConfirm(dialogs).Click();

        dialogs.WaitForAssertion(() => Assert.Contains("A file is in use.", dialogs.Markup));
        Assert.Equal(2, tab.FindAll("tr.tl-contents__row").Count);
        Assert.Equal(1, host.Batch.Count);
    }

    [Fact]
    public async Task WithoutAdministratorAccess_TheTickedRowsOfferNoDelete()
    {
        UseApi();
        SetAdministrator(administrator: false);
        var host = await OpenAsync(SeasonOne, Show, Season);
        var tab = Render<EditorContentsTab>(parameters => parameters
            .Add(t => t.Flow, host.Flow)
            .Add(t => t.Family, host.Family)
            .Add(t => t.Batch, host.Batch)
            .Add(t => t.ParentId, SeasonOne)
            .Add(t => t.CanEdit, true));
        tab.WaitForAssertion(() => Assert.Equal(2, tab.FindAll("tr.tl-contents__row").Count));
        tab.FindAll("tr.tl-contents__row")[0].QuerySelector("input[type=checkbox]")!.Change(true);

        tab.WaitForAssertion(() => Assert.NotEmpty(tab.FindAll(".tl-contents__edit")));
        Assert.Empty(tab.FindAll(".tl-contents__delete"));
    }

    // -- One file in Files --------------------------------------------------------------------------

    private static readonly Guid BookId = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid PartOne = Guid.Parse("c0000000-0000-0000-0000-0000000000a1");
    private static readonly Guid PartTwo = Guid.Parse("c0000000-0000-0000-0000-0000000000a2");

    private void UseFiles()
    {
        UseApi(stub => stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorWorkVersionsAsync), _ =>
            Task.FromResult<MediaEditorWorkVersionSelectorDto?>(new MediaEditorWorkVersionSelectorDto
            {
                WorkId = BookId,
                Editions =
                [
                    new MediaEditorEditionSelectorDto
                    {
                        AssetCount = 2,
                        Assets =
                        [
                            new MediaEditorAssetSelectorDto { AssetId = PartOne, FileName = "Part 1.m4b", Folder = "/media/audiobooks/Dune", FileSizeBytes = 400_000_000, TechnicalLabel = "M4B" },
                            new MediaEditorAssetSelectorDto { AssetId = PartTwo, FileName = "Part 2.m4b", Folder = "/media/audiobooks/Dune", FileSizeBytes = 350_000_000, TechnicalLabel = "M4B" },
                        ],
                    },
                ],
            })));
        SetAdministrator();
        _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true)).GetAwaiter().GetResult();
    }

    [Fact]
    public void DeletingOneFile_AsksOnce_DeletesAtOnce_AndTheRowLeavesTheList()
    {
        UseFiles();
        var dialogs = RenderDialogs();
        IReadOnlyCollection<Guid>? told = null;
        var tab = Render<EditorFilesTab>(parameters => parameters
            .Add(t => t.EntityId, BookId)
            .Add(t => t.CanDelete, true)
            .Add(t => t.OnDeleted, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<IReadOnlyCollection<Guid>>(this, ids => told = ids)));
        tab.WaitForAssertion(() => Assert.Equal(2, tab.FindAll("tr.tl-files__row").Count));

        tab.FindAll("tr.tl-files__row")[0].QuerySelector("button.tl-files__delete")!.Click();
        var confirm = WaitForConfirm(dialogs);
        Assert.Contains("stays in your Library with its other file", dialogs.Find("[data-testid='file-scope']").TextContent);
        Assert.Contains("Delete \"Part 1.m4b\"?", dialogs.Find(".app-dialog-shell__title").TextContent);
        Assert.Equal("Delete file", confirm.TextContent.Trim());
        Assert.Equal((BookId, PartOne), Assert.Single(_filePreviews));
        Assert.Empty(_fileDeletes);
        confirm.Click();

        tab.WaitForAssertion(() => Assert.Single(tab.FindAll("tr.tl-files__row")));
        var (work, asset, fingerprint) = Assert.Single(_fileDeletes);
        Assert.Equal((BookId, PartOne), (work, asset));
        Assert.Equal("fp-file", fingerprint);
        Assert.Equal("Part 2.m4b", tab.Find("tr.tl-files__row .tl-files__name").TextContent.Trim());
        Assert.Null(told);
        Assert.Contains(ToastLines, line => line.Contains("Part 1.m4b", StringComparison.Ordinal) && line.Contains("freed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DeletingTheLastFile_TellsTheEditorTheItemWentToo()
    {
        UseFiles();
        _itemRemainsAfterFile = false;
        _fileResult = new LibraryItemDeletionResultDto { DeletedWorkIds = [BookId], Titles = ["Dune"], FilesDeleted = 1, BytesFreed = 400_000_000 };
        var dialogs = RenderDialogs();
        IReadOnlyCollection<Guid>? told = null;
        var tab = Render<EditorFilesTab>(parameters => parameters
            .Add(t => t.EntityId, BookId)
            .Add(t => t.CanDelete, true)
            .Add(t => t.OnDeleted, Microsoft.AspNetCore.Components.EventCallback.Factory.Create<IReadOnlyCollection<Guid>>(this, ids => told = ids)));
        tab.WaitForAssertion(() => Assert.Equal(2, tab.FindAll("tr.tl-files__row").Count));

        tab.FindAll("tr.tl-files__row")[1].QuerySelector("button.tl-files__delete")!.Click();
        var confirm = WaitForConfirm(dialogs);
        Assert.Contains("only file", dialogs.Find("[data-testid='file-scope']").TextContent);
        confirm.Click();

        tab.WaitForAssertion(() => Assert.NotNull(told));
        Assert.Equal([BookId], told);
    }

    [Fact]
    public void AFailedFileDelete_ShowsTheReason_AndTheRowStays()
    {
        UseFiles();
        _failure = LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>.Failed(LibraryItemDeletionOutcomeStatus.Invalid, "The file is in use.");
        var dialogs = RenderDialogs();
        var tab = Render<EditorFilesTab>(parameters => parameters
            .Add(t => t.EntityId, BookId)
            .Add(t => t.CanDelete, true));
        tab.WaitForAssertion(() => Assert.Equal(2, tab.FindAll("tr.tl-files__row").Count));

        tab.FindAll("tr.tl-files__row")[0].QuerySelector("button.tl-files__delete")!.Click();
        WaitForConfirm(dialogs).Click();

        dialogs.WaitForAssertion(() => Assert.Contains("The file is in use.", dialogs.Markup));
        Assert.Equal(2, tab.FindAll("tr.tl-files__row").Count);
    }

    [Fact]
    public void WithoutEditAccess_NoFileOffersDelete()
    {
        UseFiles();

        var tab = Render<EditorFilesTab>(parameters => parameters.Add(t => t.EntityId, BookId));

        tab.WaitForAssertion(() => Assert.Equal(2, tab.FindAll("tr.tl-files__row").Count));
        Assert.Empty(tab.FindAll("button.tl-files__delete"));
    }
}
