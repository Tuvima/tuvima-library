using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Keeps the Dashboard on one width scale. New CSS may only use the 600 / 840 / 1180 breakpoints and
/// the existing tokens (640, 960); older off-scale literals are listed in the allow-list fixture and
/// shrink over time. A new <c>100vh</c> is also refused (use <c>100dvh</c>).
/// See docs/reference/ui-supported-devices.md.
/// </summary>
public sealed partial class ResponsiveBreakpointGuardrailTests
{
    private const string AllowListPath = "tests/MediaEngine.Web.Tests/Fixtures/responsive-breakpoint-allowlist.txt";
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly HashSet<string> AllowedScale = new(StringComparer.Ordinal) { "600px", "840px", "1180px", "640px", "960px" };

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
    public void NoNewViewportHeightUnitsAppearInStyles()
    {
        var allowList = LoadAllowList();
        var offenders = new List<string>();

        foreach (var (file, count) in FindViewportHeightCounts())
        {
            var allowed = allowList
                .Where(entry => entry.StartsWith($"{file}:100vh=", StringComparison.Ordinal))
                .Select(entry => int.Parse(entry[(entry.LastIndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture))
                .FirstOrDefault();
            if (count > allowed)
            {
                offenders.Add($"{file} has {count} uses of 100vh (allowed {allowed})");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Use 100dvh instead of 100vh so mobile browser chrome does not clip layouts: " + string.Join("; ", offenders));
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

    private static IEnumerable<(string File, int Count)> FindViewportHeightCounts()
    {
        foreach (var path in StyleFiles())
        {
            var count = ViewportHeightRegex().Matches(File.ReadAllText(path)).Count;
            if (count > 0)
            {
                yield return (Relative(path), count);
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
}
