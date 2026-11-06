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

    [Fact]
    public void SettingsFrameHeader_PutsAPageActionAtTheTopRightLevelWithTheTitle()
    {
        var css = Read("src/MediaEngine.Web/Components/Settings/Frame/SettingsPageFrame.razor.css");

        var actions = Rule(css, ".settings-frame__header ::deep .sidebar-page-header .app-page-header__actions");
        Assert.Contains("align-self: flex-start;", actions, StringComparison.Ordinal);
        var header = Rule(css, ".settings-frame__header ::deep .sidebar-page-header");
        Assert.Contains("flex-direction: row;", header, StringComparison.Ordinal);
    }

    [Fact]
    public void LibrariesPage_PutsAddLibraryInTheFrameHeaderNotAsARowOfItsOwnBelowTheStatusLine()
    {
        var overview = Read("src/MediaEngine.Web/Components/Settings/Libraries/LibrariesOverview.razor");

        // Inside the frame the button is handed to the header; only a table drawn without the frame keeps the row.
        Assert.Contains("HeaderActions?.Set(", overview, StringComparison.Ordinal);
        Assert.Contains("@if (HeaderActions is null && CanAddLibrary)", overview, StringComparison.Ordinal);
        var status = Read("src/MediaEngine.Web/Components/Settings/LibrariesTab.razor");
        Assert.Contains("<LibraryStatusLine", status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(390)]
    [InlineData(360)]
    [InlineData(320)]
    public void ViewPageToolbarColumn_FitsTheScreenWithEqual16pxGuttersAtPhoneWidths(int width)
    {
        Assert.InRange(width, 320, 640);
        var css = Read("src/MediaEngine.Web/Components/Pages/ViewContentPage.razor.css");

        // The column is one track that may shrink below its widest child, so nothing can push the right edge past the screen.
        var page = Rule(css, ".view-content-page");
        Assert.Contains("grid-template-columns: minmax(0, 1fr);", page, StringComparison.Ordinal);
        Assert.Contains("min-width: 0;", page, StringComparison.Ordinal);

        var phone = css[css.IndexOf("@media (max-width: 640px)", StringComparison.Ordinal)..];
        var gutters = Regex.Match(phone, @"\.view-content-page:not\(\.view-content-page--fill-height\)\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        Assert.Matches(@"padding:\s*\d+px 16px \d+px;", gutters);

        var actions = Regex.Match(phone, @"\.view-content-page__actions\s*\{(?<body>[^}]*)\}").Groups["body"].Value;
        Assert.Contains("align-items: stretch;", actions, StringComparison.Ordinal);
        Assert.Contains("flex-direction: column;", actions, StringComparison.Ordinal);
        Assert.Contains("width: 100%;", actions, StringComparison.Ordinal);

        var header = css[css.IndexOf("@media (max-width: 900px)", StringComparison.Ordinal)..];
        Assert.Contains("grid-template-columns: minmax(0, 1fr);", header, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveryFilterBar_ChipRowScrollsInsideItsOwnBox_AndTheBarNeverWidensItsColumn()
    {
        var css = Read("src/MediaEngine.Web/Components/Shared/AppDiscoveryFilterBar.razor.css");

        var narrow = css[css.IndexOf("@media(max-width:1050px)", StringComparison.Ordinal)..];
        Assert.Contains(".app-discovery-filter{grid-template-columns:minmax(0,1fr)}", narrow, StringComparison.Ordinal);
        Assert.Contains(".app-discovery-filter__tabs{flex:1;min-width:0}", narrow, StringComparison.Ordinal);
        var phone = css[css.IndexOf("@media(max-width:640px)", StringComparison.Ordinal)..];
        Assert.Contains(".app-discovery-filter__tabs{width:100%;overflow-x:auto}", phone, StringComparison.Ordinal);
        Assert.Contains(".app-discovery-filter__search ::deep input{width:100%;min-width:0;", css, StringComparison.Ordinal);
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
