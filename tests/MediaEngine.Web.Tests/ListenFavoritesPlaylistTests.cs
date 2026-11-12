using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Favorites inside Listen (music spec D11 and D14): the rail row opens the Favorites playlist in Listen, the page lists the hearted
/// songs in the profile's saved order on the playlist song table, Remove from Favorites takes the heart off, and moving a song asks the
/// Engine to save the new place.
/// </summary>
public sealed class ListenFavoritesPlaylistTests : AsyncBunitContext
{
    private const string FavoritesRoute = "/listen/music/playlists/system/favorite-songs";
    private static readonly Guid ActiveId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly Dictionary<Guid, string> _titles = [];
    private readonly List<Guid> _hearted = [];
    private readonly List<(Guid Song, Guid? Before)> _moves = [];
    private readonly List<(ProfileEntityKind Kind, Guid Id)> _removed = [];
    private readonly List<WorkViewModel> _songs = [];

    public ListenFavoritesPlaylistTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        // Delta is in the library but not hearted yet; the tests heart it "elsewhere" while Favorites is open.
        foreach (var title in new[] { "Alpha", "Bravo", "Charlie", "Delta" })
        {
            var id = Guid.NewGuid();
            _titles[id] = title;
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

        // The profile's saved order: Charlie, Alpha, Bravo.
        _hearted.AddRange([_songs[2].Id, _songs[0].Id, _songs[1].Id]);

        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
            {
                new(ActiveId, "Test User", "#C9922E", "Administrator", DateTimeOffset.UtcNow),
            }));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryWorksAsync), _ => Task.FromResult(_songs.ToList()));
            stub.SetHandler(nameof(IEngineApiClient.GetSavedItemsAsync), _ => Task.FromResult<IReadOnlyList<ProfileSavedItemDto>>(
                [.. _hearted.Select((id, index) => new ProfileSavedItemDto(ProfileEntityKind.Song, id, DateTimeOffset.UtcNow, index))]));
            stub.SetHandler(nameof(IEngineApiClient.GetSavedItemAsync), args => Task.FromResult(_hearted.Contains((Guid)args![1]!)
                ? new ProfileSavedItemDto(ProfileEntityKind.Song, (Guid)args[1]!, DateTimeOffset.UtcNow, _hearted.IndexOf((Guid)args[1]!))
                : null));
            stub.SetHandler(nameof(IEngineApiClient.SaveItemAsync), args =>
            {
                // A new heart goes to the top of Favorites, as the Engine does.
                _hearted.Insert(0, (Guid)args![1]!);
                return Task.FromResult<ProfileStateMutationDto?>(new ProfileStateMutationDto(ProfileEntityKind.Song, (Guid)args[1]!, true, null, DateTimeOffset.UtcNow));
            });
            stub.SetHandler(nameof(IEngineApiClient.RemoveSavedItemAsync), args =>
            {
                _removed.Add(((ProfileEntityKind)args![0]!, (Guid)args[1]!));
                _hearted.Remove((Guid)args[1]!);
                return Task.FromResult(true);
            });
            stub.SetHandler(nameof(IEngineApiClient.MoveFavoriteSongAsync), args =>
            {
                // Like the Engine: the song goes directly before the favorite named, or last when none is.
                var song = (Guid)args![0]!;
                var before = (Guid?)args[1];
                _moves.Add((song, before));
                _hearted.Remove(song);
                var at = before is { } named ? _hearted.IndexOf(named) : -1;
                _hearted.Insert(at >= 0 ? at : _hearted.Count, song);
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

    private async Task<IRenderedComponent<ListenPage>> RenderFavoritesAsync()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        Services.GetRequiredService<NavigationManager>().NavigateTo(FavoritesRoute);
        Render<AppPopoverHost>();
        var page = Render<ListenPage>(parameters => parameters.Add(component => component.PlaylistKey, "favorite-songs"));
        page.WaitForElement(".song-table__row");
        return page;
    }

    private static IReadOnlyList<string> Titles(IRenderedComponent<ListenPage> page) =>
        [.. page.FindAll(".song-table__title").Select(title => title.TextContent.Trim())];

    [Fact]
    public async Task TheRailFavoritesRow_GoesToTheFavoritesPlaylistInsideListen_AndShowsAsSelected()
    {
        var page = await RenderFavoritesAsync();

        var row = page.FindAll("a.listen-rail__item").Single(link => link.TextContent.Trim() == "Favorites");

        Assert.Equal(FavoritesRoute, row.GetAttribute("href"));
        Assert.DoesNotContain("for-me", row.GetAttribute("href"), StringComparison.Ordinal);
        Assert.Contains("is-active", row.ClassName, StringComparison.Ordinal);
        Assert.Equal("page", row.GetAttribute("aria-current"));
    }

    [Fact]
    public async Task TheFavoritesPage_ListsHeartedSongsInTheSavedOrder_OnThePlaylistTableWithItsTitleAndCount()
    {
        var page = await RenderFavoritesAsync();

        Assert.Equal(["Charlie", "Alpha", "Bravo"], Titles(page));
        Assert.Equal("Favorites", page.Find(".listen-detail__copy h2").TextContent.Trim());
        Assert.Equal("3 songs", page.Find(".listen-detail__meta").TextContent.Trim());
        Assert.NotEmpty(page.FindAll(".song-table"));
        Assert.DoesNotContain(page.FindAll(".song-table__head [role='columnheader']"), header => header.TextContent.Trim() == "Date added");
    }

    [Fact]
    public async Task RemovingASongFromFavorites_TakesTheHeartOffAndTheRowLeavesTheList_WithoutANotice()
    {
        var page = await RenderFavoritesAsync();
        var alphaId = _songs[0].Id;

        page.FindAll(".song-table__row").Single(row => row.TextContent.Contains("Alpha", StringComparison.Ordinal))
            .QuerySelector(".song-table__cell--actions button[aria-haspopup='menu']")!.Click();
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[role='menuitem']")));
        Assert.DoesNotContain(page.FindAll("[role='menuitem']"), item => item.TextContent.Trim() == "Remove from playlist");
        page.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Remove from Favorites").Click();

        page.WaitForAssertion(() => Assert.Equal(["Charlie", "Bravo"], Titles(page)));
        Assert.Equal([(ProfileEntityKind.Song, alphaId)], _removed);
        Assert.Empty(page.FindAll("[data-listen-notice]"));
        Assert.Equal("2 songs", page.Find(".listen-detail__meta").TextContent.Trim());
    }

    [Fact]
    public async Task MovingASongWithAltArrow_AsksTheEngineToSaveTheNewPlace_AndTheListShowsTheNewOrder()
    {
        var page = await RenderFavoritesAsync();
        var alphaId = _songs[0].Id;

        page.FindAll(".song-table__row").Single(row => row.TextContent.Contains("Alpha", StringComparison.Ordinal))
            .KeyDown(new KeyboardEventArgs { Key = "ArrowUp", AltKey = true });

        page.WaitForAssertion(() => Assert.Equal(["Alpha", "Charlie", "Bravo"], Titles(page)));
        Assert.Equal([(alphaId, (Guid?)_songs[2].Id)], _moves);
    }

    [Fact]
    public async Task DraggingASongDownTheList_AsksTheEngineForThePlaceItWasDroppedOn()
    {
        var page = await RenderFavoritesAsync();
        var charlieId = _songs[2].Id;
        AngleSharp.Dom.IElement RowOf(string title) =>
            page.FindAll(".song-table__row").Single(row => row.TextContent.Contains(title, StringComparison.Ordinal));

        RowOf("Charlie").DragStart();
        RowOf("Bravo").DragOver();
        RowOf("Bravo").Drop();

        page.WaitForAssertion(() => Assert.Equal(["Alpha", "Charlie", "Bravo"], Titles(page)));
        Assert.Equal([(charlieId, (Guid?)_songs[1].Id)], _moves);
    }

    private Guid Delta => _songs[3].Id;

    [Fact]
    public async Task ASongHeartedElsewhereWhileFavoritesIsOpen_AppearsAtTheTop_AndAnUnheartedOneLeaves()
    {
        var page = await RenderFavoritesAsync();
        var saved = Services.GetRequiredService<SavedItemService>();

        await saved.ToggleAsync(ProfileEntityKind.Song, Delta);
        page.WaitForAssertion(() => Assert.Equal(["Delta", "Charlie", "Alpha", "Bravo"], Titles(page)));
        Assert.Equal("4 songs", page.Find(".listen-detail__meta").TextContent.Trim());

        await saved.RemoveAsync(ProfileEntityKind.Song, _songs[0].Id);
        page.WaitForAssertion(() => Assert.Equal(["Delta", "Charlie", "Bravo"], Titles(page)));
    }

    [Fact]
    public async Task AfterAHeartAddedElsewhere_AMoveStillLandsBeforeTheSongItWasDroppedOn()
    {
        var page = await RenderFavoritesAsync();
        await Services.GetRequiredService<SavedItemService>().ToggleAsync(ProfileEntityKind.Song, Delta);
        page.WaitForAssertion(() => Assert.Equal(["Delta", "Charlie", "Alpha", "Bravo"], Titles(page)));

        page.FindAll(".song-table__row").Single(row => row.TextContent.Contains("Alpha", StringComparison.Ordinal))
            .KeyDown(new KeyboardEventArgs { Key = "ArrowUp", AltKey = true });

        page.WaitForAssertion(() => Assert.Equal(["Delta", "Alpha", "Charlie", "Bravo"], Titles(page)));
        Assert.Equal([(_songs[0].Id, (Guid?)_songs[2].Id)], _moves);
    }

    [Fact]
    public async Task ADragInProgress_SurvivesAHeartAddedElsewhere_AndDropsOnTheRowItWasOver()
    {
        var page = await RenderFavoritesAsync();
        AngleSharp.Dom.IElement RowOf(string title) =>
            page.FindAll(".song-table__row").Single(row => row.TextContent.Contains(title, StringComparison.Ordinal));

        RowOf("Charlie").DragStart();
        RowOf("Bravo").DragOver();
        await Services.GetRequiredService<SavedItemService>().ToggleAsync(ProfileEntityKind.Song, Delta);
        page.WaitForAssertion(() => Assert.Equal(["Delta", "Charlie", "Alpha", "Bravo"], Titles(page)));
        RowOf("Bravo").Drop();

        page.WaitForAssertion(() => Assert.Equal(["Delta", "Alpha", "Charlie", "Bravo"], Titles(page)));
        Assert.Equal([(_songs[2].Id, (Guid?)_songs[1].Id)], _moves);
    }

    [Fact]
    public async Task AFailedMove_ReadsTheListAgain_AndSaysSoInPlace()
    {
        var page = await RenderFavoritesAsync();
        var api = Services.GetRequiredService<IEngineApiClient>();
        ((EngineApiClientStub)(object)api).SetHandler(nameof(IEngineApiClient.MoveFavoriteSongAsync), _ => Task.FromResult(false));
        // Bravo was un-hearted somewhere this page was not told about, so what it shows is out of date.
        _hearted.Remove(_songs[1].Id);

        page.FindAll(".song-table__row").Single(row => row.TextContent.Contains("Alpha", StringComparison.Ordinal))
            .KeyDown(new KeyboardEventArgs { Key = "ArrowUp", AltKey = true });

        page.WaitForAssertion(() => Assert.Equal(["Charlie", "Alpha"], Titles(page)));
        Assert.NotEmpty(page.FindAll("[data-listen-notice]"));
    }

    [Fact]
    public async Task OnceTheFavoritesPageIsGone_AHeartChangedElsewhereIsNoLongerReadByIt()
    {
        await RenderFavoritesAsync();
        var api = (EngineApiClientStub)(object)Services.GetRequiredService<IEngineApiClient>();
        await DisposeComponentsAsync();
        var readsBefore = api.Calls.Count(call => call == nameof(IEngineApiClient.GetSavedItemsAsync));

        await Services.GetRequiredService<SavedItemService>().RemoveAsync(ProfileEntityKind.Song, _songs[0].Id);
        await Task.Delay(100);

        Assert.Equal(readsBefore, api.Calls.Count(call => call == nameof(IEngineApiClient.GetSavedItemsAsync)));
    }

    [Fact]
    public async Task WhenTheEngineRefusesTheMove_TheListKeepsItsOrderAndAQuietNoticeSaysSo()
    {
        var page = await RenderFavoritesAsync();
        var api = Services.GetRequiredService<IEngineApiClient>();
        ((EngineApiClientStub)(object)api).SetHandler(nameof(IEngineApiClient.MoveFavoriteSongAsync), _ => Task.FromResult(false));

        page.FindAll(".song-table__row").Single(row => row.TextContent.Contains("Alpha", StringComparison.Ordinal))
            .KeyDown(new KeyboardEventArgs { Key = "ArrowUp", AltKey = true });

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-listen-notice]")));
        Assert.Equal(["Charlie", "Alpha", "Bravo"], Titles(page));
    }

    [Fact]
    public void NoListenRailLinksToTheOldForMeFavoritesView()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        var pages = Path.Combine(dir!.FullName, "src", "MediaEngine.Web", "Components", "Pages");
        foreach (var file in Directory.GetFiles(pages, "Listen*.*").Where(path => path.EndsWith(".cs") || path.EndsWith(".razor")))
        {
            Assert.DoesNotContain("view=favorites", File.ReadAllText(file), StringComparison.Ordinal);
        }

        Assert.Contains(FavoritesRoute, File.ReadAllText(Path.Combine(pages, "ListenBrowsePage.razor")), StringComparison.Ordinal);
    }

    private sealed class DefaultPreferences : IUserPlaybackPreferencesAccessor
    {
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) =>
            Task.FromResult<UserPlaybackSettingsDto?>(UserPlaybackSettingsDto.CreateDefaults(ActiveId));

        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }
}
