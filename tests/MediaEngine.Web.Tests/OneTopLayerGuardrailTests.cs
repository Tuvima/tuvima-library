using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Look v3.1 D6: everything that hovers (tooltips, info tips, hover cards) goes through the one tooltip layer, which the browser draws
/// in its top layer above any open window. Nothing that hovers is layered by number, and a choice tile that shows its description has
/// no hover text repeating it.
/// </summary>
public sealed class OneTopLayerGuardrailTests
{
    // A hover surface a component draws for itself, by the names such parts go by.
    private static readonly Regex HoverSurfaceSelector = new(@"(__tip\b|__tooltip\b|__hover-card\b|__peek\b|__hint-card\b|\.hover-card\b)", RegexOptions.Compiled);

    // Real exceptions, each with its reason.
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        // The readout of the bar under the pointer in the Places timeline chart. It is part of the chart, sits inside the map's own
        // panel and never over a window (charts and the map are D7 exceptions too).
        ["Components/View/ViewPlacesTimeline.razor.css"] = "chart readout inside the map panel",
    };

    [Fact]
    public void NoHoverSurfaceIsLayeredByNumber()
    {
        var web = Path.Combine(RepoRoot(), "src", "MediaEngine.Web");
        var offenders = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(web, "Components"), "*.razor.css", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(web, path).Replace('\\', '/');
            if (Allowed.ContainsKey(relative))
            {
                continue;
            }

            foreach (Match rule in Regex.Matches(File.ReadAllText(path), @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}"))
            {
                if (HoverSurfaceSelector.IsMatch(rule.Groups["selector"].Value) && Regex.IsMatch(rule.Groups["body"].Value, @"z-index\s*:"))
                {
                    offenders.Add($"{relative}: {rule.Groups["selector"].Value.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A hover surface is layered by number, so an open window can cover it (Look v3.1 D6). Use AppTooltip or AppInfoTip, which draw in the top layer:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void NothingShownOnlyOnHoverIsLayeredByNumber()
    {
        // The same rule for a hover surface that goes by another name: a part a style reveals under :hover or :focus-within, placed
        // outside the thing it belongs to and given a layer number, is a hover card whatever its class is called. A caption or a
        // button shown over its own tile stays inside the tile and is not one.
        var web = Path.Combine(RepoRoot(), "src", "MediaEngine.Web");
        var offenders = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(web, "Components"), "*.razor.css", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(web, path).Replace('\\', '/');
            if (Allowed.ContainsKey(relative))
            {
                continue;
            }

            var rules = Regex.Matches(File.ReadAllText(path), @"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}");
            var revealed = rules
                .Where(rule => Regex.IsMatch(rule.Groups["body"].Value, @"display\s*:\s*(?!none)|visibility\s*:\s*visible|opacity\s*:\s*1"))
                .SelectMany(rule => Regex.Matches(rule.Groups["selector"].Value, @":(?:hover|focus-within)[^,]*\s\.(?<part>[\w-]+)\s*(?:,|$)"))
                .Select(match => match.Groups["part"].Value)
                .ToHashSet(StringComparer.Ordinal);
            foreach (Match rule in rules)
            {
                var body = rule.Groups["body"].Value;
                var part = Regex.Match(rule.Groups["selector"].Value.Trim(), @"^\.(?<part>[\w-]+)$").Groups["part"].Value;
                if (revealed.Contains(part) && Regex.IsMatch(body, @"position\s*:\s*absolute") && Regex.IsMatch(body, @"z-index\s*:")
                    && Regex.IsMatch(body, @"(?:top|bottom|left|right)\s*:\s*(?:calc\(\s*)?100%"))
                {
                    offenders.Add($"{relative}: .{part}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A part shown only on hover is layered by number, so an open window can cover it (Look v3.1 D6). Use AppTooltip, which draws in the top layer:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void EveryAllowedHoverSurfaceStillExists()
    {
        var web = Path.Combine(RepoRoot(), "src", "MediaEngine.Web");
        Assert.All(Allowed.Keys, relative => Assert.True(File.Exists(Path.Combine(web, relative.Replace('/', Path.DirectorySeparatorChar))), $"{relative} is allowed but no longer exists; remove it from the list."));
    }

    [Fact]
    public void TheInfoTipUsesTheSharedTooltip()
    {
        var source = Read("Components/Shared/AppInfoTip.razor");
        Assert.Contains("<AppTooltip Text=\"@Text\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("z-index", Read("Components/Shared/AppInfoTip.razor.css"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTooltipIsDrawnInTheBrowsersTopLayerInsideAnOpenWindow()
    {
        var script = Read("wwwroot/js/playback-tooltip.js");
        Assert.Contains("tooltip.setAttribute('popover', 'manual');", script, StringComparison.Ordinal);
        Assert.Contains("dialog.matches(':modal')", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Components/Shared/AppChoiceTile.razor")]
    [InlineData("Components/Shared/AppChoiceCard.razor")]
    public void AChoiceTileThatShowsItsDescriptionHasNoHoverTextOfItsOwn(string file)
    {
        var source = Read(file);
        Assert.Contains("@Description", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AppTooltip", source, StringComparison.Ordinal);
        Assert.DoesNotContain("data-playback-tooltip", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\stitle=", source);
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "MediaEngine.Web", relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
