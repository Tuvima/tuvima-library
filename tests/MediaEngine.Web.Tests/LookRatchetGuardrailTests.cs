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
    private const int RoundTokenCeiling = 47;
    private const int NativeControlCeiling = 374;
    private const int AdHocFontSizeCeiling = 150;
    private const int HandTypedHexCeiling = 902;
    private const int ForcedOverrideCeiling = 1501;
    private const int HandTypedLayerCeiling = 307;

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
        Assert.Contains("bottom: 8px", rule);
        Assert.Contains("right: 8px", rule);
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
