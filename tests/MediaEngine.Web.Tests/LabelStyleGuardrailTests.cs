namespace MediaEngine.Web.Tests;

/// <summary>
/// Labels (Look pass v2 A3): sentence case everywhere except a short list of overline styles for section labels, so the
/// number of uppercase rules stays below 20 and the faint text token stays retired.
/// </summary>
public sealed class LabelStyleGuardrailTests
{
    [Fact]
    public void FewerThanTwentyStylesSetUppercase()
    {
        var count = StyleFiles().Sum(path => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(path), @"text-transform:\s*uppercase").Count);

        Assert.True(count < 20, $"Found {count} uppercase rules; labels use sentence case except overline styles (limit 19).");
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
