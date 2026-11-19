using System.Globalization;
using Bunit;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A playlist's own page (spec listen-playlists WP4): the header shows the cover, owner, song count, length and description; the "..." menu
/// renames and deletes from the page; Sort then Keep this order saves the sorted order; songs the library cannot find are counted; and a
/// playlist of more than a thousand songs lists whole and still reorders through one move.
/// </summary>
public sealed class ListenPlaylistPageTests : AsyncBunitContext
{
    private static readonly Guid ActiveId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly Guid _playlistId = Guid.NewGuid();
    private readonly List<WorkViewModel> _songs = [];
    private readonly List<CollectionItemDto> _items = [];
    private readonly List<int> _itemReadLimits = [];
    private readonly List<(Guid Id, string? Name, string? Description)> _updates = [];
    private readonly List<Guid> _deleted = [];
    private readonly List<(IReadOnlyList<Guid> Current, IReadOnlyList<Guid> Next)> _appliedOrders = [];
    private readonly List<(Guid ItemId, CollectionItemMoveRequest Move)> _moves = [];
    private ManagedCollectionViewModel _playlist;

    public ListenPlaylistPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        _playlist = Playlist(itemCount: 0, seconds: null);
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
            {
                new(ActiveId, "Test User", "#C9922E", "Administrator", DateTimeOffset.UtcNow),
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetManagedCollectionsAsync), _ => Task.FromResult(new List<ManagedCollectionViewModel> { _playlist }));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryWorksAsync), _ => Task.FromResult(_songs.ToList()));
            stub.SetHandler(nameof(IEngineApiClient.GetCollectionItemsAsync), args =>
            {
                var limit = (int)args![1]!;
                _itemReadLimits.Add(limit);
                return Task.FromResult(_items.Take(limit).Select(Copy).ToList());
            });
            stub.SetHandler(nameof(IEngineApiClient.UpdateCollectionAsync), args =>
            {
                _updates.Add(((Guid)args![0]!, (string?)args[1], (string?)args[2]));
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.DeleteCollectionAsync), args =>
            {
                _deleted.Add((Guid)args![0]!);
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.ApplyCollectionItemOrderAsync), args =>
            {
                _appliedOrders.Add(((IReadOnlyList<Guid>)args![1]!, (IReadOnlyList<Guid>)args[2]!));
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.MoveCollectionItemAsync), args =>
            {
                _moves.Add(((Guid)args![1]!, (CollectionItemMoveRequest)args[2]!));
                return Task.FromResult(true);
            });
        });
        Services.AddSingleton(_device);
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddScoped<CollectionEditorLauncherService>();
        Services.AddScoped<ListenAudioDragService>();
        Services.AddScoped<SongDragDropService>();
        Services.AddSingleton(new SavedItemService(api));
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(new DefaultPreferences());
        Services.AddSingleton(provider => new PlaybackSessionController(
            null!, null!, preferences: provider.GetRequiredService<IUserPlaybackPreferencesAccessor>()));
    }

    private ManagedCollectionViewModel Playlist(int itemCount, double? seconds, string? description = null, string type = "Playlist", string resolution = "materialized") => new()
    {
        Id = _playlistId,
        Name = "Road trip",
        Description = description,
        CollectionType = type,
        Resolution = resolution,
        ProfileId = ActiveId,
        ItemCount = itemCount,
        TotalDurationSeconds = seconds,
        CoverMode = "mosaic",
    };

    // Songs "Song 0001" and so on, kept in the library, and the playlist's items in the order given by their titles' reversed order unless a test
    // arranges them differently.
    private void SeedSongs(IEnumerable<string> titles, bool inLibrary = true)
    {
        var order = 0;
        foreach (var title in titles)
        {
            var id = Guid.NewGuid();
            if (inLibrary)
            {
                _songs.Add(new WorkViewModel
                {
                    Id = id,
                    MediaType = "Music",
                    CanonicalValues =
                    [
                        new CanonicalValueViewModel { Key = "title", Value = title },
                        new CanonicalValueViewModel { Key = "artist", Value = "The Artist" },
                        new CanonicalValueViewModel { Key = "album", Value = "The Album" },
                    ],
                });
            }

            _items.Add(new CollectionItemDto { Id = Guid.NewGuid(), WorkId = id, Title = title, SortOrder = ++order });
        }
    }

    private static CollectionItemDto Copy(CollectionItemDto item) => new()
    {
        Id = item.Id,
        WorkId = item.WorkId,
        Title = item.Title,
        SortOrder = item.SortOrder,
    };

    private async Task<IRenderedComponent<ListenPage>> RenderPageAsync()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/listen/music/playlists/{_playlistId}");
        Render<AppPopoverHost>();
        var page = Render<ListenPage>(parameters => parameters.Add(component => component.CollectionId, _playlistId));
        page.WaitForElement(".listen-detail__copy");
        return page;
    }

    private static IReadOnlyList<string> Titles(IRenderedComponent<ListenPage> page) =>
        [.. page.FindAll(".song-table__title").Select(title => title.TextContent.Trim())];

    private static void ChooseMenuItem(IRenderedComponent<ListenPage> page, string label) =>
        page.FindAll("button.app-menu-item").Single(button => button.TextContent.Trim() == label).Click();

    [Fact]
    public async Task ThePlaylistHeader_ShowsTheKindOwnerSongCountLengthAndDescription()
    {
        SeedSongs(["Alpha", "Bravo", "Charlie"]);
        _playlist = Playlist(itemCount: 3, seconds: 2 * 3600 + 55 * 60, description: "Songs I play when I drive.");

        var page = await RenderPageAsync();

        Assert.Equal("Road trip", page.Find(".listen-detail__copy h2").TextContent.Trim());
        Assert.Equal("Playlist · yours", page.Find("[data-playlist-kind]").TextContent.Trim());
        Assert.Equal("3 songs · 2h 55m", page.Find("[data-playlist-meta]").TextContent.Trim());
        Assert.Equal("Songs I play when I drive.", page.Find("[data-playlist-description]").TextContent.Trim());
        Assert.Empty(page.FindAll("[data-playlist-unavailable]"));
    }

    [Fact]
    public async Task ThePlaylistHeader_ShowsOnlyTheCountWhenTheEngineDoesNotKnowTheLength()
    {
        SeedSongs(["Alpha", "Bravo"]);
        _playlist = Playlist(itemCount: 2, seconds: null);

        var page = await RenderPageAsync();

        Assert.Equal("2 songs", page.Find("[data-playlist-meta]").TextContent.Trim());
    }

    [Fact]
    public async Task SongsTheLibraryCannotFind_AreCountedInTheHeaderAndLeftOutOfTheCount()
    {
        SeedSongs(["Alpha", "Bravo"]);
        SeedSongs(["Gone"], inLibrary: false);
        _playlist = Playlist(itemCount: 3, seconds: null);

        var page = await RenderPageAsync();

        Assert.Equal("2 songs", page.Find("[data-playlist-meta]").TextContent.Trim());
        Assert.Equal("1 unavailable song", page.Find("[data-playlist-unavailable]").TextContent.Trim());
        Assert.Equal(["Alpha", "Bravo"], Titles(page));
    }

    [Fact]
    public async Task RenamingFromThePlaylistsOwnPage_AsksForTheNameInASmallWindowAndSavesIt()
    {
        SeedSongs(["Alpha", "Bravo"]);
        _playlist = Playlist(itemCount: 2, seconds: null);
        var host = Render<AppDialogHost>();
        var page = await RenderPageAsync();

        page.Find(".listen-detail .app-row-menu__trigger").Click();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("button.app-menu-item")));
        // The click finishes only when the window is closed, so it is not awaited here.
        _ = page.FindAll("button.app-menu-item").Single(button => button.TextContent.Trim() == "Rename").ClickAsync(new());
        host.WaitForElement(".app-dialog-shell input");
        host.Find(".app-dialog-shell input").Input("Long drive");
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Save").Click();

        page.WaitForAssertion(() => Assert.Equal([(_playlistId, (string?)"Long drive", (string?)null)], _updates));
    }

    [Fact]
    public async Task DeletingFromThePlaylistsOwnPage_AsksOnceThenDeletesIt()
    {
        SeedSongs(["Alpha"]);
        _playlist = Playlist(itemCount: 1, seconds: null);
        var host = Render<AppDialogHost>();
        var page = await RenderPageAsync();

        page.Find(".listen-detail .app-row-menu__trigger").Click();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("button.app-menu-item")));
        _ = page.FindAll("button.app-menu-item").Single(button => button.TextContent.Trim() == "Delete").ClickAsync(new());
        host.WaitForElement(".app-confirm");
        Assert.Empty(_deleted);
        host.FindAll(".app-dialog-shell__footer button").Single(button => button.TextContent.Trim() == "Delete").Click();

        page.WaitForAssertion(() => Assert.Equal([_playlistId], _deleted));
    }

    [Fact]
    public async Task ThePlaylistMenu_OffersTheSameItemsAsTheSidebarAndNoM3uItem()
    {
        SeedSongs(["Alpha", "Bravo"]);
        _playlist = Playlist(itemCount: 2, seconds: null);
        var page = await RenderPageAsync();

        page.Find(".listen-detail .app-row-menu__trigger").Click();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("button.app-menu-item")));

        Assert.Equal(
            ["Rename", "Edit description and cover", "Duplicate", "Pin to top", "Delete"],
            page.FindAll("button.app-menu-item").Select(button => button.TextContent.Trim()).ToList());
    }

    [Fact]
    public async Task SortingThenKeepingTheOrder_SavesTheSortedOrderAsTheManualOrder()
    {
        SeedSongs(["Charlie", "Alpha", "Delta", "Bravo"]);
        _playlist = Playlist(itemCount: 4, seconds: null);
        var saved = _items.Select(item => item.Id).ToList();
        var page = await RenderPageAsync();
        Assert.Equal(["Charlie", "Alpha", "Delta", "Bravo"], Titles(page));

        page.Find(".listen-playlist-sort__button").Click();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("button.app-menu-item")));
        ChooseMenuItem(page, "Title");

        // The sort is only a preview until it is kept: nothing is sent yet and Cancel would put the saved order back.
        page.WaitForAssertion(() => Assert.Equal(["Alpha", "Bravo", "Charlie", "Delta"], Titles(page)));
        Assert.Empty(_appliedOrders);
        Assert.Empty(_moves);
        var keep = page.Find("[data-reorder-save]");
        Assert.Equal("Keep this order", keep.TextContent.Trim());
        keep.Click();

        page.WaitForAssertion(() => Assert.Single(_appliedOrders));
        var (current, next) = _appliedOrders[0];
        Assert.Equal(saved, current);
        var byTitle = _items.OrderBy(item => item.Title, StringComparer.Ordinal).Select(item => item.Id).ToList();
        Assert.Equal(byTitle, next);
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".app-reorder-bar")));
    }

    [Fact]
    public async Task CancellingASort_PutsTheSavedOrderBackAndSendsNothing()
    {
        SeedSongs(["Charlie", "Alpha", "Bravo"]);
        _playlist = Playlist(itemCount: 3, seconds: null);
        var page = await RenderPageAsync();

        page.Find(".listen-playlist-sort__button").Click();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("button.app-menu-item")));
        ChooseMenuItem(page, "Title");
        page.WaitForAssertion(() => Assert.Equal(["Alpha", "Bravo", "Charlie"], Titles(page)));
        page.Find("[data-reorder-cancel]").Click();

        page.WaitForAssertion(() => Assert.Equal(["Charlie", "Alpha", "Bravo"], Titles(page)));
        Assert.Empty(_appliedOrders);
        Assert.Empty(_moves);
    }

    [Fact]
    public async Task APlaylistOfMoreThanAThousandSongs_ListsEverySong_AndMovingOneToTheBottomSendsToBottom()
    {
        const int count = 1100;
        SeedSongs(Enumerable.Range(1, count).Select(number => $"Song {number:0000}"));
        _playlist = Playlist(itemCount: count, seconds: count * 200d);
        var first = _items[0].Id;
        var page = await RenderPageAsync();

        // The page asks for the whole list, not the first thousand.
        Assert.All(_itemReadLimits, limit => Assert.True(limit >= count, $"The page read only {limit} songs."));
        Assert.Equal($"{count.ToString("N0", CultureInfo.CurrentCulture)} songs · 61h 6m", page.Find("[data-playlist-meta]").TextContent.Trim());
        Assert.Empty(page.FindAll("[data-playlist-unavailable]"));

        page.FindAll(".listen-detail button").Single(button => button.TextContent.Contains("Reorder", StringComparison.Ordinal)).Click();
        page.WaitForElement(".song-table__end-zone");
        page.Find($"[data-song-row-id='{_songs[0].Id:D}']").DragStart();
        page.Find(".song-table__end-zone").DragOver();
        page.Find(".song-table__end-zone").Drop();
        page.WaitForAssertion(() => Assert.False(page.Find("[data-reorder-save]").HasAttribute("disabled")));
        page.Find("[data-reorder-save]").Click();

        page.WaitForAssertion(() => Assert.Single(_moves));
        Assert.Equal(first, _moves[0].ItemId);
        Assert.True(_moves[0].Move.ToBottom);
        Assert.False(_moves[0].Move.ToTop);
        Assert.Null(_moves[0].Move.Position);
        Assert.Empty(_appliedOrders);
    }

    private sealed class DefaultPreferences : IUserPlaybackPreferencesAccessor
    {
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) =>
            Task.FromResult<UserPlaybackSettingsDto?>(UserPlaybackSettingsDto.CreateDefaults(ActiveId));

        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }
}
