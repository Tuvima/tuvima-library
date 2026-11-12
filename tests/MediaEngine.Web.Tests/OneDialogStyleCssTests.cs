using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// One dialog style (Look v3.1 D4): a calm roomy box with no lines inside. The numbers live in tokens, the shared card draws no header or footer
/// border, and a larger window's only line is a hairline driven by the body's scroll position.
/// </summary>
public sealed class OneDialogStyleCssTests
{
    [Theory]
    [InlineData("--tl-dialog-padding", "28px")]
    [InlineData("--tl-dialog-padding-phone", "20px")]
    [InlineData("--tl-dialog-title-gap", "8px")]
    [InlineData("--tl-dialog-footer-gap", "28px")]
    [InlineData("--tl-dialog-button-gap", "12px")]
    public void TheDialogSpacingNumbersLiveInTokens(string token, string value)
    {
        var tokens = ReadWeb("wwwroot", "tuvima.tokens.css");

        Assert.Matches(new Regex($@"^\s*{Regex.Escape(token)}:\s*{Regex.Escape(value)};", RegexOptions.Multiline), tokens);
    }

    [Fact]
    public void TheSharedCardDrawsNoBorderOnTheHeaderOrTheFooter()
    {
        var css = ReadShared("AppDialogShell.razor.css");

        foreach (var rule in Rules(css).Where(rule => rule.Selector.Contains("__header") || rule.Selector.Contains("__footer")))
        {
            Assert.DoesNotMatch(@"border(-top|-bottom|-block(-start|-end)?)?\s*:", rule.Body);
        }
    }

    [Fact]
    public void TheShortDialogTakesItsPaddingAndGapsFromTheTokens()
    {
        var css = ReadShared("AppDialogShell.razor.css");

        Assert.Contains("padding: var(--tl-dialog-padding);", css);
        Assert.Contains("padding: var(--tl-dialog-padding-phone);", css);
        Assert.Contains("margin-top: var(--tl-dialog-title-gap);", css);
        Assert.Contains("padding: var(--tl-dialog-footer-gap) 0 0;", css);
        Assert.Contains("gap: var(--tl-dialog-button-gap);", css);
        Assert.Contains("border-color: var(--tl-border-default);", css);
    }

    [Fact]
    public void ALargerWindowsHairlineIsOnlyDrawnWhileTheBodyIsScrolledAwayFromThatEdge()
    {
        var css = ReadShared("AppDialogShell.razor.css");

        Assert.Contains("@supports (animation-timeline: scroll())", css);
        Assert.Contains("scroll-timeline: --tl-dialog-body block;", css);
        Assert.Contains("animation-timeline: --tl-dialog-body;", css);
        Assert.Contains(".app-dialog-shell__header::after", css);
        Assert.Contains(".app-dialog-shell__footer::before", css);
        // At rest the line is invisible: it only fades in with the scroll position.
        Assert.Matches(@"__footer::before\s*\{[^}]*opacity:\s*0;", css);
    }

    private static IEnumerable<(string Selector, string Body)> Rules(string css) =>
        Regex.Matches(css, @"(?<selector>[^{}@]+)\{(?<body>[^{}]*)\}")
            .Select(match => (match.Groups["selector"].Value.Trim(), match.Groups["body"].Value));

    private static string ReadShared(string file) => ReadWeb("Components", "Shared", file);

    private static string ReadWeb(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine([dir!, "src", "MediaEngine.Web", .. parts])).Replace("\r\n", "\n");
    }
}
