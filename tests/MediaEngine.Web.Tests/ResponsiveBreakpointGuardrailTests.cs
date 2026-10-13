using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Keeps the Dashboard on one width scale. New CSS may only use the 600 / 840 / 1180 breakpoints and
/// the existing tokens (640, 960); older off-scale literals are listed in the allow-list fixture and
/// shrink over time. A bare <c>100vh</c> is refused: keep it only as a fallback line next to its <c>100dvh</c> twin.
/// See docs/reference/ui-supported-devices.md.
/// </summary>
public sealed partial class ResponsiveBreakpointGuardrailTests
{
    private const string AllowListPath = "tests/MediaEngine.Web.Tests/Fixtures/responsive-breakpoint-allowlist.txt";
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly HashSet<string> AllowedScale = new(StringComparer.Ordinal) { "600px", "840px", "1180px", "640px", "960px", "1023px" };

    [Fact]
    public void MediaWidthLiteralsStayOnTheSupportedScale()
    {
        var allowList = LoadAllowList();
        var offenders = new List<string>();

        foreach (var (file, value) in FindMediaWidthLiterals())
        {
            if (AllowedScale.Contains(value) || allowList.Contains($"{file}:{value}"))
            {
                continue;
            }

            offenders.Add($"{file} uses @media width {value}");
        }

        Assert.True(
            offenders.Count == 0,
            "New CSS breakpoints must use 600px, 840px or 1180px (or the existing 640px / 960px tokens). Off-scale values: "
            + string.Join("; ", offenders.Distinct())
            + ". See docs/reference/ui-supported-devices.md.");
    }

    [Fact]
    public void ViewportHeightUnitsOnlyAppearAsFallbackLines()
    {
        var offenders = FindBareViewportHeightLines();

        Assert.True(
            offenders.Count == 0,
            "Use 100dvh instead of 100vh (keep 100vh only as a fallback line directly before its 100dvh twin) so mobile browser chrome does not clip layouts: "
            + string.Join("; ", offenders));
    }

    [Fact]
    public void TouchTargetContractIsDefinedInAppCss()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "wwwroot", "app.css"));
        var tokens = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "wwwroot", "tuvima.tokens.css"));

        Assert.Contains("--tl-tap-target-min: 44px", tokens, StringComparison.Ordinal);
        Assert.Contains("Shared 44px tap-target contract", css, StringComparison.Ordinal);
        Assert.Contains("(hover: none), (pointer: coarse), (max-width: 1023px)", css, StringComparison.Ordinal);
        foreach (var selector in new[] { "a.tl-button", "[role=\"option\"]", "[role=\"menuitem\"]", "[role=\"tab\"]", ".tl-chip", ".tl-icon-button" })
        {
            Assert.Contains(selector, css, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ScannerFlagsAViewportHeightWithoutDynamicTwin()
    {
        Assert.Single(BareViewportHeightLines(new[] { "a { min-height: 100vh; }", "b { color: red; }" }));
        Assert.Empty(BareViewportHeightLines(new[] { "a { min-height: 100vh;", " min-height: 100dvh; }" }));
        Assert.Empty(BareViewportHeightLines(new[] { "a { max-height: 100vh; max-height: 100dvh; }" }));
    }

    [Fact]
    public void ScannerDetectsAnOffScaleBreakpointInSampleCss()
    {
        var hits = MediaWidthRegex().Matches("@media (max-width: 777px) { a { b: c } }");

        Assert.Single(hits);
        Assert.Equal("777", hits[0].Groups["value"].Value);
    }

    private static IEnumerable<(string File, string Value)> FindMediaWidthLiterals()
    {
        foreach (var path in StyleFiles())
        {
            var file = Relative(path);
            foreach (var line in File.ReadLines(path))
            {
                if (!line.Contains("@media", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in MediaWidthRegex().Matches(line))
                {
                    yield return (file, match.Groups["value"].Value + match.Groups["unit"].Value);
                }
            }
        }
    }

    private static List<string> FindBareViewportHeightLines()
    {
        var offenders = new List<string>();
        foreach (var path in StyleFiles())
        {
            foreach (var line in BareViewportHeightLines(File.ReadAllLines(path)))
            {
                offenders.Add($"{Relative(path)}: {line.Trim()}");
            }
        }

        return offenders;
    }

    private static IEnumerable<string> BareViewportHeightLines(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (!ViewportHeightRegex().IsMatch(lines[i]))
            {
                continue;
            }

            var sameLineTwin = DynamicViewportHeightRegex().IsMatch(lines[i]);
            var nextLineTwin = i + 1 < lines.Length && DynamicViewportHeightRegex().IsMatch(lines[i + 1]);
            if (!sameLineTwin && !nextLineTwin)
            {
                yield return lines[i];
            }
        }
    }

    private static IEnumerable<string> StyleFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "MediaEngine.Web"), "*.css", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Relative(string path) =>
        Path.GetRelativePath(RepoRoot, path).Replace(Path.DirectorySeparatorChar, '/');

    private static HashSet<string> LoadAllowList() =>
        File.ReadAllLines(Path.Combine(RepoRoot, AllowListPath.Replace('/', Path.DirectorySeparatorChar)))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    [GeneratedRegex(@"(?:(?:min|max)-)?width\s*(?::|<=|>=|<|>)\s*(?<value>\d+(?:\.\d+)?)(?<unit>px|em|rem)", RegexOptions.IgnoreCase)]
    private static partial Regex MediaWidthRegex();

    [GeneratedRegex(@"(?<![\w-])100vh(?![\w-])")]
    private static partial Regex ViewportHeightRegex();

    [GeneratedRegex(@"(?<![\w-])100[ds]vh(?![\w-])")]
    private static partial Regex DynamicViewportHeightRegex();
}
