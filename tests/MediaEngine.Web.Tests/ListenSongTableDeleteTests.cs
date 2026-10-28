using System.Reflection;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Items;
using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Listen song table's selection (spec delete-from-library D13, D25; universal editor spec D14): who sees the row checkboxes and the
/// Delete entries, what the shared "N selected" bar does (Edit, Move to album, Delete in "..."), and that the handlers refuse on
/// their own when the icon should not have been there.
/// </summary>
public sealed class ListenSongTableDeleteTests : AsyncBunitContext
{
    private const string RowMenuTrigger = ".listen-song-table__options button[aria-haspopup='menu']";

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly DashboardSessionAccessor _session = new();
    private readonly List<IReadOnlyList<Guid>> _previewRequests = [];
    private readonly List<Guid> _played = [];

    public ListenSongTableDeleteTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.PreviewLibraryBatchEditAsync), _ =>
                Task.FromResult(LibraryBatchEditOutcome<LibraryBatchEditPreview>.Ok(new LibraryBatchEditPreview { AffectedCount = 1 })));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryItemDeletionPreviewAsync), args =>
            {
                var ids = (IReadOnlyList<Guid>)args![0]!;
                _previewRequests.Add(ids);
                return Task.FromResult(LibraryItemDeletionOutcome<LibraryItemDeletionPreview>.Ok(new LibraryItemDeletionPreview
                {
                    Items = [.. ids.Select(id => new LibraryItemDeletionItemDto { WorkId = id, Title = "Song", MediaType = "Music", FileCount = 1 })],
                    Files = [.. ids.Select(id => new LibraryItemDeletionFileDto { WorkId = id, AssetId = Guid.NewGuid(), FolderLabel = "Music", FileName = "song.flac", SizeBytes = 1_000_000, Outcome = LibraryItemDeletionOutcomes.Delete })],
                    BytesFreed = ids.Count * 1_000_000L,
                    Fingerprint = "fp-1",
                }));
            });
        });
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(_device);
        Services.AddSingleton(_session);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
    }

    private Task UseDeviceAsync(string formFactor, bool touch = false, bool fine = true, bool hover = true) =>
        _device.InitialiseAsync(new DeviceContextSnapshot(formFactor == "phone" ? "mobile" : "web", formFactor, touch, FinePointer: fine, Hover: hover));

    private void SetAdministrator(bool administrator)
    {
        var id = Guid.NewGuid();
        _session.Set("test-session-token", id, id, Guid.NewGuid(),
            new DashboardAuthorityResponse(id, id, true, true, 1, 1, administrator, administrator, null, 1, [], [], []));
    }

    private static List<WorkViewModel> Songs(int count) =>
        [.. Enumerable.Range(1, count).Select(index => new WorkViewModel
        {
            Id = Guid.NewGuid(),
            RootWorkId = Guid.NewGuid(),
            MediaType = "Music",
            CanonicalValues =
            [
                new CanonicalValueViewModel { Key = "title", Value = $"Song {index}" },
                new CanonicalValueViewModel { Key = "artist", Value = "The Artist" },
                new CanonicalValueViewModel { Key = "duration_sec", Value = "185" },
            ],
        })];

    private IRenderedComponent<ListenSongTable> RenderTable(IReadOnlyList<WorkViewModel> songs) =>
        Render<ListenSongTable>(parameters => parameters
            .Add(table => table.Tracks, songs)
            .Add(table => table.Sortable, false)
            .Add(table => table.OnPlayRequested, (_, startId, _, _) =>
            {
                if (startId is { } id)
                {
                    _played.Add(id);
                }

                return Task.CompletedTask;
            }));

    private IReadOnlyList<string> Toasts => [.. Services.GetRequiredService<AppToastService>().Toasts.Select(toast => toast.Message)];

    private static IReadOnlyList<AngleSharp.Dom.IElement> Checkboxes(IRenderedComponent<ListenSongTable> cut) =>
        cut.FindAll(".listen-song-table__select input[type='checkbox']");

    // Delete from library is the last, red item of a row's "..." menu (one-look spec WP6, F45): open that row's menu to see it.
    private static IReadOnlyList<AngleSharp.Dom.IElement> OpenRowMenuItems(IRenderedComponent<ListenSongTable> cut, int row)
    {
        cut.FindAll(RowMenuTrigger)[row].Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        return cut.FindAll("[role='menuitem']");
    }

    private static bool IsDelete(AngleSharp.Dom.IElement item) => item.TextContent.Trim() == "Delete from library…";

    private const string Bar = ".tl-batch__bar";

    private static string BarCount(IRenderedComponent<ListenSongTable> cut) => cut.Find(".tl-batch__count").TextContent.Trim();

    // The bar's "..." menu holds Delete from library (administrators only).
    private static void DeleteFromTheBar(IRenderedComponent<ListenSongTable> cut)
    {
        cut.Find($"{Bar} button[aria-label='More actions']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));
        cut.FindAll("[role='menuitem']").Single(IsDelete).Click();
    }

    // -- Who sees it ---------------------------------------------------------------------------

    [Fact]
    public async Task AnAdministratorOnADesktopSeesACheckboxOnEveryRowAndDeleteAsTheLastMenuItem()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);

        var cut = RenderTable(Songs(3));

        Assert.Equal(3, Checkboxes(cut).Count);
        Assert.Empty(cut.FindAll(".listen-song-table__delete"));
        var items = OpenRowMenuItems(cut, 0);
        var delete = Assert.Single(items, IsDelete);
        Assert.Same(items[^1], delete);
        Assert.Contains("app-tone--error", delete.ClassList);
        Assert.Equal(["Select Song 1", "Select Song 2", "Select Song 3"], Checkboxes(cut).Select(box => box.GetAttribute("aria-label")));
        Assert.Empty(cut.FindAll(Bar));
    }

    [Fact]
    public async Task ANonAdministratorOnADesktopSeesTheCheckboxesToEditButDeleteNowhere()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(false);

        var cut = RenderTable(Songs(3));
        Checkboxes(cut)[0].Change(true);

        Assert.Equal(3, Checkboxes(cut).Count);
        Assert.Equal(["Edit", "Match", "Move to album", "Clear"], cut.FindAll($"{Bar} button").Select(button => button.TextContent.Trim()));
        Assert.Empty(cut.FindAll($"{Bar} button[aria-label='More actions']"));
        Assert.DoesNotContain(OpenRowMenuItems(cut, 0), IsDelete);
        Assert.DoesNotContain("Delete from library", cut.Markup.Replace("Delete from library…", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoAuthorityLoadedADesktopStillGetsTheCheckboxesAndNoDeleteIcon()
    {
        await UseDeviceAsync("desktop");

        var cut = RenderTable(Songs(2));

        Assert.Equal(2, Checkboxes(cut).Count);
        Assert.DoesNotContain(OpenRowMenuItems(cut, 0), IsDelete);
    }

    [Theory]
    [InlineData("phone", true, true, true)]
    [InlineData("tablet", true, false, true)]
    [InlineData("tablet", true, true, false)]
    [InlineData("desktop", true, false, false)]
    [InlineData("tv", false, false, false)]
    public async Task AnAdministratorOnAPhoneTabletOrTouchOnlyDeviceSeesNeitherTheCheckboxNorTheDeleteIcon(string formFactor, bool touch, bool fine, bool hover)
    {
        await UseDeviceAsync(formFactor, touch, fine, hover);
        SetAdministrator(true);

        var cut = RenderTable(Songs(3));

        Assert.Empty(Checkboxes(cut));
        Assert.DoesNotContain("Delete from library", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("checkbox", cut.Markup, StringComparison.Ordinal);
    }

    // -- One song ------------------------------------------------------------------------------

    [Fact]
    public async Task TheCountIsTheOnlyLiveRegionInTheBar()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var cut = RenderTable(Songs(2));
        Checkboxes(cut)[0].Change(true);

        var bar = cut.Find(Bar);

        Assert.Equal("toolbar", bar.GetAttribute("role"));
        Assert.Equal("1 selected", Assert.Single(bar.QuerySelectorAll("[role='status']")).TextContent.Trim());
    }

    [Fact]
    public async Task TheRowMenuDeleteItemOpensOneDialogForThatSongAndAsksTheEngineAboutOnlyIt()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var songs = Songs(3);
        var host = Render<AppDialogHost>();
        var cut = RenderTable(songs);

        OpenRowMenuItems(cut, 1).Single(IsDelete).Click();

        host.WaitForElement(".delete-library__section");
        Assert.Equal([songs[1].Id], Assert.Single(_previewRequests));
        Assert.Single(Services.GetRequiredService<AppDialogService>().Dialogs);
        Assert.Equal("Delete \"Song 2\" from your library?", host.Find(".app-dialog-shell__title").TextContent.Trim());
        Assert.Empty(_played);
    }

    // -- Several songs -------------------------------------------------------------------------

    [Fact]
    public async Task SelectingThreeSongsShowsTheBarAndDeleteOpensOneDialogCarryingAllThreeIds()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var songs = Songs(5);
        var host = Render<AppDialogHost>();
        var cut = RenderTable(songs);

        foreach (var index in new[] { 0, 2, 4 })
        {
            Checkboxes(cut)[index].Change(true);
        }

        var bar = cut.Find(Bar);
        Assert.Contains("3 selected", bar.TextContent);
        Assert.Equal(["Edit", "Move to album", "More actions", "Clear"], bar.QuerySelectorAll("button:not([hidden])")
            .Select(button => button.GetAttribute("aria-label") ?? button.TextContent.Trim())
            .Where(label => label.Length > 0));

        DeleteFromTheBar(cut);

        host.WaitForElement(".delete-library__section");
        var request = Assert.Single(_previewRequests);
        Assert.Equal(new[] { songs[0].Id, songs[2].Id, songs[4].Id }.Order(), request.Order());
        Assert.Single(Services.GetRequiredService<AppDialogService>().Dialogs);
        Assert.Contains("Delete 3 songs from your library?", host.Find(".app-dialog-shell__title").TextContent);
    }

    [Fact]
    public async Task ClearEmptiesTheSelectionAndHidesTheBar()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var cut = RenderTable(Songs(3));
        Checkboxes(cut)[0].Change(true);
        Checkboxes(cut)[1].Change(true);
        Assert.Equal("2 selected", BarCount(cut));

        cut.FindAll($"{Bar} button").Single(button => button.TextContent.Trim() == "Clear").Click();

        Assert.Empty(cut.FindAll(Bar));
        Assert.All(Checkboxes(cut), box => Assert.False(box.HasAttribute("checked")));
    }

    [Fact]
    public async Task ClickingACheckboxSelectsTheSongWithoutStartingPlayback()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var cut = RenderTable(Songs(2));

        Checkboxes(cut)[0].Change(true);
        // The cell stops the click and key events, so they never reach the row that plays the song.
        Assert.All(cut.FindAll(".listen-song-table__select"), cell =>
        {
            Assert.Contains(cell.Attributes, attribute => attribute.Name.Contains("onclick:stoppropagation", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(cell.Attributes, attribute => attribute.Name.Contains("onkeydown:stoppropagation", StringComparison.OrdinalIgnoreCase));
        });

        Assert.Empty(_played);
        Assert.Equal("1 selected", BarCount(cut));
        Assert.Single(Checkboxes(cut), box => box.HasAttribute("checked"));
    }

    [Fact]
    public async Task KeysPressedOnTheQueueEditAndDeleteCellsNeverReachTheRowThatPlaysTheSong()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var cut = RenderTable(Songs(2));

        foreach (var cell in new[] { "queue", "edit", "delete" })
        {
            Assert.All(cut.FindAll($".listen-song-table__{cell}"), element =>
                Assert.Contains(element.Attributes, attribute => attribute.Name.Contains("onkeydown:stoppropagation", StringComparison.OrdinalIgnoreCase)));
        }

        Assert.Empty(_played);
    }

    [Fact]
    public async Task PressingEnterOnTheRowStillPlaysTheSong()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var songs = Songs(2);
        var cut = RenderTable(songs);

        cut.FindAll(".listen-song-table__row")[0].KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        Assert.Equal([songs[0].Id], _played);
    }

    [Fact]
    public async Task ClickingTheRowStillPlaysTheSong()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var songs = Songs(2);
        var cut = RenderTable(songs);

        cut.FindAll(".listen-song-table__row")[1].Click();

        Assert.Equal([songs[1].Id], _played);
    }

    // -- Handlers never trust the icon ---------------------------------------------------------

    [Theory]
    [InlineData("phone", true, false, false)]
    [InlineData("tablet", true, true, false)]
    [InlineData("desktop", false, false, false)]
    public async Task TheHandlersRefuseWhenTheDeviceCannotEditEvenIfInvoked(string formFactor, bool touch, bool fine, bool hover)
    {
        await UseDeviceAsync(formFactor, touch, fine, hover);
        SetAdministrator(true);
        var songs = Songs(2);
        var cut = RenderTable(songs);

        await cut.InvokeAsync(() => cut.Instance.DeleteRowAsync(songs[0]));

        Assert.Equal(EditorDeviceRule.Message, Assert.Single(Toasts));
        Assert.Empty(Services.GetRequiredService<AppDialogService>().Dialogs);
        Assert.Empty(_previewRequests);
    }

    [Fact]
    public async Task TheHandlerRefusesANonAdministratorEvenIfInvoked()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(false);
        var songs = Songs(2);
        var cut = RenderTable(songs);

        await cut.InvokeAsync(() => cut.Instance.DeleteRowAsync(songs[0]));

        Assert.Equal("You need administrator access to delete items.", Assert.Single(Toasts));
        Assert.Empty(Services.GetRequiredService<AppDialogService>().Dialogs);
        Assert.Empty(_previewRequests);
    }

    // -- Edit and the list stay as they are -------------------------------------------------------

    [Fact]
    public async Task PressingEditOnTheBarOpensTheEditPanelAndTheListKeepsItsTicksAndRows()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(false);
        var cut = RenderTable(Songs(4));
        Checkboxes(cut)[1].Change(true);
        Checkboxes(cut)[2].Change(true);

        cut.FindAll($"{Bar} button").Single(button => button.TextContent.Trim() == "Edit").Click();

        cut.WaitForAssertion(() => Assert.Equal("Edit 2 songs", cut.Find(".tl-editor-panel__title").TextContent.Trim()));
        Assert.Equal(4, cut.FindAll(".listen-song-table__row").Count);
        Assert.Equal(2, Checkboxes(cut).Count(box => box.HasAttribute("checked")));
        Assert.Empty(_played);
    }

    [Fact]
    public async Task ClosingThePanelKeepsTheTicks()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(false);
        var cut = RenderTable(Songs(3));
        Checkboxes(cut)[0].Change(true);
        cut.FindAll($"{Bar} button").Single(button => button.TextContent.Trim() == "Edit").Click();
        cut.WaitForElement(".tl-editor-panel");

        cut.Find(".tl-editor-panel__close").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".tl-editor-panel")));
        Assert.Equal("1 selected", BarCount(cut));
        Assert.Single(Checkboxes(cut), box => box.HasAttribute("checked"));
    }

    // -- No browser confirm --------------------------------------------------------------------

    [Fact]
    public async Task NothingEverAsksTheBrowserToConfirm()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var host = Render<AppDialogHost>();
        var cut = RenderTable(Songs(2));

        Checkboxes(cut)[0].Change(true);
        DeleteFromTheBar(cut);
        host.WaitForElement(".delete-library__section");
        host.FindAll("footer button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier.Contains("confirm", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheTableCarriesNoBrowserConfirmOrOldBatchDeleteCode()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MediaEngine.Web", "Components", "Listen", "ListenSongTable.razor"));
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("confirm(", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DeleteSelectedTracks", source, StringComparison.Ordinal);
    }

    // -- Deleted somewhere -------------------------------------------------------------------

    [Fact]
    public async Task ADeletedSongLeavesTheRenderedListAndTheSelectionWhenTheLiveEventArrives()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var songs = Songs(4);
        var cut = RenderTable(songs);
        Checkboxes(cut)[0].Change(true);
        Checkboxes(cut)[1].Change(true);
        Assert.Equal("2 selected", BarCount(cut));

        await cut.InvokeAsync(() => Services.GetRequiredService<UIOrchestratorService>()
            .RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([songs[0].Id], [])));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(3, cut.FindAll(".listen-song-table__row").Count);
            Assert.DoesNotContain("Song 1<", cut.Markup, StringComparison.Ordinal);
            Assert.Equal("1 selected", BarCount(cut));
        });
    }

    [Fact]
    public async Task AfterTheTableIsLeftItNoLongerListensForTheLiveEvent()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var cut = RenderTable(Songs(2));
        var orchestrator = Services.GetRequiredService<UIOrchestratorService>();
        var backingField = typeof(UIOrchestratorService).GetField(nameof(UIOrchestratorService.OnLibraryItemsRemoved), BindingFlags.Instance | BindingFlags.NonPublic)!;
        IEnumerable<Delegate> Listeners() => (backingField.GetValue(orchestrator) as Delegate)?.GetInvocationList() ?? [];

        Assert.Contains(Listeners(), listener => listener.Target is ListenSongTable);

        await DisposeComponentsAsync();

        Assert.DoesNotContain(Listeners(), listener => listener.Target is ListenSongTable);
    }
}
