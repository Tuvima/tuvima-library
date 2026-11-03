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
/// The "+" and "..." buttons on a cover (Home, detail and tiles spec D6, D8): the menu holds only Restart (once started), Edit (desktop)
/// and Delete (administrator on a desktop); Edit opens the pop-over over Home without navigating; "+" toggles My List.
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
        Render<MediaTile>(parameters => parameters.Add(tile => tile.Item, item).Add(tile => tile.IsHomeSurface, true));

    private static IReadOnlyList<string> MenuLabels(IRenderedComponent<MediaTile> cut)
    {
        cut.Find("button[aria-label='More actions']").Click();
        return [.. cut.FindAll("[role='menuitem']").Select(item => item.TextContent.Trim())];
    }

    [Fact]
    public async Task TheMenu_HoldsOnlyRestartEditAndDelete_ForAnAdministratorOnADesktop()
    {
        await UseDevice("desktop");
        SetAdministrator(true);

        var cut = RenderTile(Movie());

        Assert.Equal(["Restart", "Edit", "Delete from library…"], MenuLabels(cut));
        Assert.Single(cut.FindAll("[data-media-tile-actions]"));
    }

    [Fact]
    public async Task Restart_IsOnlyOfferedOnceTheTitleHasBeenStarted()
    {
        await UseDevice("desktop");
        SetAdministrator(true);

        var cut = RenderTile(Movie(progress: null));

        Assert.Equal(["Edit", "Delete from library…"], MenuLabels(cut));
    }

    [Fact]
    public async Task Delete_IsHiddenFromSomeoneWhoIsNotAnAdministrator()
    {
        await UseDevice("desktop");
        SetAdministrator(false);

        var cut = RenderTile(Movie());

        Assert.Equal(["Restart", "Edit"], MenuLabels(cut));
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
    public async Task OnAPhoneOrTablet_EditAndDeleteAreHidden_AndOnlyRestartRemains(string formFactor)
    {
        await UseDevice(formFactor);
        SetAdministrator(true);

        var cut = RenderTile(Movie());

        Assert.Equal(["Restart"], MenuLabels(cut));
        Assert.Equal(0, _detailReads);
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("tablet")]
    public async Task OnAPhoneOrTablet_AnUnstartedTitleHasNoMenuAtAll(string formFactor)
    {
        await UseDevice(formFactor);
        SetAdministrator(true);

        var cut = RenderTile(Movie(progress: null));

        Assert.Empty(cut.FindAll("button[aria-label='More actions']"));
        Assert.Single(cut.FindAll("button.media-tile-action--list"));
    }

    [Fact]
    public async Task Plus_AddsToMyList_AndAgainRemovesIt()
    {
        await UseDevice("desktop");
        var cut = RenderTile(Movie());
        var plus = () => cut.Find("button.media-tile-action--list");
        Assert.Equal("Add to My List", plus().GetAttribute("aria-label"));
        Assert.Equal("false", plus().GetAttribute("aria-pressed"));

        plus().Click();

        cut.WaitForAssertion(() => Assert.Equal("true", plus().GetAttribute("aria-pressed")));
        Assert.Equal("Remove from My List", plus().GetAttribute("aria-label"));
        Assert.Single(_saved);
        Assert.Equal(ProfileEntityKind.Movie, _saved[0].EntityKind);

        plus().Click();

        cut.WaitForAssertion(() => Assert.Equal("false", plus().GetAttribute("aria-pressed")));
        Assert.Empty(_saved);
    }

    [Fact]
    public async Task Plus_StartsTickedForATitleAlreadyOnMyList()
    {
        await UseDevice("desktop");
        _saved.Add(new ProfileSavedItemDto(ProfileEntityKind.Movie, MovieId, DateTimeOffset.UtcNow, null));

        var cut = RenderTile(Movie());

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("button.media-tile-action--list").GetAttribute("aria-pressed")));
    }

    [Fact]
    public async Task AShowTile_SavesTheShow_AndAPersonOrPlaylistTileGetsNoButtons()
    {
        await UseDevice("desktop");
        var show = new MediaTileViewModel { Id = Guid.NewGuid(), WorkId = Guid.NewGuid(), Title = "Severance", MediaKind = "TV" };
        var target = MediaTileActionTarget.For(show);

        Assert.NotNull(target);
        Assert.Equal(ProfileEntityKind.TvShow, target.SavedKind);
        Assert.Equal(DetailEntityType.TvShow, target.EntityType);
        Assert.True(target.IsGroup);
        Assert.Null(MediaTileActionTarget.For(new MediaTileViewModel { Id = Guid.NewGuid(), Title = "Mix", MediaKind = "Music", IsPlaylist = true }));
        Assert.Null(MediaTileActionTarget.For(new MediaTileViewModel { Id = Guid.NewGuid(), Title = "Frank", MediaKind = "Shelf" }));
    }
}
