using System.Text.RegularExpressions;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Look v3 WP8: <c>engineering/design/design-system.md</c> is written from <c>tuvima.tokens.css</c>. Every table row that names a token and a value
/// (colours, type, icons, rows, windows, spacing, radii, buttons, tooltips, breakpoints) must match the token file exactly, and every documented token must exist.
/// </summary>
public sealed class DesignSystemDocDriftTests
{
    private static readonly Regex DocRow = new(@"^\|[^|]*\|\s*`(?<token>--[\w-]+)`\s*\|\s*`(?<value>[^`]+)`\s*\|", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Declaration = new(@"(?<token>--[\w-]+)\s*:\s*(?<value>[^;{}]+?);", RegexOptions.Compiled);

    private static string Root { get; } = FindRepoRoot();

    private static string Normalise(string value) => Regex.Replace(value.Trim(), @"\s+", " ");

    private static Dictionary<string, string> TokenFile()
    {
        var css = File.ReadAllText(Path.Combine(Root, "src", "MediaEngine.Web", "wwwroot", "tuvima.tokens.css"));
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Declaration.Matches(css))
        {
            tokens.TryAdd(match.Groups["token"].Value, Normalise(match.Groups["value"].Value));
        }

        return tokens;
    }

    private static string Doc() => File.ReadAllText(Path.Combine(Root, "engineering", "design", "design-system.md"));

    private static List<(string Token, string Value)> DocumentedValues() =>
        DocRow.Matches(Doc()).Select(m => (m.Groups["token"].Value, Normalise(m.Groups["value"].Value))).ToList();

    [Fact]
    public void TheDocumentListsColourTypeIconRowAndWindowTables()
    {
        var documented = DocumentedValues().Select(row => row.Token).ToHashSet(StringComparer.Ordinal);

        foreach (var required in new[]
        {
            "--tl-bg-root", "--tl-bg-surface-elevated", "--tl-accent-primary", "--tl-menu-item-selected", "--tl-status-danger", "--tl-area-vault",
            "--tl-font-size-body", "--font-ui", "--tl-icon-size-md", "--tl-row-h-default", "--tl-window-m", "--tl-popout-panel",
        })
        {
            Assert.Contains(required, documented);
        }
    }

    [Fact]
    public void TheTypeLadderTokensAreWrittenInPixelsAtExactlyTheD7Steps()
    {
        var tokens = TokenFile();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["--tl-font-size-xs"] = "12px",
            ["--tl-font-size-secondary"] = "13px",
            ["--tl-font-size-sm"] = "14px",
            ["--tl-font-size-md"] = "16px",
            ["--tl-font-size-dialog-title"] = "20px",
            ["--tl-font-size-shelf-title"] = "20px",
            ["--tl-font-size-title"] = "24px",
            ["--tl-font-size-xl"] = "24px",
            ["--tl-font-size-hero"] = "32px",
            ["--tl-control-font-size-sm"] = "13px",
            ["--tl-control-font-size-md"] = "14px",
            ["--tl-tooltip-font-size"] = "13px",
            ["--tl-dialog-title-size"] = "var(--tl-font-size-dialog-title)",
            ["--tl-font-size-caption"] = "var(--tl-font-size-xs)",
            ["--tl-font-size-body"] = "var(--tl-font-size-sm)",
            ["--tl-control-font-weight"] = "var(--tl-font-weight-medium)",
        };

        var problems = expected
            .Where(pair => !tokens.TryGetValue(pair.Key, out var actual) || !string.Equals(actual, pair.Value, StringComparison.Ordinal))
            .Select(pair => $"{pair.Key}: expected '{pair.Value}', token file says '{(tokens.TryGetValue(pair.Key, out var v) ? v : "(missing)")}'")
            .ToList();

        // Nothing named a font size may be written in rem or sit off the D7 ladder (--tl-font-size-lg is an alias of the 20 px section step).
        var ladder = new HashSet<string>(StringComparer.Ordinal) { "12px", "13px", "14px", "16px", "20px", "24px", "32px" };
        foreach (var (token, value) in tokens.Where(pair => pair.Key.Contains("font-size", StringComparison.Ordinal)))
        {
            if (value.StartsWith("var(", StringComparison.Ordinal))
            {
                continue;
            }

            if (!ladder.Contains(value))
            {
                problems.Add($"{token}: '{value}' is not a D7 ladder step in px");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EveryDocumentedTokenExistsInTheTokenFileWithTheSameValue()
    {
        var tokens = TokenFile();
        var problems = new List<string>();

        foreach (var (token, value) in DocumentedValues())
        {
            if (!tokens.TryGetValue(token, out var actual))
            {
                problems.Add($"{token} is documented but not defined in tuvima.tokens.css");
            }
            else if (!string.Equals(actual, value, StringComparison.Ordinal))
            {
                problems.Add($"{token}: doc says '{value}', token file says '{actual}'");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void EveryDocumentedColourTokenIsShownOnThePreviewPage()
    {
        var preview = File.ReadAllText(Path.Combine(Root, "src", "MediaEngine.Web", "Components", "Pages", "DesignSystemPreview.razor"));
        var missing = DocumentedValues()
            .Where(row => row.Token.StartsWith("--tl-bg-", StringComparison.Ordinal)
                || row.Token.StartsWith("--tl-text-", StringComparison.Ordinal)
                || row.Token.StartsWith("--tl-border-", StringComparison.Ordinal)
                || row.Token.StartsWith("--tl-accent-", StringComparison.Ordinal)
                || row.Token.StartsWith("--tl-menu-", StringComparison.Ordinal)
                || row.Token.StartsWith("--tl-status-", StringComparison.Ordinal)
                || row.Token.StartsWith("--tl-area-", StringComparison.Ordinal)
                || row.Token is "--tl-divider" or "--tl-focus-ring")
            .Select(row => row.Token)
            .Where(token => !preview.Contains($"\"{token}\"", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0, "Colour tokens in the doc that the proof page does not show: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheIconTableNamesMatchTheIconSizeSteps()
    {
        var rows = DocumentedValues().ToDictionary(row => row.Token, row => row.Value, StringComparer.Ordinal);
        var doc = Doc();

        foreach (var step in Enum.GetValues<AppIconSize>())
        {
            var token = AppIconSizes.Token(step).Replace("var(", string.Empty, StringComparison.Ordinal).TrimEnd(')');
            Assert.True(rows.ContainsKey(token), $"{step} maps to {token}, which the doc does not list.");
            Assert.Matches(new Regex($@"^\|\s*{step}\s*\|\s*`{Regex.Escape(token)}`", RegexOptions.Multiline), doc);
        }
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
