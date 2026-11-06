using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Look pass v2 A11: the browser's own hint never reaches the person. A hint written as title="..." is taken over by the app's
/// tooltip on first hover or focus, and no new native hint may be added.
/// </summary>
public sealed class TooltipGuardrailTests
{
    // Ratchet: the count of hand-written title="..." in Razor may only go down (it is 186 when this guardrail was added).
    private const int MaxLiteralTitleAttributes = 186;

    [Fact]
    public void EveryNativeTitleHintIsTakenOverByTheAppTooltip()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var module = File.ReadAllText(Path.Combine(web, "wwwroot", "js", "app-title-tooltips.js"));
        Assert.Contains("playback-tooltip.js", module);
        Assert.Contains("removeAttribute('title')", module);
        var app = File.ReadAllText(Path.Combine(web, "Components", "App.razor"));
        Assert.Contains("js/app-title-tooltips.js", app);
    }

    [Fact]
    public void TheTooltipUsesTheSharedTooltipTokens()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var tokens = File.ReadAllText(Path.Combine(web, "wwwroot", "tuvima.tokens.css"));
        Assert.Contains("--tl-tooltip-surface:", tokens);
        Assert.Contains("--tl-tooltip-radius: 6px;", tokens);
        Assert.Contains("--tl-tooltip-font-size: 0.8125rem;", tokens);
        Assert.Contains("--tl-tooltip-max-width: 280px;", tokens);
        var script = File.ReadAllText(Path.Combine(web, "wwwroot", "js", "playback-tooltip.js"));
        Assert.Contains("HOVER_DELAY_MS = 150", script);
        Assert.Contains("TOUCH_PRESS_MS = 500", script);
    }

    [Fact]
    public void NoNewLiteralTitleAttributesAreAdded()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var count = Directory.EnumerateFiles(web, "*.razor", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Sum(p => Regex.Matches(File.ReadAllText(p), "[\\s<]title=\"").Count);
        Assert.True(count <= MaxLiteralTitleAttributes, $"title=\"...\" count {count} is above the ratchet {MaxLiteralTitleAttributes}; use AppTooltip or the Tooltip parameter.");
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
