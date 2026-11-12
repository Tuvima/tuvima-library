using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The details window floats (Look v3.1 D5, issue 875). The shared Viewer part renders the dialog element, so the
/// window's size must live in the global sheet: a scoped rule in the host's own style file never reaches it, which is
/// how the window came to fill the screen.
/// </summary>
public sealed class DetailsWindowSizeTests
{
    private static readonly string WebRoot = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");

    [Fact]
    public void DetailsWindow_IsNinetyPercentOfTheScreen_CappedAt1440_WithA32PxMargin()
    {
        var tokens = File.ReadAllText(Path.Combine(WebRoot, "wwwroot", "tuvima.tokens.css"));
        Assert.Matches(@"--tl-window-details-max:\s*1440px;", tokens);
        Assert.Matches(@"--tl-window-details-margin:\s*32px;", tokens);
        Assert.Matches(@"--tl-window-details-radius:\s*16px;", tokens);

        var rule = Rule(File.ReadAllText(Path.Combine(WebRoot, "wwwroot", "app.css")), "dialog.tl-details-overlay");
        Assert.Contains("width: min(90vw, var(--tl-window-details-max), calc(100vw - 2 * var(--tl-window-details-margin)));", rule, StringComparison.Ordinal);
        Assert.Contains("height: min(90dvh, calc(100dvh - 2 * var(--tl-window-details-margin)));", rule, StringComparison.Ordinal);
        Assert.Contains("margin: auto;", rule, StringComparison.Ordinal);
        Assert.Contains("border-radius: var(--tl-window-details-radius);", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailsWindow_OnAPhone_IsABottomSheet()
    {
        var rule = Rule(File.ReadAllText(Path.Combine(WebRoot, "wwwroot", "app.css")), "dialog.tl-details-overlay.tl-details-overlay--sheet");
        Assert.Contains("width: 100%;", rule, StringComparison.Ordinal);
        Assert.Contains("height: 92dvh;", rule, StringComparison.Ordinal);
        Assert.Contains("margin: auto 0 0;", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailsWindow_BoxIsNotStyledFromTheHostsScopedFile_WhereItWouldNeverApply()
    {
        var scoped = File.ReadAllText(Path.Combine(WebRoot, "Components", "Details", "DetailsOverlayHost.razor.css"));
        Assert.DoesNotMatch(new Regex(@"^\.tl-details-overlay(--sheet)?(\[open\]|::backdrop)?\s*\{", RegexOptions.Multiline), scoped);
    }

    private static string Rule(string css, string selector)
    {
        var match = Regex.Match(css, @"^" + Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}", RegexOptions.Multiline);
        Assert.True(match.Success, $"No rule for '{selector}' in app.css.");
        return match.Groups["body"].Value;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
