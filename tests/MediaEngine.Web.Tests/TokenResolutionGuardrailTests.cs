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
    public void SurfacesAndTheSplashUseTheIntroVioletNavy()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var tokens = File.ReadAllText(Path.Combine(web, "wwwroot", "tuvima.tokens.css"));
        Assert.Contains("--tl-bg-root: #0B0920;", tokens);
        Assert.Contains("--tl-accent-primary: #8852FC;", tokens);
        var intro = File.ReadAllText(Path.Combine(web, "Components", "Setup", "IntroAnimation.razor.css"));
        Assert.DoesNotContain("#8b5cf6", intro, StringComparison.OrdinalIgnoreCase);
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
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
