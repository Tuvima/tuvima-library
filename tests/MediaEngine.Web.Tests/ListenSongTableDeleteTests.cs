using System.Reflection;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Items;
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
/// Delete from library on the Listen song table (spec delete-from-library D13, D25): who sees the Delete icon and the row checkboxes,
/// what the slim "N selected" bar does, and that the handlers refuse on their own when the icon should not have been there.
/// </summary>
public sealed class ListenSongTableDeleteTests : AsyncBunitContext
{
    private const string RowDeleteSelector = "button[title='Delete from library…']";

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

    private static Task InvokeAsync(IRenderedComponent<ListenSongTable> cut, string method, params object?[] args) =>
        cut.InvokeAsync(() => (Task)typeof(ListenSongTable)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cut.Instance, args)!);

    // -- Who sees it ---------------------------------------------------------------------------

    [Fact]
    public async Task AnAdministratorOnADesktopSeesACheckboxAndADeleteIconOnEveryRow()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);

        var cut = RenderTable(Songs(3));

        Assert.Equal(3, Checkboxes(cut).Count);
        var icons = cut.FindAll(RowDeleteSelector);
        Assert.Equal(3, icons.Count);
        Assert.All(icons, icon => Assert.StartsWith("Delete Song ", icon.GetAttribute("aria-label")));
        Assert.All(icons, icon => Assert.EndsWith(" from library…", icon.GetAttribute("aria-label")));
        Assert.Equal(["Select Song 1", "Select Song 2", "Select Song 3"], Checkboxes(cut).Select(box => box.GetAttribute("aria-label")));
        Assert.Empty(cut.FindAll(".listen-song-table__selection"));
    }

    [Fact]
    public async Task ANonAdministratorSeesNeitherTheCheckboxNorTheDeleteIcon()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(false);

        var cut = RenderTable(Songs(3));

        Assert.Empty(Checkboxes(cut));
        Assert.Empty(cut.FindAll(RowDeleteSelector));
        Assert.DoesNotContain("Delete from library", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithNoAuthorityLoadedThereIsNoCheckboxAndNoDeleteIcon()
    {
        await UseDeviceAsync("desktop");

        var cut = RenderTable(Songs(2));

        Assert.Empty(Checkboxes(cut));
        Assert.Empty(cut.FindAll(RowDeleteSelector));
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
        Assert.Empty(cut.FindAll(RowDeleteSelector));
        Assert.DoesNotContain("Delete from library", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("checkbox", cut.Markup, StringComparison.Ordinal);
    }

    // -- One song ------------------------------------------------------------------------------

    [Fact]
    public async Task TheRowDeleteIconOpensOneDialogForThatSongAndAsksTheEngineAboutOnlyIt()
    {
        await UseDeviceAsync("desktop");
        SetAdministrator(true);
        var songs = Songs(3);
        var host = Render<AppDialogHost>();
        var cut = RenderTable(songs);

        cut.FindAll(RowDeleteSelector)[1].Click();

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

        var bar = cut.Find(".listen-song-table__selection");
        Assert.Contains("3 selected", bar.TextContent);
        Assert.Equal(["Delete…", "Clear"], bar.QuerySelectorAll("button").Select(button => button.TextContent.Trim()));
        Assert.Contains("app-tone--error", bar.QuerySelectorAll("button")[0].ClassList);

        bar.QuerySelectorAll("button")[0].Click();

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
        Assert.Contains("2 selected", cut.Find(".listen-song-table__selection").TextContent);

        cut.FindAll(".listen-song-table__selection button").Single(button => button.TextContent.Trim() == "Clear").Click();

        Assert.Empty(cut.FindAll(".listen-song-table__selection"));
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
        Assert.Contains("1 selected", cut.Find(".listen-song-table__selection").TextContent);
        Assert.Single(Checkboxes(cut), box => box.HasAttribute("checked"));
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

        await InvokeAsync(cut, "DeleteRowAsync", songs[0]);

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

        await InvokeAsync(cut, "DeleteRowAsync", songs[0]);

        Assert.Equal("You need administrator access to delete items.", Assert.Single(Toasts));
        Assert.Empty(Services.GetRequiredService<AppDialogService>().Dialogs);
        Assert.Empty(_previewRequests);
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
        cut.Find(".listen-song-table__selection button").Click();
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
        Assert.Contains("2 selected", cut.Find(".listen-song-table__selection").TextContent);

        await cut.InvokeAsync(() => Services.GetRequiredService<UIOrchestratorService>()
            .RaiseLibraryItemsRemoved(new LibraryItemsRemovedEvent([songs[0].Id], [])));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(3, cut.FindAll(".listen-song-table__row").Count);
            Assert.DoesNotContain("Song 1<", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("1 selected", cut.Find(".listen-song-table__selection").TextContent);
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
