namespace MediaEngine.Web.Tests;

public sealed class VideoPlaybackSurfaceTests
{
    [Fact]
    public void ExpandedVideoPlacesIdentityAboveTimelineAndCentersTransportBetweenToolRegions()
    {
        var root = FindRepoRoot();
        var razor = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor"));
        var css = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor.css"));

        var identityIndex = razor.IndexOf("video-playback-host__stage-identity", StringComparison.Ordinal);
        var timelineIndex = razor.IndexOf("video-playback-host__timeline", StringComparison.Ordinal);
        var toolbarIndex = razor.IndexOf("video-playback-host__toolbar", StringComparison.Ordinal);
        Assert.True(identityIndex >= 0 && identityIndex < timelineIndex);
        Assert.True(timelineIndex < toolbarIndex);
        Assert.Contains("top: 50%;", css, StringComparison.Ordinal);
        Assert.Contains("--playback-primary-size: 150px;", css, StringComparison.Ordinal);
        Assert.Contains("--playback-relative-skip-size: 112px;", css, StringComparison.Ordinal);
        Assert.Contains("--playback-primary-size: 80px;", css, StringComparison.Ordinal);
        Assert.Contains("--playback-relative-skip-size: 56px;", css, StringComparison.Ordinal);
        Assert.Contains("gap: 16px;", css, StringComparison.Ordinal);
        Assert.Contains("BuildPrimaryToolStrip(", razor, StringComparison.Ordinal);
        Assert.Contains("BuildSecondaryToolStrip(", razor, StringComparison.Ordinal);
        Assert.Contains("Class=\"video-playback-host__secondary-strip\"", razor, StringComparison.Ordinal);
        Assert.Contains("BuildUtilityControls(", razor, StringComparison.Ordinal);
        Assert.Contains("<PlaybackControlStrip Controls=\"@VideoHeaderUtilityControls\"", razor, StringComparison.Ordinal);
        Assert.Contains("display: grid;", css, StringComparison.Ordinal);
        Assert.Contains("Surface=\"video\"", razor, StringComparison.Ordinal);
        Assert.Contains("CanFullscreen: true", razor, StringComparison.Ordinal);
        Assert.Contains("ValueChanged=\"SetVolumeOnHostAsync\"", razor, StringComparison.Ordinal);
        Assert.Contains("Class=\"video-playback-host__volume\"", razor, StringComparison.Ordinal);
        Assert.Contains("VideoPrimaryChoiceControls", razor, StringComparison.Ordinal);
        Assert.Contains("VideoHeaderUtilityControls", razor, StringComparison.Ordinal);
        Assert.Contains("Surface=\"dock\"", razor, StringComparison.Ordinal);
        Assert.Contains(".video-playback-host__tools ::deep .video-playback-host__tool-strip", css, StringComparison.Ordinal);
        Assert.Contains(".video-playback-host__tools ::deep .video-playback-host__secondary-strip", css, StringComparison.Ordinal);
        Assert.Contains(".video-playback-host__header ::deep .video-playback-host__header-tools", css, StringComparison.Ordinal);
        Assert.Contains("width: max-content;", css, StringComparison.Ordinal);
        Assert.Contains("max-width: calc(100% - 58px);", css, StringComparison.Ordinal);
        Assert.Contains(".video-playback-host__header ::deep .video-playback-host__icon { flex: 0 0 44px; }", css, StringComparison.Ordinal);
        Assert.Contains("Class=\"video-playback-host__icon\"", razor, StringComparison.Ordinal);
        Assert.Contains("::deep .video-playback-host__icon,", css, StringComparison.Ordinal);
        Assert.Contains("width: 44px;", css, StringComparison.Ordinal);
        Assert.Contains("height: 44px;", css, StringComparison.Ordinal);
        Assert.Contains("border-radius: 8px;", css, StringComparison.Ordinal);
        Assert.Contains("font-size: 22px !important;", css, StringComparison.Ordinal);
        Assert.DoesNotContain("max-width: 500px;", css, StringComparison.Ordinal);
        Assert.DoesNotContain("min-width: 320px;", css, StringComparison.Ordinal);
        Assert.Contains("flex-wrap: wrap;", css, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(auto-fit, minmax(64px, 1fr));", css, StringComparison.Ordinal);
        Assert.Contains("min-width: 0;", css, StringComparison.Ordinal);
        Assert.Contains("flex: 0 0 44px;", css, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: normal;", css, StringComparison.Ordinal);
        Assert.Contains("font-size: .875rem;", css, StringComparison.Ordinal);
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
        Assert.Contains("updateAutomaticCaptionPlacement(element, observer)", script, StringComparison.Ordinal);
        Assert.Contains("cue.lineAlign = 'start'", script, StringComparison.Ordinal);
        Assert.Contains("restoreAutomaticCaptionPlacement(observer)", script, StringComparison.Ordinal);
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
