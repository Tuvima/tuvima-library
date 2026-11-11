using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Contracts.ProfileState;
using MediaEngine.Domain.Enums;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Components.MediaTiles;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The "..." button on a cover (One tile design D3, D4): the menu holds Add to My List, Restart (once started), Open Universe, Edit (desktop)
/// and Delete (administrator on a desktop); Edit opens the pop-over over Home without navigating. There is no "+".
/// </summary>
public sealed class MediaTileActionsTests : AsyncBunitContext
{
    private static readonly Guid MovieId = EditingModeTestData.MovieId;

    private readonly DeviceContextService _device = new(EngineApiClientStub.CreateDefault());
    private readonly DashboardSessionAccessor _session = new();
    private readonly List<ProfileSavedItemDto> _saved = [];
    private int _detailReads;

    public MediaTileActionsTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetDetailPageAsync), _ =>
            {
                _detailReads++;
                return Task.FromResult<DetailPageViewModel?>(new DetailPageViewModel
                {
                    Id = MovieId.ToString("D"),
                    EntityType = DetailEntityType.Movie,
                    Title = "Dune: Part One",
                    OverflowActions = [new DetailAction { Key = "edit", Label = "Edit this movie", Icon = "edit" }],
                });
            });
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorContextAsync), _ => Task.FromResult<MediaEditorContextDto?>(EditingModeTestData.MovieContext()));
            stub.SetHandler(nameof(IEngineApiClient.GetSavedItemsAsync), _ => Task.FromResult<IReadOnlyList<ProfileSavedItemDto>>([.. _saved]));
            stub.SetHandler(nameof(IEngineApiClient.GetSavedItemAsync), args =>
                Task.FromResult(_saved.FirstOrDefault(item => item.EntityId == (Guid)args![1]!)));
            stub.SetHandler(nameof(IEngineApiClient.SaveItemAsync), args =>
            {
                _saved.Add(new ProfileSavedItemDto((ProfileEntityKind)args![0]!, (Guid)args[1]!, DateTimeOffset.UtcNow, null));
                return Task.FromResult<ProfileStateMutationDto?>(new((ProfileEntityKind)args[0]!, (Guid)args[1]!, true, null, DateTimeOffset.UtcNow));
            });
            stub.SetHandler(nameof(IEngineApiClient.RemoveSavedItemAsync), args =>
                Task.FromResult(_saved.RemoveAll(item => item.EntityId == (Guid)args![1]!) > 0));
        });
        Services.AddSingleton(api);
        Services.AddSingleton(_device);
        Services.AddSingleton(_session);
        Services.AddSingleton(new SavedItemService(api));
    }

    private static MediaTileViewModel Movie(double? progress = 40) => new()
    {
        Id = MovieId,
        WorkId = MovieId,
        Title = "Dune: Part One",
        MediaKind = "Movie",
        ProgressPct = progress,
        DetailsNavigationUrl = "/details/movie/dune",
        NavigationUrl = "/details/movie/dune",
        PrimaryNavigationUrl = "/watch/dune",
    };

    private Task UseDevice(string formFactor) => formFactor switch
    {
        "phone" => _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true)),
        "tablet" => _device.InitialiseAsync(new DeviceContextSnapshot("mobile", "tablet", true)),
        _ => _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true)),
    };

    private void SetAdministrator(bool administrator)
    {
        var id = Guid.NewGuid();
        _session.Set("test-session-token", id, id, Guid.NewGuid(),
            new DashboardAuthorityResponse(id, id, true, true, 1, 1, administrator, administrator, null, 1, [], [], []));
    }

    private IRenderedComponent<MediaTile> RenderTile(MediaTileViewModel item) =>
        Render<MediaTile>(parameters => parameters.Add(tile => tile.Item, item));

    private static IReadOnlyList<string> MenuLabels(IRenderedComponent<MediaTile> cut)
    {
        cut.Find("button[aria-label='More actions']").Click();
        return [.. cut.FindAll("[role='menuitem']").Select(item => item.TextContent.Trim())];
    }

    [Fact]
    public async Task TheMenu_HoldsMyListRestartEditAndDelete_ForAnAdministratorOnADesktop()
    {
        await UseDevice("desktop");
        SetAdministrator(true);

        var cut = RenderTile(Movie());

        Assert.Equal(["Add to My List", "Restart", "Edit", "Delete from library…"], MenuLabels(cut));
        Assert.Single(cut.FindAll("[data-media-tile-actions]"));
    }

    [Fact]
    public async Task Restart_IsOnlyOfferedOnceTheTitleHasBeenStarted()
    {
        await UseDevice("desktop");
        SetAdministrator(true);

        var cut = RenderTile(Movie(progress: null));

        Assert.Equal(["Add to My List", "Edit", "Delete from library…"], MenuLabels(cut));
    }

    [Fact]
    public async Task Delete_IsHiddenFromSomeoneWhoIsNotAnAdministrator()
    {
        await UseDevice("desktop");
        SetAdministrator(false);

        var cut = RenderTile(Movie());

        Assert.Equal(["Add to My List", "Restart", "Edit"], MenuLabels(cut));
    }

    [Fact]
    public async Task OpenUniverse_IsOnlyOfferedWhenTheEngineNamedAUniverse_AndOpensItsPage()
    {
        await UseDevice("phone");
        var navigation = Services.GetRequiredService<NavigationManager>();

        var without = RenderTile(Movie());
        Assert.DoesNotContain("Open Universe", MenuLabels(without));

        var withUniverse = RenderTile(new MediaTileViewModel
        {
            Id = MovieId,
            WorkId = MovieId,
            Title = "John Wick",
            MediaKind = "Movie",
            UniverseQid = "Q1234",
            DetailsNavigationUrl = "/details/movie/wick",
            NavigationUrl = "/details/movie/wick",
        });
        Assert.Equal(["Add to My List", "Open Universe"], MenuLabels(withUniverse));
        withUniverse.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Open Universe").Click();

        withUniverse.WaitForAssertion(() => Assert.EndsWith("/universe/Q1234", navigation.Uri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_OpensTheTitleFromTheBeginning()
    {
        await UseDevice("desktop");
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = RenderTile(Movie());

        cut.Find("button[aria-label='More actions']").Click();
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Restart").Click();

        cut.WaitForAssertion(() => Assert.EndsWith("/watch/dune?restart=true", navigation.Uri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Edit_OpensThePopOverOverHome_WithoutNavigatingAway()
    {
        await UseDevice("desktop");
        var navigation = Services.GetRequiredService<NavigationManager>();
        var before = navigation.Uri;
        var popover = Render<EditorPopover>();
        var host = Services.GetRequiredService<EditorHost>();
        var cut = RenderTile(Movie());

        cut.Find("button[aria-label='More actions']").Click();
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Edit").Click();

        popover.WaitForAssertion(() => Assert.True(host.IsOpen));
        Assert.Equal(MovieId, host.Session.ActiveItem?.EntityId);
        Assert.Equal(before, navigation.Uri);
        Assert.Equal(1, _detailReads);
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("tablet")]
    public async Task OnAPhoneOrTablet_EditAndDeleteAreHidden_AndOnlyMyListAndRestartRemain(string formFactor)
    {
        await UseDevice(formFactor);
        SetAdministrator(true);

        var cut = RenderTile(Movie());

        Assert.Equal(["Add to My List", "Restart"], MenuLabels(cut));
        Assert.Equal(0, _detailReads);
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("tablet")]
    public async Task OnAPhoneOrTablet_AnUnstartedTitleHasOnlyAddToMyList(string formFactor)
    {
        await UseDevice(formFactor);
        SetAdministrator(true);

        var cut = RenderTile(Movie(progress: null));

        Assert.Equal(["Add to My List"], MenuLabels(cut));
        Assert.Empty(cut.FindAll("button.media-tile-action--list"));
    }

    [Fact]
    public async Task AddToMyList_SavesTheTitle_AndTheMenuThenOffersRemoving()
    {
        await UseDevice("desktop");
        var cut = RenderTile(Movie());
        cut.Find("button[aria-label='More actions']").Click();

        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Add to My List").Click();

        cut.WaitForAssertion(() => Assert.Single(_saved));
        Assert.Equal(ProfileEntityKind.Movie, _saved[0].EntityKind);
        var again = RenderTile(Movie());
        again.WaitForAssertion(() => Assert.Contains("Remove from My List", MenuLabels(again)));
    }

    [Fact]
    public async Task AddToMyList_ReadsRemoveForATitleAlreadyOnMyList_AndRemovesIt()
    {
        await UseDevice("desktop");
        _saved.Add(new ProfileSavedItemDto(ProfileEntityKind.Movie, MovieId, DateTimeOffset.UtcNow, null));
        var cut = RenderTile(Movie());
        cut.WaitForAssertion(() => Assert.Contains("Remove from My List", MenuLabels(cut)));

        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Remove from My List").Click();

        cut.WaitForAssertion(() => Assert.Empty(_saved));
    }

    [Fact]
    public async Task TheCover_HasNoPlusButton()
    {
        await UseDevice("desktop");
        var cut = RenderTile(Movie());

        Assert.Empty(cut.FindAll("button.media-tile-action--list"));
        Assert.Single(cut.FindAll("button[aria-label='More actions']"));
    }

    [Fact]
    public async Task AShowTile_SavesTheShow_AndAPersonTileGetsNoButtons_WhileAPlaylistSavesAsAPlaylist()
    {
        await UseDevice("desktop");
        var show = new MediaTileViewModel { Id = Guid.NewGuid(), WorkId = Guid.NewGuid(), Title = "Severance", MediaKind = "TV" };
        var target = MediaTileActionTarget.For(show);

        Assert.NotNull(target);
        Assert.Equal(ProfileEntityKind.TvShow, target.SavedKind);
        Assert.Equal(DetailEntityType.TvShow, target.EntityType);
        Assert.True(target.IsGroup);
        var playlist = MediaTileActionTarget.For(new MediaTileViewModel { Id = Guid.NewGuid(), Title = "Mix", MediaKind = "Music", IsPlaylist = true });
        Assert.NotNull(playlist);
        Assert.Equal(ProfileEntityKind.Playlist, playlist.SavedKind);
        Assert.False(playlist.CanEdit);
        Assert.False(playlist.CanDelete);
        Assert.Null(MediaTileActionTarget.For(new MediaTileViewModel { Id = Guid.NewGuid(), Title = "Frank", MediaKind = "Shelf" }));
    }

    [Theory]
    [InlineData("Movie")]
    [InlineData("TV")]
    [InlineData("Video")]
    [InlineData("Videos")]
    [InlineData("Music")]
    [InlineData("Book")]
    [InlineData("Audiobook")]
    [InlineData("Comic")]
    public void EveryKindOnHomeGetsAMenu(string mediaKind)
        => Assert.NotNull(MediaTileActionTarget.For(new MediaTileViewModel { Id = Guid.NewGuid(), WorkId = Guid.NewGuid(), Title = "Anything", MediaKind = mediaKind }));

    [Fact]
    public void AShowOrAlbumDrawnAsAGroupTile_KeepsItsFullMenu_WhileASeriesGroupGetsOnlyWhatApplies()
    {
        var showGroup = new MediaTileViewModel { Id = Guid.NewGuid(), WorkId = Guid.NewGuid(), Title = "Severance", MediaKind = "TV", IsCollection = true, Presentation = MediaTilePresentation.TvSeries };
        var movieSeries = new MediaTileViewModel { Id = Guid.NewGuid(), CollectionId = Guid.NewGuid(), Title = "Dune", MediaKind = "Movie", IsCollection = true, Presentation = MediaTilePresentation.MovieSeries };
        var audiobookSeries = new MediaTileViewModel { Id = Guid.NewGuid(), CollectionId = Guid.NewGuid(), Title = "Dune", MediaKind = "Audiobook", IsCollection = true, Presentation = MediaTilePresentation.AudiobookSeries };

        var show = MediaTileActionTarget.For(showGroup);
        var series = MediaTileActionTarget.For(movieSeries);
        Assert.NotNull(show);
        Assert.True(show.CanDelete);
        Assert.NotNull(series);
        Assert.Equal(DetailEntityType.MovieSeries, series.EntityType);
        Assert.False(series.CanSave);
        Assert.True(series.CanEdit);
        Assert.False(series.CanDelete);
        Assert.False(MediaTileActionTarget.For(audiobookSeries)!.CanEdit);
    }

    [Fact]
    public async Task ACuratedCollectionAndAUniverse_HoldMyListAndEdit_ButNeverDelete()
    {
        await UseDevice("desktop");
        SetAdministrator(true);
        var collection = new MediaTileViewModel { Id = Guid.NewGuid(), CollectionId = Guid.NewGuid(), Title = "Halloween", MediaKind = "Collection", IsCollection = true, UseGroupTile = true, GroupKind = MediaTileGroupKind.Collection, TileImageUrl = "/art/h.jpg", NavigationUrl = "/details/collection/h" };
        var universe = new MediaTileViewModel { Id = Guid.NewGuid(), CollectionId = Guid.NewGuid(), Title = "Dune", MediaKind = "Collection", IsCollection = true, UseGroupTile = true, GroupKind = MediaTileGroupKind.Universe, TileImageUrl = "/art/d.jpg", NavigationUrl = "/universe/Q1" };

        Assert.Equal(["Add to My List", "Edit"], MenuLabels(RenderTile(collection)));
        Assert.Equal(["Add to My List", "Edit"], MenuLabels(RenderTile(universe)));
    }

    [Fact]
    public async Task ASeriesTile_HoldsOnlyEdit_OnADesktop_AndDrawsNoMenuWhereNothingApplies()
    {
        var series = new MediaTileViewModel { Id = Guid.NewGuid(), CollectionId = Guid.NewGuid(), Title = "Dune Novels", MediaKind = "Book", IsCollection = true, UseGroupTile = true, GroupKind = MediaTileGroupKind.Series, Presentation = MediaTilePresentation.BookSeries, TileImageUrl = "/art/d.jpg", NavigationUrl = "/details/bookseries/dune" };

        await UseDevice("desktop");
        SetAdministrator(true);
        Assert.Equal(["Edit"], MenuLabels(RenderTile(series)));

        await UseDevice("phone");
        Assert.Empty(RenderTile(series).FindAll("button[aria-label='More actions']"));
    }

    [Fact]
    public async Task APlaylistTile_HasTheMenu_WithAddToMyListAndNothingElse()
    {
        await UseDevice("desktop");
        SetAdministrator(true);
        var playlist = new MediaTileViewModel { Id = Guid.NewGuid(), CollectionId = Guid.NewGuid(), Title = "Road trip", MediaKind = "Music", IsPlaylist = true, NavigationUrl = "/listen/music/playlists/1" };

        var cut = RenderTile(playlist);

        Assert.Equal(["Add to My List"], MenuLabels(cut));
        Assert.Single(cut.FindAll("[data-media-tile-actions]"));
    }

    [Fact]
    public async Task APlaylistTile_LeavesTheTopLeftSlotForNew_AndNeverNamesItsKindThere()
    {
        await UseDevice("desktop");
        var fresh = new MediaTileViewModel { Id = Guid.NewGuid(), CollectionId = Guid.NewGuid(), Title = "Road trip", MediaKind = "Music", IsPlaylist = true, NewLabel = "New", NavigationUrl = "/listen/music/playlists/1" };
        var quiet = new MediaTileViewModel { Id = Guid.NewGuid(), CollectionId = Guid.NewGuid(), Title = "Road trip", MediaKind = "Music", IsPlaylist = true, NavigationUrl = "/listen/music/playlists/1" };

        Assert.Equal("New", RenderTile(fresh).Find(".media-tile-label--status").TextContent.Trim());
        Assert.Empty(RenderTile(quiet).FindAll(".media-tile-label--status"));
    }

    [Fact]
    public void AVideoTileShowsNoNewPill()
    {
        var video = new MediaTileViewModel { Id = Guid.NewGuid(), Title = "Clip", MediaKind = "Video", NewLabel = "New" };
        var movie = new MediaTileViewModel { Id = Guid.NewGuid(), Title = "Dune", MediaKind = "Movie", NewLabel = "New" };

        Assert.Null(MediaTileLabels.Status(video, "Getting details"));
        Assert.Equal("New", MediaTileLabels.Status(movie, "Getting details"));
    }
}
