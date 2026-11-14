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

    // details-page-bottom D17 (issue 981): the 1440 px cap is now the width of the window up to about 1500 px of screen; the width rule below it follows
    // the window's height on a wider screen (16:9 of the height, floor 1440 px, cap 1920 px). The first width line stays as the fallback for a browser
    // that cannot do the second, so up to about 1500 px nothing changes.
    [Fact]
    public void DetailsWindow_IsNinetyPercentOfTheScreen_With1440BaseWidth_AndA32PxMargin()
    {
        var tokens = File.ReadAllText(Path.Combine(WebRoot, "wwwroot", "tuvima.tokens.css"));
        Assert.Matches(@"--tl-window-details-max:\s*1440px;", tokens);
        Assert.Matches(@"--tl-window-details-wide-max:\s*1920px;", tokens);
        Assert.Matches(@"--tl-window-details-ratio:\s*1\.7778;", tokens);
        Assert.Matches(@"--tl-window-details-margin:\s*32px;", tokens);
        Assert.Matches(@"--tl-window-details-radius:\s*16px;", tokens);

        var rule = Rule(File.ReadAllText(Path.Combine(WebRoot, "wwwroot", "app.css")), "dialog.tl-details-overlay");
        Assert.Contains("width: min(90vw, var(--tl-window-details-max), calc(100vw - 2 * var(--tl-window-details-margin)));", rule, StringComparison.Ordinal);
        Assert.Contains("height: min(90dvh, calc(100dvh - 2 * var(--tl-window-details-margin)));", rule, StringComparison.Ordinal);
        Assert.Contains("margin: auto;", rule, StringComparison.Ordinal);
        Assert.Contains("border-radius: var(--tl-window-details-radius);", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailsWindow_OnAWideScreen_FollowsItsHeightUpTo1920_NeverBeyondTheScreenMargins()
    {
        var rule = Rule(File.ReadAllText(Path.Combine(WebRoot, "wwwroot", "app.css")), "dialog.tl-details-overlay");

        Assert.Contains(
            "width: min(90vw, calc(100vw - 2 * var(--tl-window-details-margin)), clamp(var(--tl-window-details-max), calc(min(90dvh, calc(100dvh - 2 * var(--tl-window-details-margin))) * var(--tl-window-details-ratio)), var(--tl-window-details-wide-max)));",
            rule,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DetailsWindow_LandscapeBackdropFillsTheHero_CroppingTopAndBottomNeverTheSides()
    {
        // On the details page a landscape backdrop covers the whole stage at 840 px and wider (D17); the wide window is at least 16:9 so the crop is vertical.
        var hero = File.ReadAllText(Path.Combine(WebRoot, "Components", "Details", "HeroBackdrop.razor.css"));

        Assert.Contains(".tl-detail-stage .tl-detail-media-stage--background .tl-detail-media-stage__background {", hero, StringComparison.Ordinal);
        Assert.Contains("width: 100% !important;", hero, StringComparison.Ordinal);
        Assert.Contains("object-fit: cover !important;", hero, StringComparison.Ordinal);
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
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
