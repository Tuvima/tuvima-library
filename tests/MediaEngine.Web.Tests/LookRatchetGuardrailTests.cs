using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Look pass WP6, WP8, WP10 and WP12: counts that may only go down. Each ceiling is today's value; lowering a number in the
/// code lets you lower the ceiling here, and nothing can raise it. This is how pages are kept from drifting back to their own
/// pill shapes, text sizes and bare native controls while the migration continues.
/// </summary>
public sealed class LookRatchetGuardrailTests
{
    private const int RoundedEndCeiling = 0;
    private const int NativeControlCeiling = 374;
    private const int AdHocFontSizeCeiling = 150;
    private const int HandTypedHexCeiling = 902;
    private const int ForcedOverrideCeiling = 1501;
    private const int HandTypedLayerCeiling = 307;
    // Class names made at runtime by a library or by joining a state name, so no source file spells them out.
    private static readonly HashSet<string> BuiltAtRuntime = ["maplibregl-ctrl-bottom-left", "maplibregl-ctrl-bottom-right", "notifications-page__channel--needs_attention"];
    private const int OldAppVariableCeiling = 164;
    private const int DeadStyleClassCeiling = 0;

    private static IEnumerable<(string Relative, string Text)> WebFiles(params string[] extensions)
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        return Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
            .Where(p => extensions.Any(p.EndsWith))
            .Where(p =>
            {
                var relative = Path.GetRelativePath(web, p).Replace('\\', '/');
                return !relative.StartsWith("obj/") && !relative.StartsWith("bin/") && !relative.Contains("/lib/") && !relative.Contains("vendor/");
            })
            .Select(p => (Path.GetRelativePath(web, p).Replace('\\', '/'), File.ReadAllText(p)));
    }

    [Fact]
    public void FullyRoundedEndsOnlyGoDown()
    {
        var count = WebFiles(".css", ".razor").Sum(f => Regex.Matches(f.Text, @"border-radius:\s*(?:999px|9999px)").Count);
        Assert.True(count <= RoundedEndCeiling, $"Rounded ends rose to {count} (ceiling {RoundedEndCeiling}). Pills are square; round is only for avatars, switches, progress bars and the player dock.");
    }

    [Fact]
    public void TheTileMenuButtonIsAlwaysVisibleAtTheBottomRightOfTheCover()
    {
        var css = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "wwwroot", "app.css"));
        var rule = Regex.Match(css, @"\.media-tile-actions \{[^}]*\}").Value;
        Assert.Contains("aspect-ratio: var(--media-tile-media-aspect)", rule);
        Assert.Contains("align-items: flex-end", rule);
        Assert.Contains("justify-content: flex-end", rule);
        Assert.DoesNotContain("opacity: 0", rule);
    }

    [Fact]
    public void TheMessageBoxNeverGuessesDestructiveStylingFromTheButtonLabel()
    {
        var box = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Components", "Shared", "AppMessageBox.razor"));
        Assert.DoesNotContain("Regex", box);
        Assert.Contains("Destructive", box);
    }

    [Fact]
    public void HandTypedHexColorsOnlyGoDown()
    {
        var count = WebFiles(".css", ".razor")
            .Where(f => f.Relative != "wwwroot/tuvima.tokens.css")
            .Sum(f => Regex.Matches(f.Text, @"#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{3})(?![0-9a-zA-Z_-])").Count);
        Assert.True(count <= HandTypedHexCeiling, $"Hand-typed hex colors rose to {count} (ceiling {HandTypedHexCeiling}). Colors live in tuvima.tokens.css; ask for a token or an icon tone.");
    }

    [Fact]
    public void ForcedOverridesOnlyGoDown()
    {
        var count = WebFiles(".css", ".razor").Sum(f => Regex.Matches(f.Text, "!important").Count);
        Assert.True(count <= ForcedOverrideCeiling, $"Forced overrides rose to {count} (ceiling {ForcedOverrideCeiling}). Fix the rule order instead of forcing.");
    }

    [Fact]
    public void HandTypedLayersOnlyGoDown()
    {
        var count = WebFiles(".css", ".razor").Sum(f => Regex.Matches(f.Text, @"z-index:\s*[0-9]+").Count);
        Assert.True(count <= HandTypedLayerCeiling, $"Hand-typed layers rose to {count} (ceiling {HandTypedLayerCeiling}). Use the layer tokens (menu, popover, dialog, toast, player).");
    }

    [Fact]
    public void TheOldAppColorVariablesOnlyGoDown()
    {
        var count = WebFiles(".css", ".razor", ".cs", ".js").Sum(f => Regex.Matches(f.Text, @"var\(--app-").Count);
        Assert.True(count <= OldAppVariableCeiling, $"Uses of the old --app-* variables rose to {count} (ceiling {OldAppVariableCeiling}). Use the --tl-* tokens.");
    }

    [Fact]
    public void EveryComponentIsUsedSomewhere()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Components");
        var all = Directory.EnumerateFiles(Path.Combine(FindRepoRoot(), "src"), "*.*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(FindRepoRoot(), "tests"), "*.cs", SearchOption.AllDirectories))
            .Where(p => (p.EndsWith(".razor") || p.EndsWith(".cs")) && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(p => (Path: p, Text: File.ReadAllText(p)))
            .ToList();
        var unused = new List<string>();
        foreach (var path in Directory.EnumerateFiles(web, "*.razor", SearchOption.AllDirectories).Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(path);
            if (Regex.IsMatch(text, @"^@page\b", RegexOptions.Multiline))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            var pattern = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(name) + @"(?![A-Za-z0-9_])");
            if (!all.Any(f => f.Path != path && pattern.IsMatch(f.Text)))
            {
                unused.Add(name);
            }
        }

        Assert.True(unused.Count == 0, "Components nothing uses (delete them): " + string.Join(", ", unused));
    }

    [Fact]
    public void NoStyleRuleTargetsAClassNothingUses()
    {
        var root = FindRepoRoot();
        var blob = string.Join("\n", new[] { "src", "tests", "clients" }
            .Select(d => Path.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories))
            .Where(p => !p.EndsWith(".css") && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !p.Contains("node_modules"))
            .Where(p => new[] { ".razor", ".cs", ".js", ".cshtml", ".html", ".json", ".md", ".mjs", ".ts", ".kt", ".swift", ".xml" }.Any(p.EndsWith))
            .Select(File.ReadAllText));
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match word in Regex.Matches(blob, @"[A-Za-z_][\w-]*"))
        {
            var value = word.Value;
            known.Add(value);
            for (var i = 0; i < value.Length - 1; i++)
            {
                if (value[i] is '-' or '_')
                {
                    known.Add(value[..(i + 1)]);
                }
            }
        }

        var dead = new SortedSet<string>();
        foreach (var file in WebFiles(".css").Where(f => f.Relative != "wwwroot/tuvima.tokens.css"))
        {
            var selectors = Regex.Replace(file.Text, @"\{[^}]*\}", "{}");
            selectors = Regex.Replace(selectors, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            foreach (Match m in Regex.Matches(selectors, @"\.([a-zA-Z_][\w-]*)"))
            {
                var name = m.Groups[1].Value;
                if (known.Contains(name))
                {
                    continue;
                }

                var prefix = Regex.Match(name, @"^(.*[-_])[^-_]+$");
                if (prefix.Success && known.Contains(prefix.Groups[1].Value))
                {
                    continue;
                }

                dead.Add(name);
            }
        }

        Assert.True(dead.Count <= DeadStyleClassCeiling, $"{dead.Count} style classes nothing uses (ceiling {DeadStyleClassCeiling}): " + string.Join(", ", dead.Take(15)));
    }

    [Fact]
    public void EveryToneSetsItsColorBackgroundAndBorderVariables()
    {
        var css = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "wwwroot", "app.css"));
        foreach (var tone in new[] { "primary", "info", "success", "warning", "error" })
        {
            var rule = string.Concat(Regex.Matches(css, @"\.app-tone--" + tone + @" \{[^}]*\}").Select(m => m.Value));
            Assert.Contains("--app-tone-color:", rule);
            Assert.Contains("--app-tone-bg:", rule);
            Assert.Contains("--app-tone-border:", rule);
        }
    }

    [Fact]
    public void NoTextSmallerThanTwelvePixels()
    {
        var offenders = WebFiles(".css", ".razor")
            .SelectMany(f => Regex.Matches(f.Text, @"font-size:\s*([0-9.]+)(px|rem|em)").Select(m => (f.Relative, m)))
            .Where(x =>
            {
                var value = double.Parse(x.m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                return (x.m.Groups[2].Value == "px" ? value : value * 16) < 12;
            })
            .Select(x => x.Relative + ": " + x.m.Value)
            .ToList();
        Assert.True(offenders.Count == 0, "Type scale starts at 12 px: " + string.Join("; ", offenders.Take(10)));
    }

    [Fact]
    public void HandTypedTextSizesOnlyGoDown()
    {
        var count = WebFiles(".css", ".razor").Sum(f => Regex.Matches(f.Text, @"font-size:\s*[0-9.]+(?:px|rem)").Count);
        Assert.True(count <= AdHocFontSizeCeiling, $"Hand-typed text sizes rose to {count} (ceiling {AdHocFontSizeCeiling}). Use the ladder tokens: --tl-font-size-xs, -secondary, -sm, -md, -lg, -dialog-title, -xl, -hero.");
    }

    [Fact]
    public void BareNativeControlsOnlyGoDown()
    {
        var count = WebFiles(".razor").Sum(f => Regex.Matches(f.Text, @"<AppNative(?:Button|Input|Select)\b").Count);
        Assert.True(count <= NativeControlCeiling, $"Bare native controls rose to {count} (ceiling {NativeControlCeiling}). Use AppButton, AppTextField or the shared select.");
    }

    [Fact]
    public void TheHeroUsesTheOneHeightToken()
    {
        var tokens = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "wwwroot", "tuvima.tokens.css"));
        Assert.Contains("--tl-hero-height: 70svh;", tokens);
        var carousel = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Components", "Cinematic", "CinematicHeroCarousel.razor.css"));
        Assert.Contains("var(--tl-hero-height)", carousel);
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
