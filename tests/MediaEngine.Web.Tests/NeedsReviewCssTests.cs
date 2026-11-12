using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Source-level checks that the Needs Review list keeps the approved mock's shape (needs-review-redesign screens 1, 2 and 5): rows as cards in
/// one list with a 44 px picture column first, the suggestion dropping under the item below 1180 px, phone cards at 600 px, and styles that
/// follow the look-pass ownership rules (tokens only, no deep selectors, no inline styles in the parts).
/// </summary>
public sealed class NeedsReviewCssTests
{
    private const string PartsFolder = "src/MediaEngine.Web/Components/Settings/NeedsReview";
    private const string RowCss = PartsFolder + "/NeedsReviewRow.razor.css";
    private const string PageCss = "src/MediaEngine.Web/Components/Settings/NeedsReviewPageContent.razor.css";
    private const string PageRazor = "src/MediaEngine.Web/Components/Settings/NeedsReviewPageContent.razor";
    private const string AppCss = "src/MediaEngine.Web/wwwroot/app.css";
    private const string TvMarker = "/* tv form factor:";

    [Fact]
    public void TheRow_IsAGridWithAPictureColumnFirst_ThenTheItem_TheSuggestionAndTheActions()
    {
        var row = Rule(Read(RowCss), ".nr-row");

        Assert.Contains("grid-template-columns: 44px minmax(0, 1fr) minmax(0, 400px) auto;", row, StringComparison.Ordinal);
        Assert.Contains("\"cover item suggestion actions\"", row, StringComparison.Ordinal);
        Assert.Contains("var(--tl-border-default)", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Below1180Pixels_TheSuggestionMovesUnderTheItem_AndOnAPhoneTheRowIsACard()
    {
        var css = Read(RowCss);

        var narrow = Block(css, "@media (max-width: 1180px)");
        Assert.Contains("\"cover item actions\"", narrow, StringComparison.Ordinal);
        Assert.Contains("\". suggestion suggestion\"", narrow, StringComparison.Ordinal);

        var phone = Block(css, "@media (max-width: 600px)");
        Assert.Contains("\"suggestion suggestion suggestion\"", phone, StringComparison.Ordinal);
        Assert.Contains("\"fixfull fixfull fixfull\"", phone, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePictureIsDashedOnlyWhenThereIsNoPicture_AndNeverAFilledPlaceholder()
    {
        var css = Read(PartsFolder + "/NeedsReviewCover.razor.css");

        Assert.Contains("inline-size: 44px;", Rule(css, ".nr-cover"), StringComparison.Ordinal);
        var none = Rule(css, ".nr-cover--none");
        Assert.Contains("background: transparent;", none, StringComparison.Ordinal);
        Assert.Contains("1px dashed", none, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageAndItsParts_UseNoInlineStyles_NoDeepSelectors_AndNoHandTypedColours()
    {
        var files = Directory.EnumerateFiles(Path.Combine(FindRepoRoot(), PartsFolder.Replace('/', Path.DirectorySeparatorChar)))
            .Concat([Path.Combine(FindRepoRoot(), PageRazor.Replace('/', Path.DirectorySeparatorChar)), Path.Combine(FindRepoRoot(), PageCss.Replace('/', Path.DirectorySeparatorChar))])
            .ToList();
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            if (file.EndsWith(".razor", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(" style=\"", text, StringComparison.Ordinal);
                Assert.DoesNotContain("InlineStyle=", text, StringComparison.Ordinal);
            }
            else if (file.EndsWith(".css", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("::deep", text, StringComparison.Ordinal);
                Assert.DoesNotMatch(new Regex(@"#[0-9a-fA-F]{3,8}\b|rgba?\("), text);
            }
        }
    }

    [Fact]
    public void TheFolderTooltipRoot_IsStyledFromTheGlobalSheet_BeforeTheTvMarker()
    {
        var app = Read(AppCss);
        var marker = app.IndexOf(TvMarker, StringComparison.Ordinal);
        Assert.True(marker > 0, "The tv form factor marker must exist in app.css.");

        var tip = Rule(app[..marker], ".nr-row__tip, .nr-row__tip[data-tooltip-wrapper-target=\"true\"]");
        Assert.Contains("min-width: 0;", tip, StringComparison.Ordinal);
        Assert.DoesNotContain("nr-row__tip", app[marker..], StringComparison.Ordinal);
    }

    [Fact]
    public void TheOldTableRules_AreGoneFromTheGlobalSheet()
    {
        var app = Read(AppCss);

        Assert.DoesNotContain("needs-review-table", app, StringComparison.Ordinal);
    }

    private static string Rule(string css, string selector)
    {
        var start = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{selector} has no rule.");
        var open = css.IndexOf('{', start);
        var close = css.IndexOf('}', open);
        return css[open..(close + 1)];
    }

    // The text between a media query's opening brace and its matching closing brace.
    private static string Block(string css, string header)
    {
        var start = css.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{header} is missing.");
        var open = css.IndexOf('{', start);
        var depth = 0;
        for (var index = open; index < css.Length; index++)
        {
            if (css[index] == '{')
            {
                depth++;
            }
            else if (css[index] == '}' && --depth == 0)
            {
                return css[open..(index + 1)];
            }
        }

        throw new InvalidOperationException($"{header} is not closed.");
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
