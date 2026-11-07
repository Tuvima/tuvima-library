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
    private const int NativeControlCeiling = 354;
    private const int AdHocFontSizeCeiling = 131;
    private const int HandTypedHexCeiling = 601;
    private const int ForcedOverrideCeiling = 1301;
    private const int HandTypedLayerCeiling = 307;
    // Class names made at runtime by a library or by joining a state name, so no source file spells them out.
    private static readonly HashSet<string> BuiltAtRuntime = ["maplibregl-ctrl-bottom-left", "maplibregl-ctrl-bottom-right", "notifications-page__channel--needs_attention"];
    private const int OldAppVariableCeiling = 159;
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
    public void TheTileMenuButtonIsAlwaysVisibleAtTheTopRightOfTheCover()
    {
        var css = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "wwwroot", "app.css"));
        var rule = Regex.Match(css, @"\.media-tile-actions \{[^}]*\}").Value;
        Assert.Contains("top:", rule.Replace(" ", ""));
        Assert.Contains("right:", rule.Replace(" ", ""));
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
    public void NoTokenAliasIsDefinedAtTheRoot()
    {
        // Look v3 D10: one name per token. A :root property that only forwards to another token is an alias; component-scoped
        // variables set per class (tones, pills, control heights) are not aliases and stay.
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "wwwroot");
        var aliases = new List<string>();
        foreach (var file in new[] { "app.css", "tuvima.tokens.css" })
        {
            var css = File.ReadAllText(Path.Combine(web, file));
            foreach (Match root in Regex.Matches(css, @":root\s*\{(?<body>[^}]*)\}"))
            {
                aliases.AddRange(Regex.Matches(root.Groups["body"].Value, @"(--app-[\w-]+)\s*:\s*var\(--(?:tl|font)-[\w-]+\)\s*;")
                    .Select(m => $"{file}: {m.Groups[1].Value}"));
            }
        }

        Assert.True(aliases.Count == 0, "Token aliases are retired; use the --tl-* token directly: " + string.Join(", ", aliases));
    }

    // A page nobody links to that is kept on purpose, with the reason. Components reached only by their type name in a string
    // (the three developer tabs, drawn by Settings.razor for a development build) are found by the string in the app's own code.
    private static readonly Dictionary<string, string> NeverRenderedAllowlist = new(StringComparer.Ordinal)
    {
        ["DesignSystemPreview"] = "The proof page for the Look rules: a page with its own address that nothing links to by design (it is a page, so it is skipped below).",
    };

    [Fact]
    public void EveryComponentIsRenderedByTheAppSomewhere()
    {
        // A part counts as rendered only when the app's own code (not a test, and not a comment) names it: a test that renders a
        // part nothing else draws keeps dead code alive, which is how thirty such parts built up.
        var root = Path.Combine(FindRepoRoot(), "src");
        var sep = Path.DirectorySeparatorChar;
        var ownWords = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(p => (p.EndsWith(".razor") || p.EndsWith(".cs") || p.EndsWith(".cshtml")) && !p.Contains($"{sep}obj{sep}") && !p.Contains($"{sep}bin{sep}")))
        {
            var text = Regex.Replace(File.ReadAllText(file), @"@\*.*?\*@|<!--.*?-->|/\*.*?\*/|(?<!:)//[^\n]*", string.Empty, RegexOptions.Singleline);
            var words = new HashSet<string>(Regex.Matches(text, @"[A-Za-z0-9_]+").Select(m => m.Value), StringComparer.Ordinal);
            ownWords[file] = words;
        }

        var unused = new List<string>();
        foreach (var path in ownWords.Keys.Where(p => p.EndsWith(".razor") && p.Contains($"{sep}MediaEngine.Web{sep}")).OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name == "_Imports" || NeverRenderedAllowlist.ContainsKey(name) || Regex.IsMatch(File.ReadAllText(path), @"^@page\b", RegexOptions.Multiline))
            {
                continue;
            }

            var own = path[..^".razor".Length];
            if (!ownWords.Any(other => !other.Key.StartsWith(own, StringComparison.Ordinal) && other.Value.Contains(name)))
            {
                unused.Add(name);
            }
        }

        Assert.True(unused.Count == 0, "Components nothing renders (delete them): " + string.Join(", ", unused));
        Assert.All(NeverRenderedAllowlist.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void NoStyleRuleTargetsAClassNothingUses()
    {
        var root = FindRepoRoot();
        // Only the app's own code counts as a use: a test, a document or a data file that spells a class name does not keep its rule alive.
        var blob = string.Join("\n", Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !p.Contains("node_modules"))
            .Where(p => new[] { ".razor", ".cs", ".js", ".cshtml", ".html", ".mjs", ".ts" }.Any(p.EndsWith))
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
                if (known.Contains(name) || BuiltAtRuntime.Contains(name))
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
