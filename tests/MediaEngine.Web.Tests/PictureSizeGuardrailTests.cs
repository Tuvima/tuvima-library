using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Right-sized pictures (spec D6): every picture a Dashboard screen draws asks the Engine for a
/// size, so a new screen cannot quietly download the full-size original. A picture's <c>src</c>
/// must go through a sizing helper (<c>MediaTileArtworkUrl.ForDrawnSize</c> / <c>Sized</c>, or
/// <c>PlaybackArtworkUrl</c>), or be listed with a reason in PictureSizeGuardrailAllowlist.txt
/// (static Dashboard files, addresses already sized upstream, photos with their own copies).
/// </summary>
public sealed class PictureSizeGuardrailTests
{
    private static readonly string RepoRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly string AllowlistPath =
        Path.Combine(RepoRoot, "tests", "MediaEngine.Web.Tests", "PictureSizeGuardrailAllowlist.txt");

    // Photos in View keep their own sized copies (160-2048) and are out of this rule's scope.
    private static readonly string[] ExcludedFolders = ["Components/View/", "Components/Pages/View"];

    private static readonly string[] SizingHelpers =
    [
        "MediaTileArtworkUrl.ForDrawnSize(",
        "MediaTileArtworkUrl.Sized(",
        "PlaybackArtworkUrl.",
    ];

    private static readonly Regex ImageTagRegex = new(@"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex SrcAttributeRegex = new(
        @"(?<![\w-])src\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>@[^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex CssUrlExpressionRegex = new(@"url\(\s*['""]?@", RegexOptions.Compiled);

    [Fact]
    public void EveryDashboardPictureAsksForASize()
    {
        var allowlist = LoadAllowlist();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var (relativePath, text) in ComponentFiles())
        {
            foreach (Match tag in ImageTagRegex.Matches(text))
            {
                var src = SrcAttributeRegex.Match(tag.Value);
                if (!src.Success)
                {
                    continue;
                }

                var value = Normalize(src.Groups["value"].Value);
                if (!value.Contains('@', StringComparison.Ordinal) || UsesSizingHelper(value))
                {
                    continue;
                }

                var key = $"{relativePath}|{value}";
                if (allowlist.Contains(key))
                {
                    used.Add(key);
                    continue;
                }

                problems.Add($"{relativePath}:{LineOf(text, tag.Index)} <img src=\"{value}\"> has no size. Use MediaTileArtworkUrl.ForDrawnSize(url, drawnPx) (and SrcSetForDrawnSize for srcset), or allowlist it with a reason.");
            }

            foreach (Match match in CssUrlExpressionRegex.Matches(text))
            {
                var line = LineText(text, match.Index);
                if (UsesSizingHelper(line))
                {
                    continue;
                }

                var key = $"{relativePath}|{Normalize(line)}";
                if (allowlist.Contains(key))
                {
                    used.Add(key);
                    continue;
                }

                problems.Add($"{relativePath}:{LineOf(text, match.Index)} CSS url(@...) picture has no size: {Normalize(line)}");
            }
        }

        foreach (var stale in allowlist.Except(used, StringComparer.Ordinal))
        {
            problems.Add($"PictureSizeGuardrailAllowlist.txt entry no longer matches any picture; remove it: {stale}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void AnUnsizedPictureFailsTheRule()
    {
        const string Markup = """<img src="@person.HeadshotUrl" alt="" />""";
        var src = SrcAttributeRegex.Match(ImageTagRegex.Match(Markup).Value);

        Assert.True(src.Success);
        Assert.False(UsesSizingHelper(Normalize(src.Groups["value"].Value)));
        Assert.True(UsesSizingHelper(Normalize("""@MediaTileArtworkUrl.ForDrawnSize(person.HeadshotUrl, 48)""")));
    }

    private static bool UsesSizingHelper(string value) =>
        SizingHelpers.Any(helper => value.Contains(helper, StringComparison.Ordinal));

    private static IEnumerable<(string RelativePath, string Text)> ComponentFiles()
    {
        var root = Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components");
        foreach (var path in Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(Path.Combine(RepoRoot, "src", "MediaEngine.Web"), path).Replace('\\', '/');
            if (ExcludedFolders.Any(folder => relative.StartsWith(folder, StringComparison.Ordinal)))
            {
                continue;
            }

            yield return (relative, File.ReadAllText(path));
        }
    }

    private static HashSet<string> LoadAllowlist() =>
        File.ReadAllLines(AllowlistPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line =>
            {
                // file|src expression|reason
                var parts = line.Split('|');
                Assert.True(parts.Length >= 3 && parts[2].Trim().Length > 0, $"Allowlist entry needs file|expression|reason: {line}");
                return $"{parts[0].Trim()}|{Normalize(parts[1])}";
            })
            .ToHashSet(StringComparer.Ordinal);

    private static string Normalize(string value) => Regex.Replace(value.Trim(), @"\s+", " ");

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string LineText(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = text.IndexOf('\n', index);
        return text[start..(end < 0 ? text.Length : end)];
    }
}
