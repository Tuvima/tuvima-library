using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Keeps every chip, pill, badge and tag square-cornered (Look v3 "Label and chip" corner, <c>--tl-radius-pill</c>, 3px).
/// The round corner is for avatars, switches, dots, progress and seek bars and the player dock only; a chip that reaches for
/// a bigger corner (or the round token) fails here instead of being found later by eye.
/// </summary>
public sealed partial class PillShapeGuardrailTests
{
    // A selector that names a chip, pill, badge or tag as its last part.
    [GeneratedRegex(@"(?<![a-z])(chip|pill|badge|tag)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex ChipName();

    [GeneratedRegex(@"([^{}]+)\{([^{}]*)\}")]
    private static partial Regex Rule();

    [GeneratedRegex(@"border-radius:\s*([^;}]+)")]
    private static partial Regex Radius();

    // Reviewed exceptions: container or control rules whose name merely contains one of the words.
    private static readonly string[] Exceptions =
    [
        ".tl-editor-field__chips",
        ".app-tag-input",
        ".view-smart-search__chip .view-smart-search__remove",
        ".collection-media-chip__dot",
        ".tl-detail-hero-genre::before",
        ".sme-tag-editor__surface",
        ".sme-tag-editor__confirmation",
        ".tl-popover.sme-tag-editor__suggestions-surface",
    ];

    [Fact]
    public void ChipsPillsBadgesAndTagsUseTheSquarePillCorner()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var files = Directory.EnumerateFiles(web, "*.css", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}vendor{Path.DirectorySeparatorChar}"));

        var failures = new List<string>();
        foreach (var file in files)
        {
            var css = File.ReadAllText(file);
            foreach (Match rule in Rule().Matches(css))
            {
                var selector = rule.Groups[1].Value.Split("*/").Last().Trim();
                if (!ChipName().IsMatch(selector) || Exceptions.Any(selector.Contains))
                {
                    continue;
                }

                var radius = Radius().Match(rule.Groups[2].Value);
                if (!radius.Success)
                {
                    continue;
                }

                var value = radius.Groups[1].Value.Replace("!important", string.Empty).Trim();
                if (value is "var(--tl-radius-pill)" or "3px" or "0" or "50%" or "inherit" || value.StartsWith("var(--tl-radius-pill)", StringComparison.Ordinal))
                {
                    continue;
                }

                failures.Add($"{Path.GetRelativePath(web, file)}: '{selector.Replace('\n', ' ')}' uses border-radius {value}");
            }
        }

        Assert.True(failures.Count == 0, "Chips, pills, badges and tags must use var(--tl-radius-pill):" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void ThePillCornerTokenStaysSquare()
    {
        var tokens = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "wwwroot", "tuvima.tokens.css"));
        Assert.Contains("--tl-radius-pill: 3px;", tokens, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }
}
