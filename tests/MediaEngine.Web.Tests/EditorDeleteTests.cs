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
    private Func<IReadOnlyList<Guid>, LibraryItemDeletionResultDto> _titleResult = ids => new() { DeletedWorkIds = [.. ids], Titles = ["Episode"], BytesFreed = 2_147_483_648 };
    private LibraryItemDeletionOutcome<LibraryItemDeletionResultDto>? _failure;

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
    public async Task TheEditorSaysItIsDeleting_FromTheQuestionUntilTheMoveToTheParentIsDone_AndNoLonger()
    {
        UseApi();
        SetAdministrator();
        var host = await OpenAsync(EpisodeOne, Show, Season, Episode);
        var dialogs = RenderDialogs();
        Assert.False(host.IsDeleting);

        var deleting = dialogs.InvokeAsync(() => EditorDeleteFlow.DeleteActiveAsync(host, Services.GetRequiredService<IAppDialogService>(), Services.GetRequiredService<IAppToastService>(), _device, _session));
        var confirm = WaitForConfirm(dialogs);
        Assert.True(host.IsDeleting);
        confirm.Click();

        Assert.True(await deleting);
        Assert.False(host.IsDeleting);

        var again = dialogs.InvokeAsync(() => EditorDeleteFlow.DeleteActiveAsync(host, Services.GetRequiredService<IAppDialogService>(), Services.GetRequiredService<IAppToastService>(), _device, _session));
        WaitForConfirm(dialogs);
        Assert.True(host.IsDeleting);
        dialogs.FindAll("footer button").First().Click();
        Assert.False(await again);
        Assert.False(host.IsDeleting);
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
        Assert.Null(host.Family.TrailTo(SeasonOne));
        Assert.Null(host.Family.TrailTo(EpisodeOne));
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
}
