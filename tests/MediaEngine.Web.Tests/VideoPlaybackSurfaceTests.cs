namespace MediaEngine.Web.Tests;

public sealed class VideoPlaybackSurfaceTests
{
    [Fact]
    public void ExpandedVideoKeepsMountedMediaAndUsesAnchoredChromeWithinContainerFullscreen()
    {
        var root = FindRepoRoot();
        var razor = LocalizedSource.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Player/VideoPlaybackHost.razor"));
        var css = LocalizedSource.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Player/VideoPlaybackHost.razor.css"));
        Assert.True(razor.IndexOf("<video", StringComparison.Ordinal) < razor.IndexOf("@if (Playback.IsVideoMode && Playback.IsVideoExpanded", StringComparison.Ordinal));
        Assert.Contains("<PlaybackVideoChrome", razor);
        Assert.Contains("data-playback-chrome-bottom", LocalizedSource.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Player/PlaybackVideoChrome.razor")));
        Assert.Contains("\"close\" => ClosePlayerAsync()", razor);
        Assert.DoesNotContain("Back to details", razor);
        // Unified player v2 D12: one Audio & subtitles panel; the old Settings menu and the separate subtitle style button are gone from the video player.
        Assert.Contains("<AudioSubtitlesPanel ToolId=\"video-captions\"", razor);
        Assert.DoesNotContain("<PlayerSettingsMenu", razor);
        Assert.DoesNotContain("video-subtitle-style", razor);
        Assert.Contains("<EpisodesPanel", razor);
        Assert.Contains("listenPlayback.toggleFullscreen\", _hostRef, _videoRef", razor);
        Assert.DoesNotContain("video-playback-host__tool-backdrop", razor);
        Assert.DoesNotContain("CurrentItem!.AssetId!.Value", razor);
        Assert.Contains("object-fit:contain", css);
        Assert.Contains("playback-video-controls", LocalizedSource.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Player/PlaybackVideoChrome.razor.css")));
        Assert.Contains("width: min(360px", LocalizedSource.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Player/VideoEndCard.razor.css")));
        Assert.Contains("<PlaybackSeekRail", razor);
        Assert.Contains("<PlaybackVolumeControl", LocalizedSource.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Player/PlaybackShell.razor")));
        Assert.Contains("flex-wrap:wrap", LocalizedSource.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Player/PlaybackVideoChrome.razor.css")));
    }
    [Fact]
    public void CaptionSheetUsesObservedNativeAndHlsTrackInventory()
    {
        var root = FindRepoRoot();
        var razor = LocalizedSource.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Player/VideoPlaybackHost.razor"));
        var script = LocalizedSource.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/wwwroot/js/playback-core.js"));

        Assert.Contains(".Select(track => new SubtitleChoice(track.Key, CaptionChoiceLabel(track), track.Selected))", razor, StringComparison.Ordinal);
        Assert.Contains("new SubtitleChoice(null, Off, !_captionTracks.Any(track => track.Selected))", razor, StringComparison.Ordinal);
        Assert.Contains("CaptionTracks { get; set; }", razor, StringComparison.Ordinal);
        Assert.Contains("state.CaptionTracks", razor, StringComparison.Ordinal);
        Assert.Contains("readCaptionTrackChoices(element)", script, StringComparison.Ordinal);
        Assert.Contains("element._tuvimaHls?.subtitleTracks", script, StringComparison.Ordinal);
        Assert.Contains("element.textTracks?.addEventListener('change'", script, StringComparison.Ordinal);
        Assert.Contains("data-playback-asset-id=\"@Playback.CurrentItem?.AssetId\"", razor, StringComparison.Ordinal);
        Assert.Contains("Playback.CurrentItem?.AssetId != observedAssetId", razor, StringComparison.Ordinal);
        var chrome = LocalizedSource.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/wwwroot/js/playback-chrome.js"));
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
