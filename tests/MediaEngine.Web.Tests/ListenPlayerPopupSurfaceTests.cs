namespace MediaEngine.Web.Tests;

public sealed class ListenPlayerPopupSurfaceTests
{
    [Fact]
    public void PopupUsesTheSharedFullPlayerWithoutExitControlsAndRetainsTheCapturedBookmarkBridge()
    {
        var razor = Source("Components/Pages/ListenPlayerPopupPage.razor");
        var full = Source("Components/Listen/PlaybackFullPlayer.razor");
        Assert.Contains("<PlaybackFullPlayer", razor);
        Assert.Contains("IsPopup=\"true\"", razor);
        foreach (var exit in new[] { "OnCollapse=", "ClosePopupWindowAsync", "Close player window", "ClosePlayer", "closeOwnWindow", "<PlaybackPopoutShell" })
        {
            Assert.DoesNotContain(exit, razor);
        }
        Assert.Contains("@if (OnCollapse.HasDelegate)", full);
        Assert.Contains("<PlaybackPanelCard", full);
        Assert.Contains("<AudiobookBookmarkDialog", razor);
        Assert.Contains("ListenPlaybackCommandActionsClient", razor);
        Assert.Contains("await actions.OpenAsync(context)", razor);
        Assert.Contains("actions.GetAuthorizedAssetIds(context)", razor);
        Assert.Contains("IsSameBookmarkSubject", razor);
        Assert.Contains("await actions.CloseAsync(context)", razor);
        Assert.DoesNotContain("_snapshot.AudiobookBookmarks", razor);
        Assert.Contains("listenPlayback.returnToVideo", razor);
        Assert.DoesNotMatch("(?i)<(?:audio|video)(?=\\s|>)", razor + full);
    }

    [Fact]
    public void PopupUsesOwnerAuthorizedIdentityNavigationAndFreshAddressedPassiveCleanup()
    {
        var razor = Source("Components/Pages/ListenPlayerPopupPage.razor");
        var navigation = Source("Services/Playback/PlaybackIdentityNavigation.cs");
        var link = Source("Components/Listen/PlaybackIdentityLink.razor");
        var js = Source("wwwroot/app.js");
        Assert.Contains("_snapshot.PlaybackRequestVersion != previousVersion", razor);
        Assert.Contains("LyricsPresenter.Observe(_snapshot, PopupPanelCommands)", razor);
        Assert.Contains("GetDetailPageAsync", navigation);
        Assert.Contains("GetCollectionSummaryAsync", navigation);
        Assert.Contains("return Current()", navigation);
        Assert.Contains("ListenPlaybackIdentityRoutes.Album(item)", navigation);
        Assert.Contains("NavigateIdentityAsync(displayed, kind, id)", link);
        Assert.Contains("href=", link);
        Assert.Contains("playback-identity-link.js", link);
        Assert.DoesNotMatch(@"opener\.location(?:\.href)?\s*=(?!=)", js);
        Assert.DoesNotContain("closeOwnWindow", js);
        Assert.Contains("popupWidth: 420", js);
        Assert.Contains("popupHeight: 780", js);
        Assert.Contains("availWidth", js);
        Assert.Contains("tuvimaPopupStateSync.getLatestState(stateKey)", js);
        Assert.Contains("requestLatestState(function (json)", js);
        Assert.Contains("popupWindowId: windowId", js);
        Assert.Contains("notifyOwner('register-popup')", js);
        Assert.Contains("notifyOwner('popup-closed')", js);
        Assert.Contains("listenPlayback.registerPopupWindow\", OwnerRecipientId", razor);
    }

    private static string Source(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }
        return File.ReadAllText(Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException(), "src/MediaEngine.Web", path));
    }
}
