namespace MediaEngine.Web.Tests;

public sealed class VideoPlaybackSurfaceTests
{
    [Fact]
    public void ExpandedVideoKeepsMountedMediaAndUsesAnchoredChromeWithinContainerFullscreen()
    {
        var root = FindRepoRoot();
        var razor = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor"));
        var css = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor.css"));
        Assert.True(razor.IndexOf("<video", StringComparison.Ordinal) < razor.IndexOf("@if (Playback.IsVideoMode && Playback.IsVideoExpanded", StringComparison.Ordinal));
        Assert.Contains("<PlaybackVideoChrome", razor);
        Assert.Contains("data-playback-chrome-bottom", File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackVideoChrome.razor")));
        Assert.Contains("Back to details", razor);
        Assert.Contains("<PlaybackPopover Title=\"Captions\"", razor);
        Assert.Contains("<VideoContextPanel", razor);
        Assert.Contains("listenPlayback.toggleFullscreen\", _hostRef, _videoRef", razor);
        Assert.DoesNotContain("video-playback-host__tool-backdrop", razor);
        Assert.DoesNotContain("CurrentItem!.AssetId!.Value", razor);
        Assert.Contains("object-fit:contain", css);
        Assert.Contains("playback-video-controls", File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackVideoChrome.razor.css")));
        Assert.Contains("width:min(320px", css);
        Assert.Contains("<PlaybackSeekRail", razor);
        Assert.Contains("playback-video-controls__volume", razor);
        Assert.Contains("flex-wrap:wrap", File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackVideoChrome.razor.css")));
    }
    [Fact]
    public void CaptionSheetUsesObservedNativeAndHlsTrackInventory()
    {
        var root = FindRepoRoot();
        var razor = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor"));
        var script = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/wwwroot/app.js"));

        Assert.Contains("@foreach (var track in _captionTracks)", razor, StringComparison.Ordinal);
        Assert.Contains("IsActive=\"@(!_captionTracks.Any(track => track.Selected))\"", razor, StringComparison.Ordinal);
        Assert.Contains("CaptionTracks { get; set; }", razor, StringComparison.Ordinal);
        Assert.Contains("state.CaptionTracks", razor, StringComparison.Ordinal);
        Assert.Contains("readCaptionTrackChoices(element)", script, StringComparison.Ordinal);
        Assert.Contains("element._tuvimaHls?.subtitleTracks", script, StringComparison.Ordinal);
        Assert.Contains("element.textTracks?.addEventListener('change'", script, StringComparison.Ordinal);
        Assert.Contains("data-playback-asset-id=\"@Playback.CurrentItem?.AssetId\"", razor, StringComparison.Ordinal);
        Assert.Contains("Playback.CurrentItem?.AssetId != observedAssetId", razor, StringComparison.Ordinal);
        var chrome = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/wwwroot/js/playback-chrome.js"));
        Assert.DoesNotContain("updateAutomaticCaptionPlacement", script, StringComparison.Ordinal);
        Assert.Contains("cue.lineAlign = 'end'", chrome, StringComparison.Ordinal);
        Assert.Contains("state.restoreCues()", chrome, StringComparison.Ordinal);
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
