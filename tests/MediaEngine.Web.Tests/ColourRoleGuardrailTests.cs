using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Look pass v3 WP0 (D2, D3, D4): menus are neutral, the section shell and rail use tokens only, and hand-typed colours in component
/// styles may only go down. Surface tokens are the backgrounds; purple is for selected, primary, switch-on, progress, focus and Like.
/// </summary>
public sealed class ColourRoleGuardrailTests
{
    // Hex and rgb()/rgba() colours in component styles and app.css/native-*.css, outside the allowlist below. Set to the count after WP0.
    private const int ComponentStyleColourCeiling = 2189;

    // Colour belongs to the data here: the map and the places views, the activity heatmap and the person avatar placeholder.
    // Chart colours and subtitle swatches live inside app.css rules and are counted with the rest until WP7 moves them to tokens.
    private static readonly string[] Allowlist =
    [
        "Components/Pages/ViewPlacesPage.razor.css",
        "Components/View/ViewMap.razor.css",
        "Components/View/ViewPlaceStoryPanel.razor.css",
        "Components/View/ViewPlacesTimeline.razor.css",
        "Components/Settings/Overview/OverviewHeatmap.razor.css",
        "Components/Details/PersonAvatar.razor.css",
    ];

    private static readonly Regex ColourLiteral = new(@"#(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{3,4})(?![0-9a-zA-Z_-])|\brgba?\(", RegexOptions.Compiled);

    [Fact]
    public void ComponentStyleColoursOnlyGoDown()
    {
        var web = WebRoot();
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(web, "*.css", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(web, path).Replace('\\', '/');
            if (relative.StartsWith("obj/") || relative.StartsWith("bin/") || relative.Contains("/lib/") || relative.Contains("vendor/"))
            {
                continue;
            }

            var counted = relative.EndsWith(".razor.css") || relative == "wwwroot/app.css" || (relative.StartsWith("wwwroot/native-") && relative.EndsWith(".css"));
            if (!counted || Allowlist.Contains(relative))
            {
                continue;
            }

            count += ColourLiteral.Matches(File.ReadAllText(path)).Count;
        }

        Assert.True(count <= ComponentStyleColourCeiling, $"Hand-typed colours in component styles rose to {count} (ceiling {ComponentStyleColourCeiling}). Use a --tl-* colour token.");
    }

    [Fact]
    public void MenusUseTheNeutralRaisedSurfaceWithAHairlineBorder()
    {
        var tokens = Tokens();
        Assert.Equal("var(--tl-bg-surface-elevated)", Token(tokens, "--tl-menu-surface"));
        Assert.Equal("#2F3039", Token(tokens, "--tl-bg-surface-elevated").ToUpperInvariant());
        Assert.Equal("var(--tl-border-default)", Token(tokens, "--tl-menu-border"));
        Assert.Equal("var(--tl-border-subtle)", Token(tokens, "--tl-menu-divider"));
        Assert.Equal("var(--tl-text-secondary)", Token(tokens, "--tl-menu-icon"));
        Assert.Equal("var(--tl-accent-primary-soft)", Token(tokens, "--tl-menu-item-selected"));
    }

    [Fact]
    public void MenuHoverIsAWhiteWashNotPurple()
    {
        var hover = Token(Tokens(), "--tl-menu-item-hover");
        Assert.StartsWith("rgba(255, 255, 255", hover);
        Assert.DoesNotContain("136, 82, 252", hover);
    }

    [Fact]
    public void SecondaryActionsHaveNoPurpleFillAtRestOrOnHover()
    {
        var tokens = Tokens();
        Assert.Equal("transparent", Token(tokens, "--tl-secondary-action-bg"));
        Assert.Equal("var(--tl-menu-item-hover)", Token(tokens, "--tl-secondary-action-bg-hover"));
        Assert.Equal("var(--tl-border-default)", Token(tokens, "--tl-secondary-action-border"));
        Assert.Equal("var(--tl-border-strong)", Token(tokens, "--tl-secondary-action-border-hover"));
    }

    [Fact]
    public void ThePlayerSurfacesAliasTheSharedNeutralTokens()
    {
        var tokens = Tokens();
        Assert.Equal("var(--tl-bg-root)", Token(tokens, "--player-bg"));
        Assert.Equal("var(--tl-bg-surface)", Token(tokens, "--player-surface"));
        Assert.Equal("var(--tl-text-secondary)", Token(tokens, "--player-text-secondary"));
        Assert.Equal("var(--tl-bg-surface-elevated)", Token(tokens, "--playback-popover-surface"));
    }

    [Fact]
    public void TheSectionShellAndRailUseOnlyTokens()
    {
        var css = File.ReadAllText(Path.Combine(WebRoot(), "Components", "MediaHub", "MediaSectionShell.razor.css"));
        Assert.Empty(ColourLiteral.Matches(css));
        Assert.DoesNotContain("gradient", css);

        var shell = Regex.Match(css, @"^\.media-section-shell \{[^}]*\}", RegexOptions.Multiline).Value;
        Assert.Contains("background: var(--tl-bg-root);", shell);
        var rail = Regex.Match(css, @"^\.media-section-shell__rail \{[^}]*\}", RegexOptions.Multiline).Value;
        Assert.Contains("background: var(--tl-bg-shell);", rail);
        Assert.Contains("border-right: 1px solid var(--tl-border-subtle);", rail);
        var item = Regex.Match(css, @"^\.media-section-shell__rail-item \{[^}]*\}", RegexOptions.Multiline).Value;
        Assert.Contains("min-height: 44px;", item);
    }

    [Fact]
    public void NoNavyAliasOrMutedSurfaceVariableIsDefined()
    {
        var web = WebRoot();
        var app = File.ReadAllText(Path.Combine(web, "wwwroot", "app.css"));
        Assert.DoesNotContain("--app-surface-muted", app);
        foreach (var navy in new[] { "#070b14", "#05070c", "rgba(7, 12, 23" })
        {
            Assert.DoesNotContain(navy, app, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(navy, File.ReadAllText(Path.Combine(web, "wwwroot", "tuvima.tokens.css")), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Tokens() => File.ReadAllText(Path.Combine(WebRoot(), "wwwroot", "tuvima.tokens.css"));

    private static string Token(string css, string name)
    {
        var match = Regex.Match(css, @"^\s*" + Regex.Escape(name) + @":\s*([^;]+);", RegexOptions.Multiline);
        Assert.True(match.Success, $"{name} is not defined.");
        return match.Groups[1].Value.Trim();
    }

    // The Dashboard project folder, found from the repository root.
    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir is null ? throw new InvalidOperationException("Repo root not found.") : Path.Combine(dir.FullName, "src", "MediaEngine.Web");
    }
}
