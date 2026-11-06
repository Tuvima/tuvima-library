using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Source-level checks that the Needs Review list keeps the approved mock's shape (universal editor v3, screen 2): a 44 px picture
/// column first, the two bulk links at the right of the filter row, and a green "Use this" that follows the look-pass ownership rules
/// (tokens only, component roots styled from the global sheet, no inline styles in the page).
/// </summary>
public sealed class NeedsReviewTableCssTests
{
    private const string PageCss = "src/MediaEngine.Web/Components/Settings/NeedsReviewPageContent.razor.css";
    private const string PageRazor = "src/MediaEngine.Web/Components/Settings/NeedsReviewPageContent.razor";
    private const string AppCss = "src/MediaEngine.Web/wwwroot/app.css";
    private const string TvMarker = "/* tv form factor:";

    [Fact]
    public void ThePictureIsA44PixelSquare_WithADarkPlaceholder_AndFirstInTheTable()
    {
        var css = Read(PageCss);

        var thumb = Rule(css, ".needs-review-page .needs-review-table__thumb");
        Assert.Contains("inline-size: 44px;", thumb, StringComparison.Ordinal);
        Assert.Contains("block-size: 44px;", thumb, StringComparison.Ordinal);
        Assert.Contains("var(--tl-bg-surface-raised)", thumb, StringComparison.Ordinal);
        Assert.Contains(".needs-review-page .needs-review-table th:nth-child(1) { width: 4.25rem; }", css, StringComparison.Ordinal);
        Assert.DoesNotContain("__tick", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePhoneCard_PutsThePictureBesideTheItemAndKeepsTheActionsAtTheEnd()
    {
        var css = Read(PageCss);

        Assert.Contains("grid-template-columns: 44px minmax(0, 1fr) auto;", css, StringComparison.Ordinal);
        Assert.Contains("\"thumb item actions\"", css, StringComparison.Ordinal);
        Assert.Contains("td.needs-review-table__thumb-cell { grid-area: thumb; }", css, StringComparison.Ordinal);
        Assert.Contains("td.needs-review-table__actions { grid-area: actions; justify-self: end; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBulkLinksSitAtTheRightEndOfTheFilterRow_NotOnARowOfTheirOwn()
    {
        var bulk = Rule(Read(PageCss), ".needs-review-page__bulk");

        Assert.Contains("margin-inline-start: auto;", bulk, StringComparison.Ordinal);
        var razor = Read(PageRazor);
        var filterRow = Regex.Match(razor, @"<AppFilterRow\b.*?</AppFilterRow>", RegexOptions.Singleline).Value;
        Assert.Contains("needs-review-page__bulk", filterRow, StringComparison.Ordinal);
        Assert.DoesNotContain("<section class=\"needs-review-page__bulk\"", razor, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageStyle_UsesNoInlineStylesAndNoHandTypedColours()
    {
        var razor = Read(PageRazor);
        var css = Read(PageCss);

        Assert.DoesNotContain(" style=\"", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("InlineStyle=", razor, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"#[0-9a-fA-F]{3,8}\b|rgba?\("), css);
    }

    [Fact]
    public void TheComponentRootsOnTheRow_AreStyledFromTheGlobalSheet_BeforeTheTvMarker_AndWithTokensOnly()
    {
        var app = Read(AppCss);
        var marker = app.IndexOf(TvMarker, StringComparison.Ordinal);
        Assert.True(marker > 0, "The tv form factor marker must exist in app.css.");
        var before = app[..marker];

        var thumbImage = Rule(before, ".needs-review-table__thumb-img");
        Assert.Contains("object-fit: cover;", thumbImage, StringComparison.Ordinal);
        var useThis = Rule(before, ".needs-review-table__use-this.app-button--text.app-control--compact");
        Assert.Contains("var(--tl-status-success)", useThis, StringComparison.Ordinal);
        Assert.Contains("var(--tl-status-success-soft)", useThis, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"#[0-9a-fA-F]{3,8}\b|rgba?\("), useThis);
        Assert.DoesNotContain("needs-review-table__use-this", app[marker..], StringComparison.Ordinal);
        Assert.DoesNotContain("needs-review-table__thumb-img", app[marker..], StringComparison.Ordinal);
    }

    private static string Rule(string css, string selector)
    {
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{selector} has no rule.");
        var open = css.IndexOf('{', start);
        var close = css.IndexOf('}', open);
        return css[open..(close + 1)];
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(
        FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
