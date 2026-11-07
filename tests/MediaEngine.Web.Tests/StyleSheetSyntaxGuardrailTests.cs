using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Every style sheet must parse. A Debug build copies broken CSS through untouched, but the Release publish minifies it and fails,
/// so a selector with an unclosed bracket or a lost closing brace only shows up in the container smoke build. This catches it in the
/// ordinary test run: braces balance in each file, and every selector closes the brackets it opens.
/// </summary>
public sealed class StyleSheetSyntaxGuardrailTests
{
    private static readonly string Web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
    private static readonly Regex Comments = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Strings = new("\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'", RegexOptions.Compiled);

    [Fact]
    public void EveryStyleSheetHasBalancedBracesAndClosedSelectors()
    {
        var problems = new List<string>();
        foreach (var path in StyleSheets())
        {
            var relative = Path.GetRelativePath(Web, path).Replace('\\', '/');
            var css = Strings.Replace(Comments.Replace(File.ReadAllText(path), string.Empty), "\"\"");
            var depth = 0;
            var start = 0;
            for (var i = 0; i < css.Length; i++)
            {
                if (css[i] == '{')
                {
                    var head = css[start..i];
                    var lastStatement = head.LastIndexOf(';');
                    var selector = (lastStatement >= 0 ? head[(lastStatement + 1)..] : head).Trim();
                    if (selector.Count(c => c == '(') != selector.Count(c => c == ')'))
                    {
                        problems.Add($"{relative}: unclosed bracket in \"{Shorten(selector)}\"");
                    }

                    depth++;
                    start = i + 1;
                }
                else if (css[i] == '}')
                {
                    depth--;
                    if (depth < 0)
                    {
                        problems.Add($"{relative}: a closing brace with no opening brace");
                        depth = 0;
                    }

                    start = i + 1;
                }
            }

            if (depth != 0)
            {
                problems.Add($"{relative}: {depth} brace(s) left open");
            }
        }

        Assert.True(problems.Count == 0, "Fix these style sheets (the Release publish cannot minify them): " + string.Join("; ", problems));
    }

    private static IEnumerable<string> StyleSheets() =>
        Directory.EnumerateFiles(Web, "*.css", SearchOption.AllDirectories)
            .Where(path =>
            {
                var relative = Path.GetRelativePath(Web, path).Replace('\\', '/');
                return !relative.StartsWith("obj/", StringComparison.Ordinal)
                    && !relative.StartsWith("bin/", StringComparison.Ordinal)
                    && !relative.Contains("/lib/", StringComparison.Ordinal)
                    && !relative.EndsWith(".min.css", StringComparison.Ordinal);
            });

    private static string Shorten(string selector) => selector.Length <= 80 ? selector : selector[..80] + "…";

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
