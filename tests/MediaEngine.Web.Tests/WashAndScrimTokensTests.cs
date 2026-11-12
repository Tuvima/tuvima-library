using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Look v3.1 D7: the named wash and scrim colors that replace hand-typed black and white alphas across the app. Each step is a literal so a
/// style that names it looks the same everywhere; WP11's sweep reuses exactly these names.
/// </summary>
public sealed class WashAndScrimTokensTests
{
    private static readonly (string Token, string Value)[] Expected =
    [
        ("--tl-wash-light-1", "rgba(255, 255, 255, 0.03)"),
        ("--tl-wash-light-2", "rgba(255, 255, 255, 0.05)"),
        ("--tl-wash-light-3", "rgba(255, 255, 255, 0.08)"),
        ("--tl-wash-light-4", "rgba(255, 255, 255, 0.12)"),
        ("--tl-wash-light-5", "rgba(255, 255, 255, 0.18)"),
        ("--tl-scrim-dark-1", "rgba(0, 0, 0, 0.2)"),
        ("--tl-scrim-dark-2", "rgba(0, 0, 0, 0.3)"),
        ("--tl-scrim-dark-3", "rgba(0, 0, 0, 0.4)"),
        ("--tl-scrim-dark-4", "rgba(0, 0, 0, 0.5)"),
        ("--tl-scrim-dark-5", "rgba(0, 0, 0, 0.6)"),
        ("--tl-scrim-dark-6", "rgba(0, 0, 0, 0.7)"),
    ];

    private static string Tokens() => File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "wwwroot", "tuvima.tokens.css"));

    [Fact]
    public void EveryWashAndScrimTokenIsDefinedWithItsLiteralValue()
    {
        var css = Tokens();
        foreach (var (token, value) in Expected)
        {
            var match = Regex.Match(css, Regex.Escape(token) + @"\s*:\s*(?<value>[^;]+);");
            Assert.True(match.Success, $"{token} is missing from tuvima.tokens.css.");
            Assert.Equal(value, Regex.Replace(match.Groups["value"].Value.Trim(), @"\s+", " "));
        }
    }

    [Fact]
    public void TheWashAndScrimSetStaysShortAndOnlyUsesBlackOrWhite()
    {
        var names = Regex.Matches(Tokens(), @"(?<token>--tl-(?:wash|scrim)-(?:light|dark)-\d+)\s*:").Select(m => m.Groups["token"].Value).ToList();
        Assert.Equal(Expected.Select(e => e.Token).OrderBy(n => n), names.OrderBy(n => n));
        Assert.All(Expected.Where(e => e.Token.Contains("wash-light")), e => Assert.StartsWith("rgba(255, 255, 255,", e.Value));
        Assert.All(Expected.Where(e => e.Token.Contains("scrim-dark")), e => Assert.StartsWith("rgba(0, 0, 0,", e.Value));
    }

    [Fact]
    public void EveryWashAndScrimTokenIsInTheDesignSystemColorTable()
    {
        var doc = File.ReadAllText(Path.Combine(FindRepoRoot(), "engineering", "design", "design-system.md"));
        foreach (var (token, value) in Expected)
        {
            Assert.Contains($"`{token}` | `{value}`", doc);
        }
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
