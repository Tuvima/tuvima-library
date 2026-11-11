using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Source-level checks that the "Add condition" panel of the rule builder (collection editor, Set rules) shows every rule family:
/// the tab strip wraps onto a second line instead of running off the right edge with no hint that more tabs are hidden.
/// </summary>
public sealed class RuleBuilderPickerFamiliesCssTests
{
    private const string BuilderCss = "src/MediaEngine.Web/Components/Rules/SharedRuleBuilder.razor.css";

    [Fact]
    public void FamilyTabStrip_WrapsOntoMoreLines_SoNoFamilyIsCutOffAtTheRightEdge()
    {
        var rule = Rule(Read(BuilderCss), ".collection-rule-builder__picker-families");

        Assert.Contains("flex-wrap: wrap;", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void FamilyTabStrip_DoesNotScrollOrClipSideways()
    {
        var rule = Rule(Read(BuilderCss), ".collection-rule-builder__picker-families");

        Assert.DoesNotContain("overflow", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void FamilyTabs_KeepTheirLabelOnOneLine_SoOnlyWholeTabsMoveToTheNextRow()
    {
        var rule = Rule(Read(BuilderCss), ".collection-rule-builder__picker-families ::deep button");

        Assert.Contains("white-space: nowrap;", rule, StringComparison.Ordinal);
    }

    private static string Rule(string css, string selector)
    {
        var match = Regex.Match(css, "(?m)^" + Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}");
        Assert.True(match.Success, $"No rule for {selector}.");
        return match.Groups["body"].Value;
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

        return directory?.FullName ?? throw new DirectoryNotFoundException("MediaEngine.slnx not found above the test output folder.");
    }
}
