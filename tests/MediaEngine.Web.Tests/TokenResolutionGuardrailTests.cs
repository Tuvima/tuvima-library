using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Look pass WP1: every <c>var(--tl-*)</c> a style reads must be defined somewhere, so a typo or a retired token can no longer
/// silently fall back to a stray color (the cause of the unexplained green). Surfaces and the splash read the violet-navy tokens.
/// </summary>
public sealed class TokenResolutionGuardrailTests
{
    [Fact]
    public void EveryTlTokenAStyleReadsIsDefined()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var files = Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".css") || p.EndsWith(".razor") || p.EndsWith(".cs"))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}vendor{Path.DirectorySeparatorChar}"))
            .Select(p => (Path: p, Text: File.ReadAllText(p)))
            .ToList();

        var defined = files.SelectMany(f => Regex.Matches(f.Text, @"(--tl-[\w-]+)\s*:").Select(m => m.Groups[1].Value)).ToHashSet();
        var offenders = files
            .SelectMany(f => Regex.Matches(f.Text, @"var\(\s*(--tl-[\w-]+)").Select(m => (File: f.Path, Token: m.Groups[1].Value)))
            .Where(u => !u.Token.EndsWith('-') && !defined.Contains(u.Token))
            .Select(u => $"{u.Token} ({Path.GetRelativePath(web, u.File).Replace('\\', '/')})")
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0, "Undefined tokens: " + string.Join(", ", offenders));
    }

    [Fact]
    public void SurfacesAreNeutralNearBlackAndControlsAre44()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var tokens = File.ReadAllText(Path.Combine(web, "wwwroot", "tuvima.tokens.css"));
        Assert.Contains("--tl-bg-root: #101115;", tokens);
        Assert.Contains("--tl-bg-shell: #14151A;", tokens);
        Assert.Contains("--tl-bg-surface: #1A1B21;", tokens);
        Assert.Contains("--tl-bg-surface-hover: #272830;", tokens);
        Assert.Contains("--tl-bg-surface-elevated: #2F3039;", tokens);
        Assert.Contains("--tl-control-height-md: 44px;", tokens);
        Assert.DoesNotContain("0B0920", tokens, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("100B2A", tokens, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--tl-accent-primary: #8852FC;", tokens);
        var intro = File.ReadAllText(Path.Combine(web, "Components", "Setup", "IntroAnimation.razor.css"));
        Assert.DoesNotContain("#8b5cf6", intro, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DangerTextAndFocusRingFollowTheLookV2Contract()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var tokens = File.ReadAllText(Path.Combine(web, "wwwroot", "tuvima.tokens.css"));
        Assert.Contains("--tl-text-danger: #FF8A8A;", tokens);
        var utilities = File.ReadAllText(Path.Combine(web, "wwwroot", "native-utilities.css"));
        Assert.DoesNotContain("a:focus-visible,button:focus{outline:none}", utilities);
        Assert.Contains(":focus-visible{outline:var(--tl-focus-ring-width", utilities);
    }

    [Fact]
    public void PageTitlesAndShelfHeadingsUseTheTitleTokens()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var tokens = File.ReadAllText(Path.Combine(web, "wwwroot", "tuvima.tokens.css"));
        Assert.Contains("--tl-font-size-title: 1.5rem;", tokens);
        Assert.Contains("--tl-font-size-shelf-title: 1.25rem;", tokens);
        foreach (var file in new[] { "Components/MediaHub/ShelfHeader.razor.css", "Components/MediaTiles/ContinueAcrossMediaSection.razor.css" })
        {
            Assert.Contains("var(--tl-font-size-shelf-title)", File.ReadAllText(Path.Combine(web, file)));
        }
        foreach (var file in new[] { "Components/Pages/SearchPage.razor.css", "Components/Settings/Frame/SettingsPageFrame.razor.css", "Components/Shared/Shell/SidebarPageHeader.razor.css", "Components/Pages/ViewContentPage.razor.css" })
        {
            Assert.Contains("var(--tl-font-size-title)", File.ReadAllText(Path.Combine(web, file)));
        }
    }

    [Fact]
    public void FilterChipsShareOneHeightAndGrowToTouchSize()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var tokens = File.ReadAllText(Path.Combine(web, "wwwroot", "tuvima.tokens.css"));
        Assert.Contains("--tl-chip-height: 36px;", tokens);
        Assert.Contains("--tl-chip-height-touch: 44px;", tokens);
        var app = File.ReadAllText(Path.Combine(web, "wwwroot", "app.css"));
        Assert.Contains("min-height: var(--tl-chip-height-touch)", app);
        var toggle = File.ReadAllText(Path.Combine(web, "Components", "Shared", "AppToggleChip.razor.css"));
        Assert.Contains("min-height: var(--tl-chip-height);", toggle);
    }

    [Fact]
    public void MenuRowsUseTheOneMenuSizeTokens()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var tokens = File.ReadAllText(Path.Combine(web, "wwwroot", "tuvima.tokens.css"));
        Assert.Contains("--tl-menu-row-height: 44px;", tokens);
        Assert.Contains("--tl-menu-width: 272px;", tokens);
        var app = File.ReadAllText(Path.Combine(web, "wwwroot", "app.css"));
        Assert.DoesNotContain("min-height: 34px !important;\r\n    border-radius: var(--tl-radius-md);\r\n    color: var(--tl-text-secondary)", app);
        Assert.Contains("min-height: var(--tl-menu-row-height) !important;", app);
    }

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
