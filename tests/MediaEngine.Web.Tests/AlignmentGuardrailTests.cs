using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Look pass v3 D15 (alignment rules): the shared row, field, toolbar and footer parts line things up with flex and gap, never with
/// nudges. Their styles may not use <c>text-align: center</c>, a non-zero <c>margin-top</c> or <c>vertical-align</c> outside the written
/// allowlist below, and the body-copy, dialog and settings styles may not add new centred text (a ratchet that can only go down).
/// Choice tiles and numbers are allowed to centre or right-align (D15.4).
/// </summary>
public sealed class AlignmentGuardrailTests
{
    // The shared row parts, as scoped style sheets. A dialog shell is covered only through its footer rules (its header is not a row).
    private static readonly string[] SharedPartStyles =
    [
        "Components/Shared/AppSettingRow.razor.css",
        "Components/Shared/AppSettingsGroup.razor.css",
        "Components/Shared/AppSwitchRow.razor.css",
        "Components/Shared/AppChoiceRow.razor.css",
        "Components/Shared/AppCollapsibleRow.razor.css",
        "Components/Shared/AppFilterRow.razor.css",
        "Components/Shared/AppSearchFilterRow.razor.css",
        "Components/Shared/AppListPageHeader.razor.css",
        "Components/Shared/AppDialogFrame.razor.css",
        "Components/Shared/AppDialogShell.razor.css",
        "Components/Details/EditorMatchPanel.razor.css",
    ];

    // The same parts' rules inside the global sheet: a rule counts when one of its selectors starts with one of these stems.
    private static readonly string[] SharedPartSelectorStems =
    [
        ".app-setting-row", ".app-settings-group", ".app-field", ".app-action-bar", ".app-filter-bar", ".app-switch-row",
        ".app-dialog-shell__footer", ".app-segmented-control", ".tl-matchrow",
    ];

    // Written allowlist: "file :: selector fragment :: property" with the reason it is not an alignment nudge. Keep it short; add nothing without a reason.
    private static readonly (string File, string Selector, string Property, string Reason)[] Allowlist =
    [
        ("Components/Shared/AppDialogFrame.razor.css", ".app-native-dialog--position-", "margin-top",
            "A dialog placed at the top of the screen keeps a 32 px gap to the screen edge; it is a window position, not row alignment."),
    ];

    // Today's count of rules that centre text in the body-copy, dialog and settings styles. It can only go down.
    private const int CentredTextCeiling = 11;

    private static readonly Regex Rule = new(@"([^{}]+)\{([^{}]*)\}", RegexOptions.Compiled);

    [Fact]
    public void SharedRowParts_DoNotCentreTextNudgeWithMarginTopOrUseVerticalAlign_OutsideTheAllowlist()
    {
        var web = WebRoot();
        var offenders = new List<string>();
        foreach (var relative in SharedPartStyles)
        {
            var onlyFooter = relative.EndsWith("AppDialogShell.razor.css", StringComparison.Ordinal);
            foreach (var (selector, body) in Rules(File.ReadAllText(Path.Combine(web, relative))))
            {
                if (onlyFooter && !selector.Contains("__footer", StringComparison.Ordinal))
                {
                    continue;
                }

                Collect(relative, selector, body, offenders);
            }
        }

        foreach (var (selector, body) in Rules(File.ReadAllText(Path.Combine(web, "wwwroot", "app.css"))))
        {
            if (SharedPartSelectorStems.Any(stem => selector.Split(',').Any(part => part.Trim().StartsWith(stem, StringComparison.Ordinal))))
            {
                Collect("wwwroot/app.css", selector, body, offenders);
            }
        }

        Assert.True(offenders.Count == 0,
            "Shared row, field, toolbar and footer parts line up with flex and gap (D15). Fix the layout, or add a written allowlist entry with a reason:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void TheAllowlist_EveryEntryStillMatchesARule_AndGivesAReason()
    {
        var web = WebRoot();
        foreach (var entry in Allowlist)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Reason), $"{entry.File} :: {entry.Selector} needs a reason.");
            var text = File.ReadAllText(Path.Combine(web, entry.File));
            Assert.True(Rules(text).Any(rule => rule.Selector.Contains(entry.Selector, StringComparison.Ordinal) && Declares(rule.Body, entry.Property)),
                $"The allowlist entry {entry.File} :: {entry.Selector} :: {entry.Property} no longer matches a rule. Delete it.");
        }
    }

    [Fact]
    public void CentredText_InBodyCopyDialogAndSettingsStyles_OnlyGoesDown()
    {
        var web = WebRoot();
        var count = 0;
        var files = Directory.EnumerateFiles(Path.Combine(web, "Components", "Settings"), "*.css", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(web, "Components", "Shared"), "*Dialog*.css"))
            .Append(Path.Combine(web, "Components", "Shared", "AppMessageBox.razor.css"));
        foreach (var file in files.Where(File.Exists))
        {
            count += Rules(File.ReadAllText(file)).Count(rule => CentresText(rule.Body));
        }

        // The global sheet holds dialog and settings rules too; count the rules whose selector names them.
        count += Rules(File.ReadAllText(Path.Combine(web, "wwwroot", "app.css")))
            .Count(rule => CentresText(rule.Body) && Regex.IsMatch(rule.Selector, "setting|dialog|modal|prose", RegexOptions.IgnoreCase));

        Assert.True(count <= CentredTextCeiling,
            $"Centred text in body copy, dialogs and settings rose to {count} (ceiling {CentredTextCeiling}). Text is left-aligned; only numbers (right) and choice tiles (centred) are exceptions (D15.4). When the count falls, lower the ceiling.");
    }

    [Fact]
    public void TheMatchRow_IsOneNonWrappingFlexLine_OnTheCentreLine()
    {
        var css = File.ReadAllText(Path.Combine(WebRoot(), "Components", "Details", "EditorMatchRow.razor.css"));
        var row = Rules(css).Single(rule => rule.Selector.Trim() == ".tl-matchrow").Body;
        Assert.Contains("display: flex", row, StringComparison.Ordinal);
        Assert.Contains("align-items: center", row, StringComparison.Ordinal);
        Assert.DoesNotContain("wrap", row, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowFooters_KeepCancelWithThePrimary_OnlyAMarkedQuietActionGoesFarLeft()
    {
        // Shy 2026-11-07, as in the Look v3 mock: Delete, Discard and Save changes sit together on the right; only an
        // app-footer-start action (such as Reset) is pushed to the far left. No rule may push a footer's first button aside.
        var css = File.ReadAllText(Path.Combine(WebRoot(), "wwwroot", "app.css"));
        var pushes = Rules(css)
            .Where(rule => rule.Selector.Contains("__footer", StringComparison.Ordinal) && Regex.IsMatch(rule.Body, @"margin-inline-end\s*:\s*auto"))
            .Select(rule => rule.Selector.Trim())
            .ToList();
        Assert.NotEmpty(pushes);
        Assert.All(pushes, selector =>
        {
            Assert.Contains(".app-footer-start", selector, StringComparison.Ordinal);
            Assert.DoesNotContain(":first-child", selector, StringComparison.Ordinal);
        });
    }

    private static void Collect(string file, string selector, string body, List<string> offenders)
    {
        if (CentresText(body) && !Allowed(file, selector, "text-align"))
        {
            offenders.Add($"{file}: {selector.Trim()} -> text-align: center");
        }

        if (Regex.IsMatch(body, @"(?<![\w-])margin-top\s*:(?>\s*)(?!0(?:px|rem|em)?\s*(?:!important)?\s*(?:;|$))") && !Allowed(file, selector, "margin-top"))
        {
            offenders.Add($"{file}: {selector.Trim()} -> margin-top nudge");
        }

        if (Declares(body, "vertical-align") && !Allowed(file, selector, "vertical-align"))
        {
            offenders.Add($"{file}: {selector.Trim()} -> vertical-align");
        }
    }

    private static bool Allowed(string file, string selector, string property) =>
        Allowlist.Any(entry => entry.File == file && selector.Contains(entry.Selector, StringComparison.Ordinal) && entry.Property == property);

    private static bool CentresText(string body) => Regex.IsMatch(body, @"(?<![\w-])text-align\s*:\s*center");

    private static bool Declares(string body, string property) => Regex.IsMatch(body, @"(?<![\w-])" + Regex.Escape(property) + @"\s*:");

    private static IEnumerable<(string Selector, string Body)> Rules(string css) =>
        Rule.Matches(Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline)).Select(m => (m.Groups[1].Value, m.Groups[2].Value));

    private static string WebRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine(dir ?? throw new DirectoryNotFoundException("MediaEngine.slnx not found above the test output."), "src", "MediaEngine.Web");
    }
}
