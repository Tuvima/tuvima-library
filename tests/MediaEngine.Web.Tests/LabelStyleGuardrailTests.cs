namespace MediaEngine.Web.Tests;

/// <summary>
/// Labels (Look pass v3 D7): sentence case everywhere. The wordmark is the one brand exemption and the only style that
/// sets uppercase; the faint text token stays retired.
/// </summary>
public sealed class LabelStyleGuardrailTests
{
    [Fact]
    public void ExactlyOneStyleSetsUppercase_TheWordmark()
    {
        var offenders = StyleFiles()
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(path), @"text-transform:\s*uppercase"))
            .Select(path => Path.GetFileName(path))
            .ToList();

        Assert.True(offenders.SequenceEqual(["MainLayout.razor.css"]), "Labels use sentence case; only the wordmark is uppercase. Found: " + string.Join(", ", offenders));
        var count = StyleFiles().Sum(path => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(path), @"text-transform:\s*uppercase").Count);
        Assert.Equal(1, count);
    }

    [Fact]
    public void TheWordmarkIsUppercase()
    {
        var css = File.ReadAllText(Path.Combine(StyleFiles().First(path => path.EndsWith("MainLayout.razor.css", StringComparison.Ordinal))));
        var rule = System.Text.RegularExpressions.Regex.Match(css, @"\.layout-shell__brand-wordmark\s*\{[^}]*\}").Value;

        Assert.Contains("text-transform: uppercase;", rule, StringComparison.Ordinal);
        Assert.Contains("var(--font-brand)", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void NoStyleReadsTheRetiredFaintTextToken()
    {
        var offenders = StyleFiles().Where(path => File.ReadAllText(path).Contains("--tl-text-faint", StringComparison.Ordinal)).ToList();

        Assert.True(offenders.Count == 0, "Use --tl-text-muted instead of --tl-text-faint: " + string.Join(", ", offenders));
    }

    private static IEnumerable<string> StyleFiles()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MediaEngine.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        var web = Path.Combine(root!, "src", "MediaEngine.Web");
        return Directory.EnumerateFiles(web, "*.css", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
