using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Look pass v3.1 D8 to D10: the type scale has one size token and one weight token per job, and the shared parts (tabs, text
/// typed into a field, a dropdown's options, the dialog body) name a job instead of a number.
/// </summary>
public sealed class TypeScaleJobsTests
{
    private static string Root { get; } = FindRepoRoot();

    private static string Wwwroot(string file) => File.ReadAllText(Path.Combine(Root, "src", "MediaEngine.Web", "wwwroot", file));

    private static string Compact(string css) => Regex.Replace(css, @"\s+", " ");

    [Theory]
    [InlineData("hero", "32px", "700")]
    [InlineData("page-title", "24px", "700")]
    [InlineData("heading", "20px", "600")]
    [InlineData("subheading", "16px", "600")]
    [InlineData("menu", "16px", "500")]
    [InlineData("control", "14px", "500")]
    [InlineData("body", "14px", "400")]
    [InlineData("secondary", "13px", "400")]
    [InlineData("label", "13px", "500")]
    [InlineData("caption", "12px", "400")]
    [InlineData("overline", "12px", "600")]
    public void EveryTypeJobHasItsSizeAndWeightToken(string job, string size, string weight)
    {
        var tokens = Wwwroot("tuvima.tokens.css");

        Assert.Matches(new Regex($@"--tl-type-{job}-size:\s*{size};"), tokens);
        Assert.Matches(new Regex($@"--tl-type-{job}-weight:\s*{weight};"), tokens);
    }

    [Fact]
    public void ThePageTitleIsTwentyEightPixelsOnWideScreens()
    {
        var tokens = Compact(Wwwroot("tuvima.tokens.css"));

        Assert.Matches(@"@media \(min-width: 1180px\) \{ :root \{ --tl-type-page-title-size: 28px; \} \}", tokens);
    }

    [Fact]
    public void TextTypedIntoAFieldIsTheBodyJobAndSixteenPixelsOnAPhone()
    {
        var tokens = Compact(Wwwroot("tuvima.tokens.css"));
        var fields = Wwwroot("native-fields.css");

        Assert.Contains(":root { --tl-field-text-size: var(--tl-type-body-size); }", tokens);
        Assert.Matches(@"@media \(max-width: 600px\) \{ :root \{ --tl-field-text-size: var\(--tl-font-size-md\); \} \}", tokens);
        Assert.Matches(@"--tl-font-size-md:\s*16px;", tokens);

        foreach (var selector in new[] { @"\.tl-input \{", @"input\.app-text-field \{" })
        {
            var rule = Regex.Match(fields, selector + @"[^}]*\}").Value;
            Assert.Contains("font-size: var(--tl-field-text-size);", rule);
            Assert.Contains("font-weight: var(--tl-type-body-weight);", rule);
        }
    }

    [Fact]
    public void TabLabelsAreTheControlJob()
    {
        var rule = Regex.Match(Wwwroot("app.css"), @"\.tl-tab \{[^}]*\}").Value;

        Assert.Contains("font-size: var(--tl-type-control-size);", rule);
        // The weight is the long-standing control weight, which is the Control job's 500.
        Assert.Contains("font-weight: var(--tl-control-font-weight);", rule);
        Assert.Matches(@"--tl-control-font-weight:\s*var\(--tl-font-weight-medium\);", Wwwroot("tuvima.tokens.css"));
    }

    [Fact]
    public void ADropdownsOptionsAreTheControlJobWhilePopUpMenuRowsStayAtTheMenuSize()
    {
        var css = Wwwroot("app.css");
        var options = Regex.Match(css, @"\.tl-select-options \.tl-list-item \{[^}]*\}").Value;
        var menuRows = Regex.Match(css, @"\.app-menu-popover \.tl-list-item,\s*\.app-menu-item,[^{]*\{[^}]*\}").Value;

        Assert.Contains("font-size: var(--tl-type-control-size) !important;", options);
        Assert.Contains("font-size: var(--tl-menu-row-font-size) !important;", menuRows);
        Assert.Matches(@"--tl-menu-row-font-size:\s*16px;", Wwwroot("tuvima.tokens.css"));
    }

    [Fact]
    public void TheDialogBodyIsTheBodyJob()
    {
        var css = File.ReadAllText(Path.Combine(Root, "src", "MediaEngine.Web", "Components", "Shared", "AppDialogShell.razor.css"));
        var rule = Regex.Match(css, @"\.app-dialog-shell__body \{[^}]*\}").Value;

        Assert.Contains("font-size: var(--tl-type-body-size);", rule);
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
