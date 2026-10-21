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
    public void LibrarySummaryTiles_WrapInCentredBalancedRows_AtEveryWidth()
    {
        var css = Read("src/MediaEngine.Web/Components/Settings/LibrarySummary.razor.css");

        var breakdown = Rule(css, ".library-summary__breakdown");
        Assert.Contains("display: flex;", breakdown, StringComparison.Ordinal);
        Assert.Contains("flex-wrap: wrap;", breakdown, StringComparison.Ordinal);
        Assert.Contains("justify-content: center;", breakdown, StringComparison.Ordinal);
        Assert.Contains("--library-summary-columns: 4;", breakdown, StringComparison.Ordinal);

        // Seven tiles: 4 + 3 on desktop, 3 + 3 + 1 on tablet, pairs on a phone. Never five then two.
        Assert.Matches(new Regex(@"@media \(max-width: 1180px\)\s*\{\s*\.library-summary__breakdown \{ --library-summary-columns: 3; \}"), css);
        Assert.Matches(new Regex(@"@media \(max-width: 600px\)\s*\{\s*\.library-summary__breakdown \{ --library-summary-columns: 2; \}"), css);
        Assert.DoesNotContain("repeat(6", css, StringComparison.Ordinal);
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
