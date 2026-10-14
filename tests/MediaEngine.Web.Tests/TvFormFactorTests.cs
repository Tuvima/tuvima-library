using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Guards the tv form factor block in app.css (one 10-foot presentation on html[data-form-factor="tv"]), the shared
/// focus ring, the spatial navigation helper and the removal of the old automotive presentation.
/// </summary>
public sealed class TvFormFactorTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void TvBlockDefinesTheTenFootValues()
    {
        var css = ReadWeb("wwwroot/app.css");

        Assert.Single(Regex.Matches(css, @"/\* tv form factor:"));
        AssertRule(css, "html[data-form-factor=\"tv\"]", "font-size: 18px;");
        AssertRule(css, "html[data-form-factor=\"tv\"]", "--media-shelf-gutter: 48px;");
        AssertRule(css, "html[data-form-factor=\"tv\"] .layout-shell__intent-dock-link", "min-height: 56px;");
        AssertRule(css, "html[data-form-factor=\"tv\"] .layout-shell__intent-dock-link", "min-width: 56px;");
        AssertRule(css, "html[data-form-factor=\"tv\"] .layout-shell__intent-dock-link span", "font-size: 14px;");
        AssertRule(css, "html[data-form-factor=\"tv\"] .media-tile-caption__title", "font-size: 0.9rem;");
        AssertRule(css, "html[data-form-factor=\"tv\"] .tl-detail-hero__synopsis", "max-width: 60ch;");
    }

    [Fact]
    public void TvBlockAddsNoImportantOrDeepOverrides()
    {
        var css = ReadWeb("wwwroot/app.css");
        var block = css[css.IndexOf("/* tv form factor:", StringComparison.Ordinal)..];

        Assert.DoesNotContain("!important", block, StringComparison.Ordinal);
        Assert.DoesNotContain("::deep", block, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedFocusRingIsDefinedForInteractiveControls()
    {
        var css = ReadWeb("wwwroot/app.css");
        var tokens = ReadWeb("wwwroot/tuvima.tokens.css");

        Assert.Contains("--tl-focus-ring:", tokens, StringComparison.Ordinal);
        // The width and offset are tokens beside the ring colour (always defined, never inside a media query).
        Assert.Matches(new Regex(@"^    --tl-focus-ring: #[0-9A-Fa-f]{6};\s*--tl-focus-ring-width: 3px;\s*--tl-focus-ring-offset: 2px;", RegexOptions.Multiline), tokens);
        Assert.Contains("outline: var(--tl-focus-ring-width) solid var(--tl-focus-ring);", css, StringComparison.Ordinal);
        foreach (var target in new[] { "a[href]", "button", "tr[tabindex]", ".tl-chip", ".media-tile-link", ".layout-shell__intent-dock-link", ":focus-visible" })
        {
            Assert.Contains(target, css[css.IndexOf("Shared keyboard/remote focus ring", StringComparison.Ordinal)..], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SpatialNavigationHelperIsRegisteredByTheDashboardScript()
    {
        var script = ReadWeb("wwwroot/app.js");

        Assert.Contains("window.registerSpatialNav = ", script, StringComparison.Ordinal);
        Assert.Contains("window.registerSpatialNav(document);", script, StringComparison.Ordinal);
    }

    [Fact]
    public void NoAutomotivePresentationRemainsInDashboardStyles()
    {
        var webRoot = Path.Combine(RepoRoot, "src", "MediaEngine.Web");
        var offenders = Directory.EnumerateFiles(webRoot, "*.css", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}vendor{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("automotive", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(RepoRoot, path))
            .ToList();
        Assert.Empty(offenders);

        var deviceContext = ReadWeb("Services/Theming/DeviceContextService.cs");
        var layout = ReadWeb("Shared/MainLayout.razor");
        Assert.DoesNotContain("IsAutomotive", deviceContext + layout, StringComparison.Ordinal);
        Assert.DoesNotContain("automotive", layout, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertRule(string css, string selector, string declaration)
    {
        var match = Regex.Matches(css, Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}")
            .FirstOrDefault(m => m.Groups["body"].Value.Contains(declaration, StringComparison.Ordinal));
        Assert.True(match is not null, $"Expected '{selector}' to declare '{declaration}'.");
    }

    private static string ReadWeb(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
