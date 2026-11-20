using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The static half of the automatic phone check (Look v3.2 D12, D10). A change that adds a hover-only reveal, text under 12 px, or "on a computer" wording
/// outside setup turns this red. The old ones are counted in ceilings that only go down. The render half (targets under 44, sideways overflow at 390 and 768)
/// is <c>tools/Check-PhoneStandards.mjs</c>, which needs a running Dashboard and goes in the Local checks.
/// </summary>
public sealed partial class PhoneStandardsGuardrailTests
{
    /// <summary>Hover-only reveals that predate Look v3.2. Fix them (show on touch, or wrap the rule in @media (hover: hover)); never raise this.</summary>
    private const int HoverOnlyRevealCeiling = 23;

    /// <summary>"On a computer" wording outside setup that predates Look v3.2. WP6 (Settings on a phone) takes it to 0; never raise this.</summary>
    private const int OnAComputerWordingCeiling = 39;

    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void NoNewHoverOnlyRevealsAppear()
    {
        var offenders = StyleFiles().SelectMany(p => HoverOnlyReveals(File.ReadAllText(p)).Select(s => $"{Relative(p)}: {s}")).ToList();

        Assert.True(
            offenders.Count <= HoverOnlyRevealCeiling,
            $"Hover-only reveals rose to {offenders.Count} (ceiling {HoverOnlyRevealCeiling}). Anything hover shows must also be visible on touch: wrap the hover rule in @media (hover: hover) and keep the thing visible otherwise. Found: "
            + string.Join("; ", offenders));
    }

    [Fact]
    public void NoTextSmallerThan12PixelsIsDrawn()
    {
        var offenders = new List<string>();
        foreach (var path in StyleFiles())
        {
            foreach (var size in TextSmallerThanTwelve(File.ReadAllText(path)))
            {
                offenders.Add($"{Relative(path)}: font-size {size}px");
            }
        }

        Assert.True(offenders.Count == 0, "Text under 12 px is unreadable on a phone. Use --tl-font-size-xs (12px) or larger: " + string.Join("; ", offenders));
    }

    [Fact]
    public void ComputerOnlyWordingOutsideSetupOnlyShrinks()
    {
        var hits = new List<string>();
        var src = Path.Combine(RepoRoot, "src", "MediaEngine.Web");
        foreach (var path in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories).Where(p => p.EndsWith(".razor", StringComparison.Ordinal) || p.EndsWith(".cs", StringComparison.Ordinal)))
        {
            var relative = Relative(path);
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("Setup", StringComparison.Ordinal))
            {
                continue;
            }

            hits.AddRange(File.ReadLines(path).Where(l => ComputerWording().IsMatch(l)).Select(_ => relative));
        }

        Assert.True(
            hits.Count <= OnAComputerWordingCeiling,
            $"\"On a computer\" wording rose to {hits.Count} (ceiling {OnAComputerWordingCeiling}). Everything except first-time setup works on a phone (Shy, 2026-11-12). Files: " + string.Join(", ", hits.Distinct()));
    }

    [Fact]
    public void ScannerFlagsAHoverOnlyRevealAndAllowsAMouseOnlyOne()
    {
        Assert.Single(HoverOnlyReveals(".row:hover .actions { opacity: 1; }"));
        Assert.Empty(HoverOnlyReveals("@media (hover: hover) { .row:hover .actions { opacity: 1; } }"));
        Assert.Empty(HoverOnlyReveals(".row:hover { background: red; }"));
        Assert.Empty(HoverOnlyReveals("@keyframes x { 50% { opacity: 1; } }"));
    }

    [Fact]
    public void ScannerFlagsTextUnderTwelvePixels()
    {
        Assert.Single(TextSmallerThanTwelve(".a { font-size: 10px; } .b { font-size: 12px; } .c { font-size: 0.8rem; }"));
    }

    private static IEnumerable<string> HoverOnlyReveals(string css)
    {
        css = CssComment().Replace(css, string.Empty);
        var scopes = new Stack<(bool Hover, bool Frames)>();
        foreach (Match token in CssToken().Matches(css))
        {
            if (token.Value == "}")
            {
                if (scopes.Count > 0)
                {
                    scopes.Pop();
                }

                continue;
            }

            var selector = token.Groups["sel"].Value.Trim();
            var parentHover = scopes.Count > 0 && scopes.Peek().Hover;
            var parentFrames = scopes.Count > 0 && scopes.Peek().Frames;
            var isAt = selector.StartsWith('@');
            var hoverOnly = parentHover || (isAt && HoverMedia().IsMatch(selector));
            var frames = parentFrames || (isAt && selector.Contains("keyframes", StringComparison.OrdinalIgnoreCase));
            scopes.Push((hoverOnly, frames));
            if (isAt || hoverOnly || frames || !selector.Contains(":hover", StringComparison.Ordinal))
            {
                continue;
            }

            if (RevealDeclaration().IsMatch(token.Groups["body"].Value))
            {
                yield return selector.Length > 90 ? selector[..90] : selector;
            }
        }
    }

    private static IEnumerable<double> TextSmallerThanTwelve(string css)
    {
        foreach (Match m in FontSizePx().Matches(CssComment().Replace(css, string.Empty)))
        {
            var size = double.Parse(m.Groups["v"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (size < 12)
            {
                yield return size;
            }
        }
    }

    private static IEnumerable<string> StyleFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot, "src", "MediaEngine.Web"), "*.css", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}vendor{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Relative(string path) => Path.GetRelativePath(RepoRoot, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CssComment();

    [GeneratedRegex(@"(?<sel>[^{};]*)\{(?<body>[^{}]*)(?=\}|\{)?|\}", RegexOptions.Singleline)]
    private static partial Regex CssToken();

    [GeneratedRegex(@"hover\s*:\s*hover", RegexOptions.IgnoreCase)]
    private static partial Regex HoverMedia();

    [GeneratedRegex(@"opacity\s*:\s*(?:1|0?\.[5-9]\d*)\s*(?:;|$)|visibility\s*:\s*visible|display\s*:\s*(?:block|flex|inline-flex|inline-block|grid)|pointer-events\s*:\s*auto", RegexOptions.IgnoreCase)]
    private static partial Regex RevealDeclaration();

    [GeneratedRegex(@"font-size\s*:\s*(?<v>\d+(?:\.\d+)?)px", RegexOptions.IgnoreCase)]
    private static partial Regex FontSizePx();

    [GeneratedRegex(@"on a computer|computer or tablet|on a desktop or tablet|use a computer", RegexOptions.IgnoreCase)]
    private static partial Regex ComputerWording();
}
