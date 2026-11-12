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
        Assert.Contains("--tl-font-size-title: 24px;", tokens);
        Assert.Contains("--tl-font-size-shelf-title: 20px;", tokens);
        foreach (var file in new[] { "Components/MediaHub/ShelfHeader.razor.css", "Components/MediaTiles/ContinueAcrossMediaSection.razor.css" })
        {
            Assert.Contains("var(--tl-type-heading-size)", File.ReadAllText(Path.Combine(web, file)));
        }
        // Every page title is the page-title type job (Look v3.1 D8: 24 px, weight 700).
        foreach (var file in new[] { "Components/Pages/SearchPage.razor.css", "Components/Shared/Shell/SidebarPageHeader.razor.css", "Components/Pages/ViewContentPage.razor.css", "Components/Settings/Frame/SettingsPageFrame.razor.css" })
        {
            Assert.Contains("var(--tl-type-page-title-size)", File.ReadAllText(Path.Combine(web, file)));
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
    public void NoStyleUsesWeight800Or900()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var offenders = Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
            .Where(p => (p.EndsWith(".css") || p.EndsWith(".razor"))
                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}vendor{Path.DirectorySeparatorChar}"))
            .Where(p => Regex.IsMatch(File.ReadAllText(p), @"font-weight:\s*(800|900)"))
            .Select(p => Path.GetRelativePath(web, p))
            .ToList();
        Assert.True(offenders.Count == 0, "Weights are 400/500/600/700 (Look pass v2 A3): " + string.Join(", ", offenders));
    }

    [Fact]
    public void MenuRowsUseTheOneMenuSizeTokens()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var tokens = File.ReadAllText(Path.Combine(web, "wwwroot", "tuvima.tokens.css"));
        Assert.Contains("--tl-menu-row-height: 48px;", tokens);
        Assert.Contains("--tl-menu-width: 272px;", tokens);
        var app = File.ReadAllText(Path.Combine(web, "wwwroot", "app.css"));
        Assert.DoesNotContain("min-height: 34px !important;\r\n    border-radius: var(--tl-radius-md);\r\n    color: var(--tl-text-secondary)", app);
        Assert.Contains("min-height: var(--tl-menu-item-height) !important;", app);
    }

    [Fact]
    public void MenuRows_Are48Px16PxAtWeight500_InPixelsBecauseThePageRootIs14Px()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var tokens = File.ReadAllText(Path.Combine(web, "wwwroot", "tuvima.tokens.css"));
        Assert.Contains("--tl-menu-row-height: 48px;", tokens);
        Assert.Contains("--tl-menu-row-font-size: 16px;", tokens);
        Assert.Contains("--tl-menu-row-font-weight: 500;", tokens);
        Assert.Contains("--tl-menu-sheet-row-height: 52px;", tokens);
        Assert.Contains("--tl-menu-item-height: var(--tl-menu-row-height);", tokens);

        var app = File.ReadAllText(Path.Combine(web, "wwwroot", "app.css"));
        var sharedRow = Regex.Match(app, @"\.app-menu-popover \.tl-list-item,\s*\.app-menu-item,[^{]*\{(?<body>[^}]*)\}");
        Assert.True(sharedRow.Success, "The shared menu row rule is missing from app.css.");
        var body = sharedRow.Groups["body"].Value;
        Assert.Contains("min-height: var(--tl-menu-item-height) !important;", body);
        Assert.Contains("font-size: var(--tl-menu-row-font-size) !important;", body);
        Assert.Contains("font-weight: var(--tl-menu-row-font-weight) !important;", body);

        // Phone sheets take their 52 px rows from the one token, never a second literal.
        Assert.DoesNotContain("--tl-menu-item-height: 52px;", app);
        Assert.Equal(2, Regex.Matches(app, @"--tl-menu-item-height: var\(--tl-menu-sheet-row-height\);").Count);

        // Other menu lists read the same tokens instead of keeping their own size.
        var fields = File.ReadAllText(Path.Combine(web, "wwwroot", "native-fields.css"));
        Assert.Contains("font-size: var(--tl-menu-row-font-size, 16px); font-weight: var(--tl-menu-row-font-weight, 500);", fields);
        var account = File.ReadAllText(Path.Combine(web, "Components", "Navigation", "TopNavAccountMenu.razor.css"));
        Assert.Equal(2, Regex.Matches(account, @"font-weight: var\(--tl-menu-row-font-weight\)").Count);
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
