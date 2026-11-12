namespace MediaEngine.Web.Tests;

/// <summary>
/// Editor v6 D18: the icons in the editor header and its tab row are outlines in the normal icon color. They draw the Outlined glyph set, the
/// home buttons are outlined neutral buttons (never a filled accent tile), the dropdown badges are neutral, and the tab icons take the tab's
/// own text color instead of one color per tab.
/// </summary>
public sealed class EditorHeaderIconStyleGuardrailTests
{
    private static readonly string[] HeaderFiles =
    [
        "src/MediaEngine.Web/Components/Editor/EditorItemHeader.razor",
        "src/MediaEngine.Web/Components/Editor/EditorHeaderNav.razor",
        "src/MediaEngine.Web/Components/Editor/EditorWindowChrome.razor",
        "src/MediaEngine.Web/Components/Editor/EditorPanelHost.razor",
        "src/MediaEngine.Web/Components/Editor/ViewEditHeaderNav.razor",
    ];

    [Fact]
    public void TheHeaderAndTabRowDrawOnlyTheOutlinedGlyphs()
    {
        foreach (var file in HeaderFiles)
        {
            var razor = Read(file);
            Assert.DoesNotContain("AppMaterialIcons.Filled.", razor, StringComparison.Ordinal);
            Assert.Contains("AppMaterialIcons.Outlined.", razor, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheHomeButtonsAreOutlinedNeutralButtons_NotFilledAccentTiles()
    {
        foreach (var file in new[] { HeaderFiles[0], HeaderFiles[1], HeaderFiles[4] })
        {
            var razor = Read(file);
            Assert.DoesNotContain("AppButtonStyle.Filled", razor, StringComparison.Ordinal);
            foreach (System.Text.RegularExpressions.Match button in System.Text.RegularExpressions.Regex.Matches(
                         razor, "<AppIconButton.*?/>", System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                Assert.DoesNotContain("AppUiTone.Primary", button.Value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TheDropdownBadgesAreAllNeutral_WithNoAccentVariant()
    {
        Assert.DoesNotContain("tl-hnav__badge--accent", Read(HeaderFiles[1]), StringComparison.Ordinal);
        Assert.DoesNotContain("tl-hnav__badge--accent", Read("src/MediaEngine.Web/Components/Editor/EditorHeaderNav.razor.css"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTabIconsTakeTheTabsOwnColor_NotOneColorPerTab()
    {
        var css = Read("src/MediaEngine.Web/wwwroot/app.css");

        // How the tab icons are colored overall belongs to the Look work; the rule here is only that no tab has a color of its own.
        Assert.DoesNotContain("--tl-editor-tab-icon", css, StringComparison.Ordinal);
        foreach (var tab in new[] { "details", "artwork", "match", "history" })
        {
            var selector = ".tl-editor-tab--" + tab;
            var index = css.IndexOf(selector, StringComparison.Ordinal);
            while (index >= 0)
            {
                var block = css[index..css.IndexOf('}', index)];
                Assert.DoesNotContain("color", block, StringComparison.OrdinalIgnoreCase);
                index = css.IndexOf(selector, index + selector.Length, StringComparison.Ordinal);
            }
        }
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(FindRepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
