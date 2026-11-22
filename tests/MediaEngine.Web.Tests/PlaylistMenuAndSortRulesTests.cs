using Bunit;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The playlist "..." menu (spec listen-playlists D7) and the Sort choices of a playlist's own page.</summary>
public sealed class PlaylistMenuAndSortRulesTests : AsyncBunitContext
{
    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());

    public PlaylistMenuAndSortRulesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_device);
    }

    private static ManagedCollectionViewModel Collection(string type, string resolution = "materialized", bool? pinned = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Road trip",
        CollectionType = type,
        Resolution = resolution,
        IsPinned = pinned,
    };

    [Fact]
    public void AHandMadePlaylistsMenu_HasRenameDetailsDuplicatePinAndDeleteLast()
    {
        Assert.Equal(
            [PlaylistMenuAction.Rename, PlaylistMenuAction.EditDetails, PlaylistMenuAction.Duplicate, PlaylistMenuAction.TogglePin, PlaylistMenuAction.Delete],
            PlaylistMenuRules.ActionsFor(Collection("Playlist")));
    }

    [Fact]
    public void ASmartListsMenu_AddsEditRulesFirst_AndLeavesOutDuplicateBecauseARuleListHasNoSongsOfItsOwn()
    {
        Assert.Equal(
            [PlaylistMenuAction.EditRules, PlaylistMenuAction.Rename, PlaylistMenuAction.EditDetails, PlaylistMenuAction.TogglePin, PlaylistMenuAction.Delete],
            PlaylistMenuRules.ActionsFor(Collection("Smart", resolution: "query")));
    }

    [Theory]
    [InlineData("System")]
    [InlineData("Mix")]
    [InlineData("PlaylistFolder")]
    public void ABuiltInListHasNoMenu(string type)
    {
        Assert.Empty(PlaylistMenuRules.ActionsFor(Collection(type)));
    }

    [Fact]
    public async Task ThePlaylistMenuComponent_ReadsUnpinOncePinned_AndHandsThePlaylistBackWhenAnItemIsChosen()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        Render<MediaEngine.Web.Components.Shared.AppPopoverHost>();
        var playlist = Collection("Playlist", pinned: true);
        ManagedCollectionViewModel? pinned = null;

        var menu = Render<PlaylistActionsMenu>(parameters => parameters
            .Add(component => component.Playlist, playlist)
            .Add(component => component.OnTogglePin, EventCallback.Factory.Create<ManagedCollectionViewModel>(this, chosen => pinned = chosen)));
        menu.Find(".app-row-menu__trigger").Click();
        menu.WaitForAssertion(() => Assert.NotEmpty(menu.FindAll("button.app-menu-item")));
        Assert.DoesNotContain(menu.FindAll("button.app-menu-item"), item => item.TextContent.Trim() == "Pin to top");
        menu.FindAll("button.app-menu-item").Single(item => item.TextContent.Trim() == "Unpin").Click();

        menu.WaitForAssertion(() => Assert.Same(playlist, pinned));
    }

    private static SongRow Row(string title, string artist, string album, double seconds, int daysAgo) => new()
    {
        Id = Guid.NewGuid().ToString("D"),
        Source = SongRowSource.PlaylistItem,
        Title = title,
        Artist = artist,
        Album = album,
        DurationSeconds = seconds,
        DateAdded = DateTimeOffset.UtcNow.AddDays(-daysAgo),
        PlaylistItemId = Guid.NewGuid(),
    };

    private static List<string> TitlesIn(IEnumerable<SongRow> rows, IReadOnlyList<Guid> order) =>
        [.. order.Select(id => rows.Single(row => row.PlaylistItemId == id).Title)];

    [Fact]
    public void EachSortChoice_OrdersTheSongsByItsColumn()
    {
        List<SongRow> rows =
        [
            Row("Charlie", "Zed", "Beta", 200, 1),
            Row("Alpha", "Yan", "Gamma", 100, 3),
            Row("Bravo", "Xia", "Alpha", 300, 2),
        ];

        Assert.Equal(["Alpha", "Bravo", "Charlie"], TitlesIn(rows, PlaylistSortRules.Order(rows, PlaylistSortRules.Choose(null, PlaylistSortKey.Title))));
        Assert.Equal(["Bravo", "Alpha", "Charlie"], TitlesIn(rows, PlaylistSortRules.Order(rows, PlaylistSortRules.Choose(null, PlaylistSortKey.Artist))));
        Assert.Equal(["Bravo", "Charlie", "Alpha"], TitlesIn(rows, PlaylistSortRules.Order(rows, PlaylistSortRules.Choose(null, PlaylistSortKey.Album))));
    }

    [Fact]
    public void DateAddedStartsNewestFirstAndLengthLongestFirst_AndChoosingTheSameOneAgainFlipsIt()
    {
        List<SongRow> rows =
        [
            Row("Charlie", "Zed", "Beta", 200, 1),
            Row("Alpha", "Yan", "Gamma", 100, 3),
            Row("Bravo", "Xia", "Alpha", 300, 2),
        ];

        var newest = PlaylistSortRules.Choose(null, PlaylistSortKey.DateAdded);
        Assert.Equal(["Charlie", "Bravo", "Alpha"], TitlesIn(rows, PlaylistSortRules.Order(rows, newest)));
        var oldest = PlaylistSortRules.Choose(newest, PlaylistSortKey.DateAdded);
        Assert.Equal(["Alpha", "Bravo", "Charlie"], TitlesIn(rows, PlaylistSortRules.Order(rows, oldest)));

        var longest = PlaylistSortRules.Choose(null, PlaylistSortKey.Length);
        Assert.Equal(["Bravo", "Charlie", "Alpha"], TitlesIn(rows, PlaylistSortRules.Order(rows, longest)));
    }
}
