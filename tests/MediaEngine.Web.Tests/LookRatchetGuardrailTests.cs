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
    private const int NativeControlCeiling = 276;
    private const int AdHocFontSizeCeiling = 39;
    private const int HandTypedHexCeiling = 147;
    private const int ForcedOverrideCeiling = 1040;
    private const int HandTypedLayerCeiling = 271;
    // Class names made at runtime by a library or by joining a state name, so no source file spells them out.
    private static readonly HashSet<string> BuiltAtRuntime = ["maplibregl-ctrl-bottom-left", "maplibregl-ctrl-bottom-right", "notifications-page__channel--needs_attention"];
    private const int OldAppVariableCeiling = 151;
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

    // Any renamed copy of the toast part (a service or a host, with or without the interface prefix) counts too.
    private static readonly Regex ToastServiceOrHost = new(@"\bI?\w*Toast(Service|Host)\b", RegexOptions.CultureInvariant);

    [Fact]
    public void TheOldAppDialogPartHasNotReturned()
    {
        // One window part: AppDialogShell (hosted with Framed when a page opens it inline). AppDialogFrame and AppDialogHost are its frame and host.
        var offenders = WebFiles(".razor")
            .Where(f => Regex.IsMatch(f.Text, @"<AppDialog[\s>]") || f.Relative.EndsWith("Shared/AppDialog.razor", StringComparison.Ordinal))
            .Select(f => f.Relative)
            .ToList();
        Assert.True(offenders.Count == 0, $"The old AppDialog window part was retired. Use AppDialogShell (Framed=\"true\" for an inline window), but these files use or define AppDialog: {string.Join(", ", offenders)}.");
    }

    [Fact]
    public void ThereAreNoToasts()
    {
        var offenders = WebFiles(".razor", ".cs")
            .Where(f => new[] { "IAppToastService", "AppToastService", "AppToastHost", "Snackbar" }.Any(word => f.Text.Contains(word, StringComparison.Ordinal))
                || ToastServiceOrHost.IsMatch(f.Text))
            .Select(f => f.Relative)
            .ToList();
        Assert.True(offenders.Count == 0, $"The toast part has been removed and must not return, but these files mention it: {string.Join(", ", offenders)}. There are no pop-up notices: glow the changed row, show the quiet Saved line, or put an alert in place (design system, Feedback after an action).");
    }

    [Fact]
    public void HandTypedHexColorsOnlyGoDown()
    {
        var count = WebFiles(".css", ".razor")
            .Where(f => f.Relative != "wwwroot/tuvima.tokens.css")
            .Sum(f => Regex.Matches(f.Text, @"#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{3})(?![0-9a-zA-Z_-])").Count);
        Assert.True(count <= HandTypedHexCeiling, $"Hand-typed hex colors rose to {count} (ceiling {HandTypedHexCeiling}). Colors live in tuvima.tokens.css; ask for a token or an icon tone.");
    }

    // Look v3.1 D7: no component style and no shared sheet (the token file aside, which is where colors live) holds a hand-typed color in any form (hex, rgb(), rgba(), hsl(), hsla()
    // with typed numbers, or a named color, including a var() fallback), outside the written lists below. The readers' stylesheets
    // under wwwroot/css/ are left out (page themes are theirs).
    private static bool IsColorLocked(string relative) =>
        relative.EndsWith(".razor.css", StringComparison.Ordinal)
        || (relative.StartsWith("wwwroot/", StringComparison.Ordinal) && relative.EndsWith(".css", StringComparison.Ordinal)
            && relative.IndexOf('/', "wwwroot/".Length) < 0 && relative != "wwwroot/tuvima.tokens.css");

    // The written list of real exceptions to the zero lock above (D7): colors taken from a cover's artwork, charts, subtitle color choices,
    // the map, avatars and the readers' page themes. Keyed by a file path or a path prefix under src/MediaEngine.Web, each with its reason.
    private static readonly Dictionary<string, string> ColorLockExceptions = new(StringComparer.Ordinal)
    {
        ["Components/View/ViewMap.razor.css"] = "The map draws its own base, labels and hotspots on top of map tiles, so its colors are the map's, not the app's.",
        ["Components/Player/SubtitleAppearanceView.razor.css"] = "Shows the viewer's subtitle color choices, which are literal colors the viewer picks.",
        ["Components/Editor/"] = "Temporary: the editor window's styles are swept last, after the Universal editor v6 build has merged (WP11).",
    };

    // Files that are locked except for a counted few, each with how many and why. The count may only go down.
    private static readonly Dictionary<string, (int Max, string Reason)> ColorLockAllowances = new(StringComparer.Ordinal)
    {
        ["wwwroot/app.css"] = (29, "The subtitle shadow and high-contrast outline are literal black, the same value the subtitle preview uses (a subtitle color choice); and, temporarily, the editor window's block, swept after the Universal editor v6 build has merged."),
    };

    // A color built from a variable's channels, such as rgba(var(--hero-accent-rgb), .4) or hsl(var(--person-avatar-hue) 40% 30%), is
    // not hand-typed: the channels come from the item's artwork or the person's name (D7 exceptions), set in code.
    private static readonly Regex AnyHandTypedColor = new(
        @"#(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{4}|[0-9a-fA-F]{3})(?![0-9a-zA-Z_-])"
        + @"|(?<![\w-])(?:rgb|rgba|hsl|hsla)\((?!\s*(?:calc\(\s*)?var\(--)"
        + @"|(?<![\w.#-])(?:white|black|red|green|blue|gray|grey|silver|purple|orange|yellow|pink|navy|teal|gold|maroon|lime|aqua|cyan|magenta|brown)(?![\w-])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static List<string> HandTypedColors(string css) =>
        AnyHandTypedColor.Matches(Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline)).Select(m => m.Value).ToList();

    [Fact]
    public void NoStyleHoldsAHandTypedColorOutsideTheWrittenLists()
    {
        var counted = WebFiles(".css")
            .Where(f => IsColorLocked(f.Relative))
            .Where(f => !ColorLockExceptions.Keys.Any(path => f.Relative.StartsWith(path, StringComparison.Ordinal)))
            .Select(f => (f.Relative, Colors: HandTypedColors(f.Text)))
            .Where(f => f.Colors.Count > 0)
            .ToList();
        var offenders = counted
            .Where(f => !ColorLockAllowances.ContainsKey(f.Relative))
            .Select(f => $"{f.Relative} ({f.Colors.Count}, first {f.Colors[0]})")
            .ToList();
        Assert.True(offenders.Count == 0, "Hand-typed colors; use a --tl-* color, or a wash/scrim step (--tl-wash-light-*, --tl-scrim-dark-*): " + string.Join("; ", offenders));
        Assert.All(ColorLockAllowances, pair =>
        {
            var count = counted.Where(f => f.Relative == pair.Key).Sum(f => f.Colors.Count);
            Assert.True(count <= pair.Value.Max, $"{pair.Key} has {count} hand-typed colors, the list allows {pair.Value.Max} ({pair.Value.Reason}).");
            Assert.True(count > 0, $"{pair.Key} holds no hand-typed color any more; remove it from the list.");
        });
    }

    [Fact]
    public void TheColorLockExceptionsAreReal()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        Assert.True(IsColorLocked("Components/Collections/CollectionEditorShell.razor.css"));
        Assert.True(IsColorLocked("wwwroot/app.css"));
        Assert.True(IsColorLocked("Shared/MainLayout.razor.css"));
        Assert.False(IsColorLocked("wwwroot/tuvima.tokens.css"));
        Assert.False(IsColorLocked("wwwroot/css/reader.css"));
        Assert.All(ColorLockExceptions, pair =>
        {
            Assert.False(string.IsNullOrWhiteSpace(pair.Value));
            Assert.True(File.Exists(Path.Combine(web, pair.Key)) || Directory.Exists(Path.Combine(web, pair.Key)), $"Exception {pair.Key} names a path that does not exist.");
        });
        Assert.All(ColorLockAllowances, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value.Reason)));
    }

    [Theory]
    [InlineData("a{color:#fff}", 1)]
    [InlineData("a{color:var(--tl-text-primary, #A78BFA)}", 1)]
    [InlineData("a{border:1px solid rgba(255,255,255,.1)}", 1)]
    [InlineData("a{background:hsl(10 20% 30%)}", 1)]
    [InlineData("a{background:rgba(var(--hero-accent-rgb), .4);color:hsl(var(--person-avatar-hue) 40% 30%)}", 0)]
    [InlineData("a{background:color-mix(in srgb, white 3%, transparent)}", 1)]
    [InlineData("a{white-space:nowrap;color:var(--tl-text-primary);background:var(--tl-wash-light-1)}", 0)]
    [InlineData("/* was #fff */ .red-flag{color:currentColor;background:transparent}", 0)]
    public void TheColorLockSeesEveryFormOfHandTypedColor(string css, int expected) =>
        Assert.Equal(expected, HandTypedColors(css).Count);

    // Look pass v3.1 D11: the written list of places that may still write a text size by hand, each with how many and why.
    // A key is a file or a folder prefix under src/MediaEngine.Web; a folder's number is the total for the folder.
    private static readonly Dictionary<string, (int Max, string Reason)> OffScaleFontSizeAllowlist = new(StringComparer.Ordinal)
    {
        ["Components/Details/DetailHeroContent.razor.css"] = (26, "The details hero: its title and facts scale with the screen (D8 hero)."),
        ["Components/Details/DetailHeroSkeleton.razor.css"] = (1, "The loading stand-in for the details hero title scales with it."),
        ["Components/Details/SequencePlacementPanel.razor.css"] = (3, "The series hero title and position number scale with the screen (D8 hero)."),
        ["Components/Details/GeneratedIdentity.razor.css"] = (1, "The letter drawn on a generated cover scales with the cover; it is artwork, not text."),
        ["Components/Cinematic/"] = (4, "The hero carousel: its title scales with the screen (D8 hero)."),
        ["Components/Player/"] = (19, "The players set their own sizes over video and on TV screens; outside this walk (D12)."),
        ["Components/Editor/"] = (4, "The editor window's screens belong to the Universal editor v6 build (D12)."),
        ["Components/MediaTiles/MediaTile.razor.css"] = (3, "A stand-in number and two icons that scale with the tile; they are artwork stand-ins."),
        ["Components/Shared/MediaViewerShell.razor.css"] = (2, "Subtitle cues scale with the video."),
        ["Components/Shared/MediaArtworkGroupPreview.razor.css"] = (1, "A fallback icon that scales with its picture."),
        ["Components/Shared/AppRangeSlider.razor.css"] = (1, "A size of zero hides a label that screen readers still read."),
        ["Components/View/ViewMap.razor.css"] = (1, "Map labels scale with the map."),
        ["Components/View/ViewPlaceStoryPanel.razor.css"] = (1, "An icon size."),
        ["Components/Pages/ViewPlacesPage.razor.css"] = (1, "An icon size on the map toolbar."),
        ["Components/Navigation/SystemActivityIndicator.razor.css"] = (1, "The activity glyph's size, set by a variable."),
        ["wwwroot/app.css"] = (11, "Four large icon helper classes, the Settings card sizes set by a variable, the reading sample, the TV base size, the phone hero title and the editor window's lines."),
    };

    private static readonly Regex OffScaleFontSize = new(@"(?<![\w-])font-size:\s*(?!var\(--tl-|inherit)[^;}\s][^;}]*", RegexOptions.Compiled);

    [Fact]
    public void NoTextSizeIsWrittenByHandOutsideTheWrittenList()
    {
        // Look pass v3.1 D8 and D11: a style names a job or a step (a --tl-* token) or inherits, never a number, a fraction of the
        // base size or a fluid size. Checked over the component styles and the shared sheets; the readers' stylesheets under
        // wwwroot/css/ are left out (book text is theirs). Anything else is zero unless the list above names it.
        var counts = WebFiles(".css")
            .Where(f => f.Relative.StartsWith("Components/", StringComparison.Ordinal)
                || (f.Relative.StartsWith("wwwroot/", StringComparison.Ordinal) && f.Relative.IndexOf('/', "wwwroot/".Length) < 0))
            .Select(f => (f.Relative, Count: OffScaleFontSize.Matches(f.Text).Count))
            .Where(f => f.Count > 0)
            .ToList();
        var offenders = counts
            .Where(f => !OffScaleFontSizeAllowlist.Keys.Any(key => f.Relative.StartsWith(key, StringComparison.Ordinal)))
            .Select(f => $"{f.Relative} ({f.Count})")
            .ToList();
        Assert.True(offenders.Count == 0, "Text sizes written by hand; name a type job (--tl-type-<job>-size) instead of a number, rem, em or clamp(): " + string.Join("; ", offenders));
        Assert.All(OffScaleFontSizeAllowlist, pair =>
        {
            Assert.False(string.IsNullOrWhiteSpace(pair.Value.Reason));
            var total = counts.Where(f => f.Relative.StartsWith(pair.Key, StringComparison.Ordinal)).Sum(f => f.Count);
            Assert.True(total <= pair.Value.Max, $"{pair.Key} has {total} hand-written text sizes, the list allows {pair.Value.Max} ({pair.Value.Reason}).");
            Assert.True(total > 0, $"{pair.Key} no longer writes a text size by hand; remove it from the list.");
        });
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
