using Bunit;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// What a failed playlist action on the Listen page says and where (design system, "Feedback after an action"): an alert in place at the top of
/// the page that stays until dismissed or the next try, never a pop-up. A playlist that worked is shown by the rail itself, so it says nothing more.
/// </summary>
public sealed partial class UiShellRenderTests
{
    private static MediaSectionNavigationManageEvent PlaylistManageEvent() =>
        new(
            new MediaSectionNavigationItem("Road trip", "/listen/music/playlists/x", "playlist"),
            new MediaSectionNavigationItemManagement(Guid.NewGuid(), "playlist"));

    private bool _deletePlaylistSucceeds;

    private IRenderedComponent<IComponent> RenderListenPageWithPlaylistDelete()
    {
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.DeleteCollectionAsync), _ => Task.FromResult(_deletePlaylistSucceeds))));
        return RenderListenBrowsePageWithProviders("music");
    }

    [Fact]
    public async Task ListenBrowsePage_WhenAPlaylistCannotBeDeleted_SaysSoInPlaceAndShowsNoPopUp()
    {
        _deletePlaylistSucceeds = false;
        var cut = RenderListenPageWithPlaylistDelete();
        cut.WaitForAssertion(() => Assert.NotNull(cut.FindComponent<MediaLanePage>()));
        var lane = cut.FindComponent<MediaLanePage>();

        var result = await cut.InvokeAsync(() => lane.Instance.OnNavigationItemDelete!(PlaylistManageEvent()));

        Assert.False(result);
        cut.WaitForAssertion(() => Assert.Contains("could not be deleted", cut.Find("[data-lane-notice]").TextContent, StringComparison.Ordinal));
        Assert.Empty(Services.GetRequiredService<AppToastService>().Toasts);

        // The alert goes away when dismissed.
        cut.Find("[data-lane-notice] button").Click();
        Assert.Empty(cut.FindAll("[data-lane-notice]"));
    }

    [Fact]
    public async Task ListenBrowsePage_WhenAPlaylistIsDeleted_AddsNoConfirmationBecauseTheRailAlreadyShowsIt()
    {
        _deletePlaylistSucceeds = true;
        var cut = RenderListenPageWithPlaylistDelete();
        cut.WaitForAssertion(() => Assert.NotNull(cut.FindComponent<MediaLanePage>()));
        var lane = cut.FindComponent<MediaLanePage>();

        var result = await cut.InvokeAsync(() => lane.Instance.OnNavigationItemDelete!(PlaylistManageEvent()));

        Assert.True(result);
        Assert.Empty(cut.FindAll("[data-lane-notice]"));
        Assert.Empty(Services.GetRequiredService<AppToastService>().Toasts);
    }
}
