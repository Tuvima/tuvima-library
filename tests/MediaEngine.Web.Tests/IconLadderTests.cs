using System.Text.RegularExpressions;
using Bunit;
using MediaEngine.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Look v3 WP10 (D13): one icon size ladder (16, 20, 24, 32, 48, 64) and two placements. The icon parts take only the ladder's names, the choice tile
/// puts a 48px icon above a 14px label, an empty state draws a 64px icon with no tinted square, and no component style types an icon size by hand.
/// </summary>
public sealed class IconLadderTests : AsyncBunitContext
{
    public IconLadderTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static string Web(params string[] relative) => Path.Combine([FindRepoRoot(), "src", "MediaEngine.Web", .. relative]);

    private static string Tokens() => File.ReadAllText(Web("wwwroot", "tuvima.tokens.css"));

    private static int TokenPixels(string name)
    {
        var match = Regex.Match(Tokens(), Regex.Escape(name) + @":\s*(\d+)px;");
        Assert.True(match.Success, $"{name} is not a pixel token");
        return int.Parse(match.Groups[1].Value);
    }

    [Fact]
    public void TheLadderTokensAre16_20_24_32_48And64()
    {
        Assert.Equal([16, 20, 24, 32, 48, 64], new[] { "xs", "sm", "md", "lg", "xl", "2xl" }.Select(step => TokenPixels($"--tl-icon-size-{step}")).ToArray());
    }

    [Fact]
    public void AChoiceTileShowsA48PixelIconAboveA14PixelLabel()
    {
        var cut = Render<AppChoiceTile>(parameters => parameters
            .Add(component => component.Icon, AppMaterialIcons.Outlined.Movie)
            .Add(component => component.Label, "Watch")
            .Add(component => component.Description, "Movies, TV, videos")
            .Add(component => component.Area, "watch"));

        var tile = cut.Find("button.app-choice-tile");
        Assert.Equal("app-choice-tile__square", tile.Children[0].ClassName);
        Assert.Equal("app-choice-tile__text", tile.Children[1].ClassName);
        var icon = tile.Children[0].QuerySelector("svg")!;
        Assert.Contains("tl-icon-size-choice48", icon.ClassName, StringComparison.Ordinal);
        Assert.Equal(48, TokenPixels("--tl-icon-size-xl"));
        Assert.Contains("--app-choice-tile-tone:var(--tl-area-watch)", tile.GetAttribute("style"), StringComparison.Ordinal);
        Assert.Equal("Watch", tile.QuerySelector(".app-choice-tile__label")!.TextContent);
        Assert.Equal("Movies, TV, videos", tile.QuerySelector(".app-choice-tile__description")!.TextContent);

        // Under the icon, centred, with the 14px / 500 name and a 14% tint (tokens only).
        var css = File.ReadAllText(Web("Components", "Shared", "AppChoiceTile.razor.css"));
        Assert.Matches(@"\.app-choice-tile \{[^}]*flex-direction: column;[^}]*align-items: center;", css);
        Assert.Matches(@"\.app-choice-tile__label \{[^}]*font-size: var\(--tl-type-control-size\);[^}]*font-weight: var\(--tl-type-control-weight\);", css);
        Assert.Matches(@"--tl-font-size-sm:\s*14px;", Tokens());
        Assert.Contains("var(--app-choice-tile-tone, var(--tl-text-secondary)) 14%", css, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"#[0-9a-fA-F]{3,8}\b|rgba?\(", css);
    }

    [Fact]
    public void AChoiceTilePressesLikeAChoiceAndIgnoresPressesWhenOff()
    {
        var pressed = 0;
        var cut = Render<AppChoiceTile>(parameters => parameters
            .Add(component => component.Icon, AppMaterialIcons.Outlined.Movie)
            .Add(component => component.Label, "Watch")
            .Add(component => component.Selected, true)
            .Add(component => component.OnClick, () => pressed++));
        var tile = cut.Find("button");
        Assert.Equal("true", tile.GetAttribute("aria-pressed"));
        Assert.Contains("is-selected", tile.ClassName, StringComparison.Ordinal);
        tile.Click();
        Assert.Equal(1, pressed);

        cut.Render(parameters => parameters.Add(component => component.Disabled, true));
        var off = cut.Find("button");
        Assert.Equal("true", off.GetAttribute("aria-disabled"));
        off.Click();
        Assert.Equal(1, pressed);
    }

    [Fact]
    public void AnEmptyStateShowsA64PixelIconWithoutATintedSquare()
    {
        var cut = Render<AppEmptyState>(parameters => parameters
            .Add(component => component.Title, "No providers")
            .Add(component => component.Icon, AppMaterialIcons.Outlined.Search));

        var icon = cut.Find(".app-page-state svg");
        Assert.Contains("tl-icon-size-hero64", icon.ClassName, StringComparison.Ordinal);
        Assert.Equal(64, TokenPixels("--tl-icon-size-2xl"));
        Assert.Empty(cut.FindAll(".app-choice-tile__square, .app-icon-tile"));

        // The choice tile's empty form is the same: a 64px icon and no square.
        var tile = Render<AppChoiceTile>(parameters => parameters
            .Add(component => component.Empty, true)
            .Add(component => component.Icon, AppMaterialIcons.Outlined.Search)
            .Add(component => component.Label, "Nothing yet"));
        Assert.Contains("tl-icon-size-hero64", tile.Find("svg").ClassName, StringComparison.Ordinal);
        Assert.Empty(tile.FindAll(".app-choice-tile__square"));
        Assert.Empty(tile.FindAll("button"));
    }

    [Fact]
    public void TheIconPartsTakeOnlyTheLadderNames()
    {
        Assert.Equal(["Inline16", "Control20", "Row24", "Tile32", "Choice48", "Hero64"], Enum.GetNames<AppIconSize>());
        foreach (var (type, property) in new[] { (typeof(AppMaterialIcon), "Size"), (typeof(AppIcon), "Size"), (typeof(AppIconTile), "Size") })
        {
            Assert.Equal(typeof(AppIconSize), type.GetProperty(property)!.PropertyType);
        }

        Assert.Equal(AppIconSize.Control20, AppIconSizes.ForControl(AppControlSize.Compact));
        Assert.Equal(AppIconSize.Row24, AppIconSizes.ForControl(AppControlSize.Normal));
        Assert.Equal(AppIconSize.Row24, AppIconSizes.ForControl(AppControlSize.Large));
    }

    [Fact]
    public void AnIconButtonDrawsItsIconAt20In36PixelControlsAnd24InTheRest()
    {
        var compact = Render<AppIconButton>(parameters => parameters.Add(component => component.Icon, AppMaterialIcons.Outlined.Edit).Add(component => component.AriaLabel, "Edit").Add(component => component.Size, AppControlSize.Compact));
        var normal = Render<AppIconButton>(parameters => parameters.Add(component => component.Icon, AppMaterialIcons.Outlined.Edit).Add(component => component.AriaLabel, "Edit"));
        Assert.Contains("tl-icon-size-control20", compact.Find("svg").ClassName, StringComparison.Ordinal);
        Assert.Contains("tl-icon-size-row24", normal.Find("svg").ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void ABeginningIconInAChipIs16Pixels()
    {
        var cut = Render<AppChip>(parameters => parameters.Add(component => component.Text, "Tag").Add(component => component.Icon, AppMaterialIcons.Outlined.Circle));
        Assert.Contains("tl-icon-size-inline16", cut.Find("svg").ClassName, StringComparison.Ordinal);
    }

    // Component styles size an icon only through the ladder tokens. Rules that size a glyph (svg, .tl-icon-root, .app-icon, material icons) and font sizes on
    // a named icon (*__icon) may not hold a pixel, rem or em literal. Exempt by selector: the status dot's 8 and 13px marks (smaller than the ladder's
    // first step, a dot not an icon) and the icon tile and choice tile, which derive their boxes from the ladder; width and height on a *__icon container
    // are the tile box around a glyph, not the glyph.
    [Fact]
    public void ComponentStylesDoNotTypeIconSizesByHand()
    {
        var offenders = new List<string>();
        var glyph = new Regex(@"(^svg|tl-icon-root|\.tl-icon\b|tl-svg-icon|\.app-icon\b|material-icon)", RegexOptions.IgnoreCase);
        var named = new Regex(@"(__[a-z-]*(icon|glyph)\b|-icon$|^\.[a-z-]*icon$)", RegexOptions.IgnoreCase);
        var buttonish = new Regex(@"(icon-button|button|btn|trigger|link|action|key)", RegexOptions.IgnoreCase);
        var exempt = new[] { "app-status-dot", "app-icon-tile", "app-choice-tile" };
        var literal = new Regex(@"\d+(\.\d+)?(px|rem|em)\b");
        var declaration = new Regex(@"(?<![-\w])(width|height|font-size|min-width|min-height)\s*:\s*([^;]+);");
        var root = Web();
        var files = Directory.EnumerateFiles(root, "*.razor.css", SearchOption.AllDirectories).Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Append(Web("wwwroot", "app.css"));
        foreach (var file in files)
        {
            var css = Regex.Replace(File.ReadAllText(file), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            foreach (Match rule in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
            {
                var selector = rule.Groups[1].Value;
                if (exempt.Any(selector.Contains))
                {
                    continue;
                }

                var kinds = SplitSelectors(selector).Select(part =>
                {
                    var last = LastCompound(part);
                    return glyph.IsMatch(last) ? "glyph" : named.IsMatch(last) && !buttonish.IsMatch(last) ? "named" : "other";
                }).ToArray();
                var properties = kinds.All(kind => kind == "glyph") ? new[] { "width", "height", "font-size", "min-width", "min-height" }
                    : kinds.All(kind => kind is "glyph" or "named") ? new[] { "font-size" }
                    : [];
                foreach (Match d in declaration.Matches(rule.Groups[2].Value))
                {
                    if (properties.Contains(d.Groups[1].Value) && !d.Groups[2].Value.Contains("var(", StringComparison.Ordinal) && literal.IsMatch(d.Groups[2].Value))
                    {
                        offenders.Add($"{Path.GetRelativePath(root, file)}: {selector.Trim()} {{ {d.Groups[1].Value}: {d.Groups[2].Value.Trim()} }}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0, "Icon sizes are typed by hand (use --tl-icon-size-* from the ladder):" + Environment.NewLine + string.Join(Environment.NewLine, offenders.Take(20)));
    }

    [Fact]
    public void NoComponentSpellsAnIconSizeAsAString()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Web("Components"), "*.razor", SearchOption.AllDirectories).Concat(Directory.EnumerateFiles(Web("Shared"), "*.razor", SearchOption.AllDirectories)))
        {
            var text = File.ReadAllText(file);
            foreach (Match tag in Regex.Matches(text, @"<(AppMaterialIcon|AppIcon|AppIconTile)\b[^>]*?\sSize=""([^""]*)""", RegexOptions.Singleline))
            {
                var value = tag.Groups[2].Value;
                if (!value.StartsWith("AppIconSize.", StringComparison.Ordinal) && !value.StartsWith('@') && value != "IconSize" && !value.StartsWith("AppIconSizes.", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {tag.Groups[1].Value} Size=\"{value}\"");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    private static IEnumerable<string> SplitSelectors(string selector)
    {
        var depth = 0;
        var current = new System.Text.StringBuilder();
        foreach (var ch in selector)
        {
            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
            }

            if (ch == ',' && depth == 0)
            {
                yield return current.ToString();
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        yield return current.ToString();
    }

    private static string LastCompound(string selector)
    {
        var stripped = selector.Trim();
        string previous;
        do
        {
            previous = stripped;
            stripped = Regex.Replace(stripped, @"\([^()]*\)", string.Empty);
        }
        while (stripped != previous);

        var tokens = Regex.Split(stripped, @"\s+|>|\+|~").Where(t => t.Length > 0 && t != "::deep" && t != ":deep").ToArray();
        return tokens.Length == 0 ? string.Empty : tokens[^1];
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
