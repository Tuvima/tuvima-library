using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Source-level checks for music-player-and-listen-fixes D6 (WP5): the music and audiobook bar is exactly as tall as the video bar, a 28 px
/// timeline plus a 60 px row with the same 56 px cover slot, and no block padding beyond the safe area.
/// </summary>
public sealed class PlaybackBarHeightStyleTests
{
    private const string ShellCss = "src/MediaEngine.Web/Components/Player/PlaybackShell.razor.css";
    private const string IdentityCss = "src/MediaEngine.Web/Components/Player/PlaybackIdentity.razor.css";
    private const string HostCss = "src/MediaEngine.Web/Components/Player/ListenNowPlayingBar.razor.css";

    [Fact]
    public void AudioAndVideoBars_ShareOneTimelineHeight_WithNoPerSurfaceOverride()
    {
        var css = Read(ShellCss);

        Assert.Contains("height: 28px;", Rule(css, ".playback-shell__timeline"), StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^\.playback-shell--(surface-dock|music|audiobook)[^{]*\.playback-shell__timeline\s*\{", css);
    }

    [Fact]
    public void AudioAndVideoBars_ShareOneRowHeight()
    {
        var css = Read(ShellCss);

        Assert.Contains("min-height: 60px;", Rule(css, ".playback-shell__row"), StringComparison.Ordinal);
        // The audio row is pinned to the same 60 px so a third identity line cannot make the bar taller than the video bar.
        Assert.Contains("height: 60px;", Rule(css, ".playback-shell--surface-dock:not(.playback-shell--compact) .playback-shell__row"), StringComparison.Ordinal);
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
    public void AudioAndVideoBars_ShareOneCoverSlot_OfFiftySixPixels()
    {
        var art = Rule(Read(IdentityCss), ".playback-identity__art");

        Assert.Contains("height: 56px;", art, StringComparison.Ordinal);
        Assert.Contains("width: 56px;", art, StringComparison.Ordinal);
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
