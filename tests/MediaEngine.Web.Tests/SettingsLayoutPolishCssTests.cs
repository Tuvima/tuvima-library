using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>Source-level checks for the Settings layout polish: the centred page column, balanced media-type tiles and the aligned menu toggle.</summary>
public sealed class SettingsLayoutPolishCssTests
{
    [Fact]
    public void SettingsFrame_CentresItsContentColumn_WithinTheSpaceBesideTheMenu()
    {
        var css = Read("src/MediaEngine.Web/Components/Settings/Frame/SettingsPageFrame.razor.css");

        var frame = Rule(css, ".settings-frame");
        Assert.Contains("max-width: var(--settings-frame-max-width);", frame, StringComparison.Ordinal);
        Assert.Contains("margin: 0 auto;", frame, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsMenuToggleSlot_CentresTheChevronOnTheRowsMiddle()
    {
        var css = Read("src/MediaEngine.Web/Components/MediaHub/MediaSectionShell.razor.css");

        var slot = Rule(css, ".media-section-shell__tree-chevron-slot");
        Assert.Contains("display: flex;", slot, StringComparison.Ordinal);
        Assert.Contains("align-self: stretch;", slot, StringComparison.Ordinal);
        Assert.Contains("align-items: center;", slot, StringComparison.Ordinal);
        Assert.Contains("justify-content: center;", slot, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsRail_IsItsOwnScroller_SoEveryEntryStaysReachableInAShortWindow()
    {
        var css = Read("src/MediaEngine.Web/Components/MediaHub/MediaSectionShell.razor.css");

        var rail = Rule(css, ".media-section-shell__rail");
        Assert.Contains("overflow-y: auto;", rail, StringComparison.Ordinal);
        Assert.Contains("overflow-x: hidden;", rail, StringComparison.Ordinal);
        Assert.Contains("min-height: 0;", rail, StringComparison.Ordinal);
        Assert.DoesNotContain("overflow: visible;", rail, StringComparison.Ordinal);
        Assert.Contains("border-right:", rail, StringComparison.Ordinal);
        Assert.Contains("background:", rail, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsShell_CannotBeScrolledByFocus_SoTheRailAndPageNeverShiftUnderTheTopBar()
    {
        var css = Read("src/MediaEngine.Web/Components/MediaHub/MediaSectionShell.razor.css");

        var shell = Rule(css, ".media-section-shell");
        Assert.Contains("overflow: clip;", shell, StringComparison.Ordinal);
        // The content column is the other scroller, so nothing inside the shell overflows it.
        var content = Rule(css, ".media-section-shell__content");
        Assert.Contains("overflow-y: auto;", content, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsRail_InAShortPlaybackFrame_LeavesScrollingToTheWholeShell()
    {
        var css = Read("src/MediaEngine.Web/Components/MediaHub/MediaSectionShell.razor.css");

        var block = css[css.IndexOf("@container playback-frame (max-height:700px)", StringComparison.Ordinal)..];
        var rail = Regex.Match(block, @"\.media-section-shell__rail\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        Assert.Contains("overflow: visible;", rail, StringComparison.Ordinal);
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
