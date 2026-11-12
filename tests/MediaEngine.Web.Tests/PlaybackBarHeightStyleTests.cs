using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Source-level checks for music-player-and-listen-fixes D6 (WP5): the music and audiobook bar is exactly as tall as the video bar, a 28 px
/// timeline plus the video row's 72 px (video player follow-ups D1) with the same 64 px cover slot, and no block padding beyond the safe area. The sizes are main's
/// literals (72 px row, 64 px cover); what these tests pin is that music, audiobook and video share them.
/// </summary>
public sealed class PlaybackBarHeightStyleTests
{
    private const string ShellCss = "src/MediaEngine.Web/Components/Player/PlaybackShell.razor.css";
    private const string IdentityCss = "src/MediaEngine.Web/Components/Player/PlaybackIdentity.razor.css";
    private const string HostCss = "src/MediaEngine.Web/Components/Player/ListenNowPlayingBar.razor.css";
    private const string SeekRailCss = "src/MediaEngine.Web/Components/Shared/PlaybackSeekRail.razor.css";
    private const string AppCss = "src/MediaEngine.Web/wwwroot/app.css";

    [Fact]
    public void AudioAndVideoBars_ShareOneTimelineHeight_WithNoPerSurfaceOverride()
    {
        var css = Read(ShellCss);

        Assert.Contains("height: 28px;", Rule(css, ".playback-shell__timeline"), StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^\.playback-shell--(surface-dock|music|audiobook)[^{]*\.playback-shell__timeline\s*\{", css);
    }

    [Fact]
    public void TheVideoTimelineIsAsTallAsTheAudioOne_BecauseTheSeekRailItHoldsIsTwentyEightPixels_NotByChance()
    {
        var shell = Read(ShellCss);
        var rail = Read(SeekRailCss);

        // The video timeline is "height: auto", so it is exactly as tall as the seek rail inside it. That makes the two bars equal only while the rail is 28 px in every
        // place the video bar draws it, so pin that: the rail's own rule, no override of its height on the video rail, and no padding or margin on the video timeline.
        Assert.Contains("height: auto;", Rule(shell, ".playback-shell--surface-video .playback-shell__timeline"), StringComparison.Ordinal);
        Assert.Matches(@"(?m)^\.playback-seek-rail\s*\{[^}]*\bheight:\s*28px;", rail);
        Assert.DoesNotMatch(@"playback-seek-rail--video[^{]*\{[^}]*(?<![-\w])(min-|max-)?height\s*:", rail);
        Assert.DoesNotMatch(@"playback-seek-rail--video[^{]*\{[^}]*(?<![-\w])(min-|max-)?height\s*:", Read(AppCss));
        var videoTimeline = Rule(shell, ".playback-shell--surface-video .playback-shell__timeline");
        Assert.DoesNotMatch(@"\b(padding|margin|min-height|max-height)\b", videoTimeline);
        Assert.Contains("height: 28px;", Rule(shell, ".playback-shell__timeline"), StringComparison.Ordinal);
    }

    [Fact]
    public void AudioAndVideoBars_ShareOneRowHeight()
    {
        var css = Read(ShellCss);

        Assert.Contains("min-height: 72px;", Rule(css, ".playback-shell__row"), StringComparison.Ordinal);
        // The audio row is pinned to the same 72 px so a third identity line cannot make the bar taller than the video bar.
        Assert.Contains("height: 72px;", Rule(css, ".playback-shell--surface-dock:not(.playback-shell--compact) .playback-shell__row"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAudioBarsIdentityBlock_IsKeptInsideTheSeventyTwoPixelRow_AndDropsTheSecondaryLineBeforeItOverflows()
    {
        var css = Read(IdentityCss);
        const string Dock = ".playback-shell--surface-dock:not(.playback-shell--compact) ";

        Assert.Matches(
            Regex.Escape(Dock + ".playback-identity,\n" + Dock + ".playback-identity__text") + @"\s*\{[^}]*max-height: 72px;",
            css.Replace("\r\n", "\n"));
        // A receiver line or an error line outranks the artist and album line, which is the one dropped.
        Assert.Contains("display: none;", Rule(css, Dock + ".playback-identity__text:has(.playback-identity__remote, .playback-identity__error) .playback-identity__secondary"), StringComparison.Ordinal);
        // Title (23.2) plus both remaining lines at 1.35 of 13 px (17.55 each) is 58.3 px, so it fits; and nothing is clipped, so no focus ring is cut off.
        Assert.Contains("line-height: 1.35;", css[css.IndexOf(Dock + ".playback-identity__text:has(.playback-identity__remote, .playback-identity__error) .playback-identity__remote", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.DoesNotMatch(@"playback-identity__text[^{]*\{[^}]*overflow\s*:\s*(hidden|clip)", css);
    }

    [Fact]
    public void AudioBar_AddsNoBlockPaddingOrBorder_BeyondTheSafeArea()
    {
        var css = Read(ShellCss);
        var shell = Rule(css, ".playback-shell");
        var video = Rule(css, ".playback-shell--surface-video");

        // Horizontal padding stays; above is zero and below is only the phone safe area (the hairline is an inset shadow, so it adds no height).
        Assert.Contains("padding: 0 16px var(--tl-safe-area-bottom, 0px);", shell, StringComparison.Ordinal);
        Assert.Contains("border-top: 0;", shell, StringComparison.Ordinal);
        Assert.Contains("box-shadow: inset 0 1px 0 var(--playback-hairline);", shell, StringComparison.Ordinal);
        Assert.Contains("padding: 0;", video, StringComparison.Ordinal);
        Assert.Contains("border-top: 0;", video, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioAndVideoBars_ShareOneCoverSlot_OfSixtyFourPixels()
    {
        var art = Rule(Read(IdentityCss), ".playback-identity__art");

        Assert.Contains("height: 64px;", art, StringComparison.Ordinal);
        Assert.Contains("width: 64px;", art, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^\.playback-shell--(surface-dock|music|audiobook)[^{]*\.playback-identity__art\b", Read(ShellCss));
    }

    [Fact]
    public void ListenBarHost_AddsNoPaddingAroundTheBar()
    {
        Assert.Contains("padding:0;", Rule(Read(HostCss), ".listen-player-shell").Replace(" ", string.Empty), StringComparison.Ordinal);
    }

    private static string Rule(string css, string selector)
    {
        var match = Regex.Match(css, "(?m)^" + Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}");
        Assert.True(match.Success, $"No rule for {selector}.");
        return match.Groups["body"].Value;
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(
        FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("MediaEngine.slnx not found above the test output folder.");
    }
}
