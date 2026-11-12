using System.Text.RegularExpressions;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Look v3.1 D1 (Shy, issue 896): one tab row. Every tab is a 20 px outline icon then the label, 48 px tall, 28 px between tabs,
/// a 3 px purple underline on the open tab and one divider under the row. A tab without an icon fails here, and so does a
/// hand-built tab row outside the shared tab parts and the written allowlist below.
/// </summary>
public sealed partial class TabRowGuardrailTests
{
    // Files that may carry a role="tablist" without the shared tab classes, and why. Starts with the rows that are a different
    // control or are owned by another plan; a new hand-built tab row fails until it moves onto the shared row or is listed here.
    private static readonly Dictionary<string, string> HandBuiltTablistAllowlist = new(StringComparer.Ordinal)
    {
        ["Components/Artwork/ArtworkWorkspace.razor"] = "Role overview: cards with a picture of the chosen artwork for each role, not a row of text tabs.",
        ["Components/Collections/CollectionEditorShell.razor"] = "Draft role overview: cards with an icon, a name and a status line for each artwork role, not a row of text tabs.",
        ["Components/Player/PlaybackListTabs.razor"] = "Player part: owned by the player plans.",
        ["Components/Player/AudioSubtitlesChoicesView.razor"] = "Player part: owned by the player plans.",
    };

    // Tabs allowed to have no icon. Starts empty: every tab has one.
    private static readonly Dictionary<string, string> IconlessTabAllowlist = new(StringComparer.Ordinal);

    // The shared tab parts that own the tab row markup.
    private static readonly string[] SharedTabParts =
    [
        "Components/Shared/AppTabs.razor",
        "Components/Shared/AppCountTabs.razor",
    ];

    [Fact]
    public void EveryAppTabPanelInTheDashboard_SuppliesAnIcon()
    {
        var offenders = new List<string>();
        foreach (var (relative, source) in WebSources().Where(file => file.Relative.EndsWith(".razor", StringComparison.Ordinal)))
        {
            foreach (Match tag in AppTabPanelTag().Matches(source))
            {
                if (!Regex.IsMatch(tag.Value, @"\bIcon\s*=\s*""[^""]+""") && !IconlessTabAllowlist.ContainsKey(relative))
                {
                    offenders.Add($"{relative}: {tag.Value.Split('\n')[0].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Every tab has an outline icon (Look v3.1 D1). AppTabPanel without Icon: " + string.Join("; ", offenders));
        Assert.All(IconlessTabAllowlist.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void EveryTabShape_RequiresAnIconWhereItIsBuilt()
    {
        // A tab built in code can only omit its icon if the type allows it, so the types name the icon as required (a compile error otherwise).
        var panel = Read("Components/Shared/AppTabPanel.razor");
        Assert.Matches(@"\[Parameter, EditorRequired\] public string Icon", panel);
        Assert.DoesNotContain("IsNullOrWhiteSpace(panel.Icon)", Read("Components/Shared/AppTabs.razor"), StringComparison.Ordinal);

        var countTab = Read("Components/Shared/AppCountTab.cs");
        Assert.Contains("string Icon,", countTab, StringComparison.Ordinal);
        Assert.DoesNotContain("string? Icon", countTab, StringComparison.Ordinal);

        Assert.Contains("string Icon,", Read("Models/ViewDTOs/CinematicViewModels.cs"), StringComparison.Ordinal);
        Assert.Contains("string Icon);", Read("Models/ViewDTOs/MediaHubViewModels.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryTabOfTheSettingsMenu_HasAnIcon()
    {
        var tabs = SettingsNav.MenuGroups.SelectMany(group => group.Nodes).SelectMany(node => node.Tabs).ToList();

        Assert.NotEmpty(tabs);
        Assert.All(tabs, tab => Assert.False(string.IsNullOrWhiteSpace(tab.Icon), $"The {tab.Label} tab has no icon."));
    }

    [Fact]
    public void NoHandBuiltTabRow_ExistsOutsideTheSharedPartsAndTheWrittenAllowlist()
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (relative, source) in WebSources().Where(file => file.Relative.EndsWith(".razor", StringComparison.Ordinal)))
        {
            if (SharedTabParts.Contains(relative, StringComparer.Ordinal))
            {
                continue;
            }

            // A tablist drawn with the shared tab row classes is the shared row; anything else is hand-built.
            if (TablistTag().Matches(source).Any(tag => !tag.Value.Contains("tl-tabs-toolbar", StringComparison.Ordinal)))
            {
                found.Add(relative);
            }
        }

        var unlisted = found.Where(path => !HandBuiltTablistAllowlist.ContainsKey(path)).OrderBy(path => path, StringComparer.Ordinal).ToList();
        var stale = HandBuiltTablistAllowlist.Keys.Where(path => !found.Contains(path)).OrderBy(path => path, StringComparer.Ordinal).ToList();

        Assert.True(unlisted.Count == 0, "A tab row is the shared row (tl-tabs-toolbar and tl-tab, with an icon each) or is listed with its reason in HandBuiltTablistAllowlist (Look v3.1 D1): " + string.Join(", ", unlisted));
        Assert.True(stale.Count == 0, "Remove allowlist entries whose hand-built tablist is gone: " + string.Join(", ", stale));
        Assert.All(HandBuiltTablistAllowlist.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void TabRowNumbers_LiveInTokens_AndTheOpenTabUsesTheUnderlineToken()
    {
        var tokens = ReadWwwroot("tuvima.tokens.css");
        Assert.Contains("--tl-tab-min-height: 48px;", tokens, StringComparison.Ordinal);
        Assert.Contains("--tl-tab-gap: 28px;", tokens, StringComparison.Ordinal);
        Assert.Contains("--tl-tab-underline: 3px;", tokens, StringComparison.Ordinal);
        Assert.Contains("--tl-tab-icon-size: var(--tl-icon-size-sm);", tokens, StringComparison.Ordinal);
        Assert.Contains("--tl-icon-size-sm: 20px;", tokens, StringComparison.Ordinal);

        var css = ReadWwwroot("app.css");
        var row = CssRule(css, "\n.tl-tabs-toolbar {");
        Assert.Contains("gap: var(--tl-tab-gap)", row, StringComparison.Ordinal);
        Assert.Contains("min-height: var(--tl-tab-min-height)", row, StringComparison.Ordinal);

        var tab = CssRule(css, "\n.tl-tab {");
        Assert.Contains("min-height: var(--tl-tab-min-height)", tab, StringComparison.Ordinal);
        Assert.Contains("padding: 0;", tab, StringComparison.Ordinal);

        var active = CssRule(css, "\n.tl-tab.tl-tab-active {");
        Assert.Contains("border-bottom: var(--tl-tab-underline) solid var(--tl-accent-primary)", active, StringComparison.Ordinal);

        var icon = CssRule(css, "\n.tl-tab > .tl-icon-root {");
        Assert.Contains("width: var(--tl-tab-icon-size)", icon, StringComparison.Ordinal);
    }

    [Fact]
    public void TabRow_OnAPhone_ScrollsSidewaysAndFadesAtTheCutEdge()
    {
        var css = ReadWwwroot("app.css").Replace("\r\n", "\n", StringComparison.Ordinal);
        var phone = css.IndexOf("@media (max-width: 600px) {\n    @supports (animation-timeline: scroll()) {\n        .tl-tabs-toolbar {", StringComparison.Ordinal);

        Assert.True(phone >= 0, "The tab row has no phone rule.");
        Assert.Contains("mask-image: linear-gradient(90deg", css[phone..(phone + 600)], StringComparison.Ordinal);
        // The fade follows the scroll position, so a row that fits or is scrolled to its end has none.
        Assert.Contains("animation-timeline: scroll(self inline);", css[phone..(phone + 600)], StringComparison.Ordinal);
        Assert.Contains("overflow-x: auto", CssRule(css, "\n.tl-tabs-toolbar {"), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"(?<!\w)<AppTabPanel(?=[\s/>])[\s\S]*?(?<![=-])>", RegexOptions.CultureInvariant)]
    private static partial Regex AppTabPanelTag();

    [GeneratedRegex(@"<[A-Za-z][^<>]*role=""tablist""[^<>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex TablistTag();

    private static string CssRule(string css, string selector)
    {
        var normalized = css.Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = normalized.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{selector.Trim()} was not found.");
        var end = normalized.IndexOf('}', start);
        return normalized[start..end];
    }

    private static string Read(string relativeToWeb) =>
        LocalizedSource.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", relativeToWeb.Replace('/', Path.DirectorySeparatorChar)));

    private static string ReadWwwroot(string file) =>
        LocalizedSource.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "wwwroot", file));

    private static IEnumerable<(string Relative, string Source)> WebSources()
    {
        var web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");
        var sep = Path.DirectorySeparatorChar;
        foreach (var path in Directory.EnumerateFiles(web, "*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
                     .Where(path => !path.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !path.Contains($"{sep}bin{sep}", StringComparison.Ordinal)))
        {
            yield return (Path.GetRelativePath(web, path).Replace(sep, '/'), LocalizedSource.ReadAllText(path));
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
