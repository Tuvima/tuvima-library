namespace MediaEngine.Web.Tests;

public sealed class ListenPlayerPopupSurfaceTests
{
    [Fact]
    public void PopupHasCenteredHistoryCompositionAndTruthfulVideoHandoff()
    {
        var root = FindRepoRoot();
        var razor = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Pages/ListenPlayerPopupPage.razor"));
        var css = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Pages/ListenPlayerPopupPage.razor.css"));
        var js = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/wwwroot/app.js"));

        Assert.Contains("listen-popup__composition", razor, StringComparison.Ordinal);
        Assert.Contains("listen-popup__composition--with-context", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-popup__topbar-identity", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-popup__topbar", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("Close player window", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("ClosePopupWindowAsync", razor, StringComparison.Ordinal);
        Assert.Contains("Close playback context panel", razor, StringComparison.Ordinal);
        Assert.Contains("listen-popup__context", razor, StringComparison.Ordinal);
        Assert.Contains("role=\"tablist\"", razor, StringComparison.Ordinal);
        Assert.Contains("<AudiobookBookmarkDialog", razor, StringComparison.Ordinal);
        Assert.Contains("ListenPlaybackCommandActionsClient", razor, StringComparison.Ordinal);
        Assert.Contains("BookmarkCommandChannel.SendAsync(ownerRecipientId, request)", razor, StringComparison.Ordinal);
        Assert.Contains("CommandId = Guid.NewGuid()", razor, StringComparison.Ordinal);
        Assert.Contains("SenderId = _popupRecipientId", razor, StringComparison.Ordinal);
        Assert.Contains("RecipientId = ownerRecipientId", razor, StringComparison.Ordinal);
        Assert.Contains("reply.CommandId != request.CommandId", razor, StringComparison.Ordinal);
        Assert.Contains("reply.RecipientId != _popupRecipientId", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("listenPlayback.sendCommand", razor, StringComparison.Ordinal);
        Assert.Contains("await actions.OpenAsync(context)", razor, StringComparison.Ordinal);
        Assert.Contains("actions.GetAuthorizedAssetIds(context)", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("_snapshot.AudiobookBookmarks", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("PopupCommand", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("add-audiobook-bookmark", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("delete-audiobook-bookmark", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("play-audiobook-bookmark", razor, StringComparison.Ordinal);
        Assert.Contains("PlaybackContextRow Variant=\"history\"", razor, StringComparison.Ordinal);
        Assert.Contains("AudiobookHistoryDuration(item)", razor, StringComparison.Ordinal);
        Assert.Contains("PlaybackSessionController.ScopeAudiobookHistory", razor, StringComparison.Ordinal);
        Assert.Contains("min(70vw, 220px)", razor, StringComparison.Ordinal);
        Assert.Contains("min(76vw, 330px)", razor, StringComparison.Ordinal);
        Assert.Contains("<PlaybackRangeSlider Min=\"0\" Max=\"1\"", razor, StringComparison.Ordinal);
        Assert.Contains("ValueChanged=\"SetVolumeAsync\"", razor, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, 1fr) minmax(300px, 34vw)", css, StringComparison.Ordinal);
        Assert.Contains("grid-template-rows: minmax(0, 1fr)", css, StringComparison.Ordinal);
        Assert.Contains("overflow: hidden;", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 900px)", css, StringComparison.Ordinal);
        Assert.Contains("IsVideoMode", razor, StringComparison.Ordinal);
        Assert.Contains("ReturnToVideoAsync", razor, StringComparison.Ordinal);
        Assert.Contains("listenPlayback.returnToVideo", razor, StringComparison.Ordinal);
        Assert.Contains("function returnToVideo()", js, StringComparison.Ordinal);
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex("<audio(?=\\s|>)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), razor);
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex("<video(?=\\s|>)", System.Text.RegularExpressions.RegexOptions.IgnoreCase), razor);
    }

    [Fact]
    public void PopupRejectsLyricsFromObsoleteSnapshotAndUsesCanonicalIdentityRoutes()
    {
        var root = FindRepoRoot();
        var razor = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Pages/ListenPlayerPopupPage.razor"));
        var model = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Services/Playback/PlaybackModels.cs"));
        var js = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/wwwroot/app.js"));

        Assert.Contains("_snapshot.PlaybackRequestVersion != previousVersion", razor, StringComparison.Ordinal);
        Assert.Contains("IsCurrentLyricsRequest(assetId, workId, requestVersion)", razor, StringComparison.Ordinal);
        Assert.Contains("ListenPlaybackIdentityRoutes.Audiobook(current)", razor, StringComparison.Ordinal);
        Assert.Contains("ListenPlaybackIdentityRoutes.Artist(current)", razor, StringComparison.Ordinal);
        Assert.Contains("ListenPlaybackIdentityRoutes.Album(current)", razor, StringComparison.Ordinal);
        Assert.Contains("ListenPlaybackIdentityRoutes.Contributor(contributor)", razor, StringComparison.Ordinal);
        Assert.Contains("popupWidth: 1040", js, StringComparison.Ordinal);
        Assert.Contains("popupHeight: 780", js, StringComparison.Ordinal);
        Assert.Contains("availWidth", js, StringComparison.Ordinal);
        Assert.Contains("tuvimaPopupStateSync.getLatestState(stateKey)", js, StringComparison.Ordinal);
        Assert.Contains("requestLatestState(function (json)", js, StringComparison.Ordinal);
        Assert.Contains("action: 'request-state'", js, StringComparison.Ordinal);
        Assert.Contains("getStoredState: function ()", js, StringComparison.Ordinal);
        Assert.Contains("if (popupWindow && !popupWindow.closed)", js, StringComparison.Ordinal);
        Assert.Contains("popupWindow.focus();\n            }\n            return true;", js, StringComparison.Ordinal);
        Assert.Contains("AudiobookWorkId ?? item.AlbumWorkId ?? item.WorkId", model, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
