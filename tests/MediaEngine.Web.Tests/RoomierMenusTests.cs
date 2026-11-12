namespace MediaEngine.Web.Tests;

/// <summary>
/// Look v3.1 D2: pop-up menus have 48 px rows, a 24 px icon with a 12 px gap, 8 px around the list and between groups, and are 240 to
/// 360 px wide; a longer label shortens to one line. Every menu reads these from the menu tokens.
/// </summary>
public sealed class RoomierMenusTests
{
    private static string ReadWeb(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "MediaEngine.Web", relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Theory]
    [InlineData("--tl-menu-row-height", "48px")]
    [InlineData("--tl-menu-sheet-row-height", "52px")]
    [InlineData("--tl-menu-min-width", "240px")]
    [InlineData("--tl-menu-max-width", "360px")]
    [InlineData("--tl-menu-padding", "8px")]
    [InlineData("--tl-menu-icon-gap", "12px")]
    [InlineData("--tl-menu-icon-size", "24px")]
    [InlineData("--tl-menu-row-font-size", "16px")]
    public void TheMenuTokensCarryTheD2Sizes(string token, string value) =>
        Assert.Contains($"{token}: {value};", ReadWeb("wwwroot/tuvima.tokens.css"), StringComparison.Ordinal);

    [Fact]
    public void MenusGrowWithTheirLongestLabelBetweenTheMinimumAndMaximumWidth()
    {
        var app = ReadWeb("wwwroot/app.css");

        Assert.Contains("min-width: var(--tl-menu-min-width) !important;", app, StringComparison.Ordinal);
        Assert.Contains("max-width: min(var(--tl-menu-max-width), calc(100vw - 16px)) !important;", app, StringComparison.Ordinal);
        Assert.Contains("padding: var(--tl-menu-padding);", app, StringComparison.Ordinal);
        Assert.Contains("gap: var(--tl-menu-icon-gap);", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ALongMenuLabelShortensToOneLineAndKeepsItsFullTextAsTheOverflowTooltip()
    {
        var app = ReadWeb("wwwroot/app.css");
        var start = app.IndexOf(".app-menu-item__label {", StringComparison.Ordinal);
        Assert.True(start >= 0, "The shared menu label rule is missing.");
        var rule = app[start..app.IndexOf('}', start)];

        Assert.Contains("text-overflow: ellipsis;", rule, StringComparison.Ordinal);
        Assert.Contains("white-space: nowrap;", rule, StringComparison.Ordinal);
        Assert.Contains("data-overflow-tooltip=\"@Label\"", ReadWeb("Components/Shared/AppMenuItem.razor"), StringComparison.Ordinal);
    }

    [Fact]
    public void NoMenuKeepsARowHeightOrWidthOfItsOwn()
    {
        foreach (var file in new[] { "Components/Shared/AppTickMenu.razor.css", "Components/Navigation/TopNavAccountMenu.razor.css" })
        {
            var css = ReadWeb(file);
            Assert.DoesNotContain("min-height: 44px", css, StringComparison.Ordinal);
            Assert.DoesNotContain("272px", css, StringComparison.Ordinal);
        }

        // The account menu's rows, icons and gaps come from the menu tokens.
        var account = ReadWeb("Components/Navigation/TopNavAccountMenu.razor.css");
        foreach (var expected in new[] { "width: var(--tl-menu-icon-size);", "margin-right: var(--tl-menu-icon-gap);", "margin: var(--tl-menu-padding) 0 !important;", "min-height: var(--tl-menu-row-height);" })
        {
            Assert.Contains(expected, account, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void APressOnASheetsDimAreaClosesTheSheet()
    {
        var script = ReadWeb("wwwroot/js/app-popover.js");
        Assert.Contains("if (opened.at(-1) === state && isSheetBackdropPress(surface, event)) { close(state, false); return; }", script, StringComparison.Ordinal);
    }
}
