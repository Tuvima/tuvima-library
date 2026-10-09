using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

public sealed partial class UiConsistencyGuardrailTests
{
    private static readonly string RepoRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void PlaybackSongActionRows_UseOneOwnerForIconTypeAndGeometry()
    {
        foreach (var host in new[] { "ListenNowPlayingBar", "PlaybackFullPlayer", "PlaybackDesktopScene" })
        {
            var source = Read($"src/MediaEngine.Web/Components/Listen/{host}.razor");
            Assert.Contains("<PlaybackSongActions", source, StringComparison.Ordinal);
            Assert.DoesNotContain("<SongActions", source, StringComparison.Ordinal);
        }
        var style = Read("src/MediaEngine.Web/Components/Listen/PlaybackSongActions.razor.css");
        Assert.Contains(".playback-song-actions ::deep .playback-song-action", style, StringComparison.Ordinal);
        Assert.Contains("width:44px; height:44px;", style, StringComparison.Ordinal);
        Assert.Contains("border-radius:50%", style, StringComparison.Ordinal);
        Assert.DoesNotContain("!important", style, StringComparison.Ordinal);
        Assert.DoesNotContain("playback-lyrics__sync", Read("src/MediaEngine.Web/Components/Listen/PlaybackLyrics.razor"), StringComparison.Ordinal);
    }

    [Fact]
    public void AppButtons_UseOnlyTheSharedSemanticApi()
    {
        var violations = RazorFiles()
            .SelectMany(path => AppButtonTag().Matches(File.ReadAllText(path)).Select(match => (path, match.Value)))
            .Where(item => LegacyButtonAttribute().IsMatch(item.Value))
            .Select(item => Relative(item.path))
            .Distinct()
            .ToArray();

        Assert.True(violations.Length == 0,
            $"AppButton and AppIconButton must use ButtonStyle, Tone, and AppControlSize. Legacy Variant/Color attributes remain in: {string.Join(", ", violations)}");

        var appButton = Read("src/MediaEngine.Web/Components/Shared/AppButton.razor");
        Assert.DoesNotContain("[Parameter] public AppColor? Color", appButton, StringComparison.Ordinal);
        Assert.DoesNotContain("[Parameter] public AppVariant? Variant", appButton, StringComparison.Ordinal);
        Assert.Contains("[Parameter] public bool Loading", appButton, StringComparison.Ordinal);
    }

    [Fact]
    public void RawMudButtons_RemainInsideSharedControlComponents()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/MediaEngine.Web/Components/Shared/AppButton.razor",
            "src/MediaEngine.Web/Components/Shared/AppDialogShell.razor",
            "src/MediaEngine.Web/Components/Shared/AppIconButton.razor",
        };

        var violations = RazorFiles()
            .Where(path => RawMudButton().IsMatch(File.ReadAllText(path)))
            .Select(Relative)
            .Where(path => !allowed.Contains(path))
            .ToArray();

        Assert.True(violations.Length == 0,
            $"Raw MudButton/MudIconButton usage bypasses the shared visual contract in: {string.Join(", ", violations)}");
    }

    [Fact]
    public void UserFacingType_DoesNotFallBelowTwelvePixels()
    {
        var roots = new[]
        {
            Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components"),
            Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Shared"),
            Path.Combine(RepoRoot, "src", "MediaEngine.Web", "wwwroot"),
        };

        var violations = roots
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(path => NumericFontSize().Matches(File.ReadAllText(path)).Any(match => ToPixels(match) < 12d))
            .Select(Relative)
            .ToArray();

        Assert.True(violations.Length == 0,
            $"Readable UI text has a 12px minimum. Sub-floor declarations remain in: {string.Join(", ", violations)}");
    }

    [Fact]
    public void PrimarySettingsActions_KeepTheCanonicalFilledTreatment()
    {
        var expectations = new Dictionary<string, string>
        {
            ["src/MediaEngine.Web/Components/Settings/IngestionScanAction.razor"] = "Label=\"Scan now\"",
            ["src/MediaEngine.Web/Components/Settings/LocalNetworkSettingsPanel.razor"] = "Label=\"Save\" ButtonStyle=\"AppButtonStyle.Filled\" Tone=\"AppUiTone.Primary\"",
            ["src/MediaEngine.Web/Components/Settings/NetworkStreamingSettingsPanel.razor"] = "Label=\"Save streaming settings\" ButtonStyle=\"AppButtonStyle.Filled\" Tone=\"AppUiTone.Primary\"",
            ["src/MediaEngine.Web/Components/Settings/RemoteAccessSettingsPanel.razor"] = "Label=\"Save and verify\" ButtonStyle=\"AppButtonStyle.Filled\" Tone=\"AppUiTone.Primary\"",
        };

        foreach (var (path, expected) in expectations)
        {
            Assert.Contains(expected, Read(path), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SharedActionControls_UseTheSemanticTypographyContract()
    {
        var tokens = Read("src/MediaEngine.Web/wwwroot/tuvima.tokens.css");
        Assert.Contains("--tl-control-font-size-sm: 0.8125rem;", tokens, StringComparison.Ordinal);
        Assert.Contains("--tl-control-font-size-md: 0.875rem;", tokens, StringComparison.Ordinal);
        Assert.Contains("--tl-control-font-size-lg: 0.9375rem;", tokens, StringComparison.Ordinal);
        Assert.Contains("--tl-control-font-weight: var(--tl-font-weight-semibold);", tokens, StringComparison.Ordinal);
        Assert.Contains("--tl-control-line-height: 1.25;", tokens, StringComparison.Ordinal);
        Assert.Contains("--tl-control-letter-spacing: 0;", tokens, StringComparison.Ordinal);

        var css = Read("src/MediaEngine.Web/wwwroot/app.css");
        var buttonRule = CssRule(Read("src/MediaEngine.Web/wwwroot/native-fields.css"), ".tl-button {");
        Assert.Contains("font-family: var(--font-ui)", buttonRule, StringComparison.Ordinal);
        Assert.Contains("font-weight: var(--tl-control-font-weight)", buttonRule, StringComparison.Ordinal);
        Assert.Contains("line-height: var(--tl-control-line-height)", buttonRule, StringComparison.Ordinal);
        Assert.Contains("letter-spacing: var(--tl-control-letter-spacing)", buttonRule, StringComparison.Ordinal);
        Assert.DoesNotContain("font-weight: 700", buttonRule, StringComparison.Ordinal);

        Assert.Contains(".app-button.app-control--compact", css, StringComparison.Ordinal);
        Assert.Contains("font-size: var(--tl-control-font-size-sm)", css, StringComparison.Ordinal);
        Assert.Contains(".app-button.app-control--normal", css, StringComparison.Ordinal);
        Assert.Contains("font-size: var(--tl-control-font-size-md)", css, StringComparison.Ordinal);
        Assert.Contains(".app-button.app-control--large", css, StringComparison.Ordinal);
        Assert.Contains("font-size: var(--tl-control-font-size-lg)", css, StringComparison.Ordinal);

        var segmentedRule = CssRule(css, ".app-segmented-control__item");
        Assert.Contains("font-family: var(--font-ui)", segmentedRule, StringComparison.Ordinal);
        Assert.Contains("font-size: var(--tl-control-font-size-md)", segmentedRule, StringComparison.Ordinal);
        Assert.Contains("font-weight: var(--tl-control-font-weight)", segmentedRule, StringComparison.Ordinal);
        Assert.Contains("line-height: var(--tl-control-line-height)", segmentedRule, StringComparison.Ordinal);
        Assert.Contains("letter-spacing: var(--tl-control-letter-spacing)", segmentedRule, StringComparison.Ordinal);
        Assert.DoesNotContain("font-weight: 700", segmentedRule, StringComparison.Ordinal);

        var tabRule = CssRule(css, "\n.tl-tab {");
        Assert.Contains("font-weight: var(--tl-control-font-weight)", tabRule, StringComparison.Ordinal);
        Assert.DoesNotContain("font-weight: 700", tabRule, StringComparison.Ordinal);

        var chipRule = CssRule(css, "\n.app-chip {");
        Assert.Contains("font-weight: var(--tl-control-font-weight)", chipRule, StringComparison.Ordinal);
        Assert.DoesNotContain("font-weight: 700", chipRule, StringComparison.Ordinal);

        var scopedControlRules = new Dictionary<string, string>
        {
            ["src/MediaEngine.Web/Components/Browse/BrowseShellStyles.razor.css"] = ".browse-shell__tabs-frame .tl-tab {",
            ["src/MediaEngine.Web/Components/Browse/MediaBrowseShell.razor.css"] = ".browse-shell__tabs-frame ::deep .tl-tab {",
            ["src/MediaEngine.Web/Components/Pages/ViewPage.razor.css"] = "::deep .view-kind-tab {",
            ["src/MediaEngine.Web/Components/Shared/AppSortableHeader.razor.css"] = ".app-sortable-header {",
        };

        foreach (var (path, selector) in scopedControlRules)
        {
            var rule = CssRule(Read(path), selector);
            Assert.Contains("font-weight: var(--tl-control-font-weight)", rule, StringComparison.Ordinal);
            Assert.DoesNotContain("font-weight: 700", rule, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> RazorFiles() =>
        Directory.EnumerateFiles(
            Path.Combine(RepoRoot, "src", "MediaEngine.Web"),
            "*.razor",
            SearchOption.AllDirectories)
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static double ToPixels(Match match)
    {
        var value = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
        return match.Groups["unit"].Value.Equals("rem", StringComparison.OrdinalIgnoreCase)
            ? value * 16d
            : value;
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    private static string Relative(string path) =>
        Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');

    private static string CssRule(string css, string selector)
    {
        var selectorStart = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(selectorStart >= 0, $"Missing CSS selector: {selector}");
        var declarationStart = css.IndexOf('{', selectorStart);
        var declarationEnd = css.IndexOf('}', declarationStart);
        Assert.True(declarationStart >= 0 && declarationEnd > declarationStart, $"Malformed CSS rule: {selector}");
        return css[declarationStart..(declarationEnd + 1)];
    }

    [GeneratedRegex("<App(?:Icon)?Button\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex AppButtonTag();

    [GeneratedRegex("\\s(?:Variant|Color)\\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyButtonAttribute();

    [GeneratedRegex("<Mud(?:Icon)?Button\\b", RegexOptions.IgnoreCase)]
    private static partial Regex RawMudButton();

    [GeneratedRegex("font-size\\s*:\\s*(?<value>(?:\\d+(?:\\.\\d+)?|\\.\\d+))(?<unit>px|rem)", RegexOptions.IgnoreCase)]
    private static partial Regex NumericFontSize();
}
