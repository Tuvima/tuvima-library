using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Keeps every content block on a Settings page to one card style: a <c>SettingsSectionPanel</c> or <c>AppCard</c>,
/// padded by the one <c>--settings-card-padding</c> token the page frame owns, so the first line of text inside any
/// card starts the same distance from the card edge on every page (see engineering/specs/settings-revamp.md, WP3).
/// </summary>
public sealed partial class SettingsCardGuardrailTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string SettingsFolder = Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Settings");
    private static readonly string FrameCss = Path.Combine(SettingsFolder, "Frame", "SettingsPageFrame.razor.css");

    /// <summary>Settings components that may still use <c>AppPanel</c>, each with the reason it is not a page content block.</summary>
    private static readonly IReadOnlyDictionary<string, string> AppPanelAllowlist = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CuratorsDrawer.razor"] = "A side drawer, not a Settings page.",
        ["EnrichmentTesterToolTab.razor"] = "Developer tool; the Developer tools spec replaces it.",
        ["ProviderTesterToolTab.razor"] = "Developer tool; the Developer tools spec replaces it.",
        ["MetadataPipelineStrip.razor"] = "Strip for a library detail view; no Settings page renders it.",
        ["IngestionTasksTab.razor"] = "Live ingestion panels owned by the ingestion and readiness spec; its own panel layout stays until that spec restyles it.",
    };

    /// <summary>
    /// Style rules that pad something named like a card or panel but are not a page content block. A selector is
    /// excused when it contains one of these fragments.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> PaddedSelectorAllowlist = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [".curator-result-card"] = "A result row inside the curators drawer.",
        [".network-status-card"] = "A small status tile inside a settings card.",
        [".admin-library-summary"] = "Library and Ingestion page; rebuilt by the ingestion-flow packet (WP16).",
        [".admin-overview-card"] = "Library and Ingestion page; rebuilt by the ingestion-flow packet (WP16).",
        [".pt-detail-panel"] = "Developer tool; the Developer tools spec replaces it.",
        [".settings-tab-panel"] = "Spacing above the body of a tab strip, not a card.",
    };

    [Fact]
    public void SettingsPageComponents_DoNotUseAppPanelForContentBlocks_ExceptTheListedOnes()
    {
        var offenders = Directory.EnumerateFiles(SettingsFolder, "*.razor", SearchOption.AllDirectories)
            .Where(path => !AppPanelAllowlist.ContainsKey(Path.GetFileName(path)))
            .Where(path => File.ReadAllText(path).Contains("<AppPanel", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepoRoot, path))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Settings content blocks are SettingsSectionPanel or AppCard, not AppPanel: " + string.Join("; ", offenders));
    }

    [Fact]
    public void AppPanelAllowlist_NamesRealComponents_SoItCannotQuietlyGoStale()
    {
        foreach (var file in AppPanelAllowlist.Keys)
        {
            Assert.True(
                Directory.EnumerateFiles(SettingsFolder, file, SearchOption.AllDirectories).Any(),
                $"{file} is in the AppPanel allowlist but no longer exists; remove the entry.");
        }
    }

    [Fact]
    public void TheCardPaddingToken_IsDefinedOnceInTheFrame_AndUsedForEveryCard()
    {
        var frame = StripComments(File.ReadAllText(FrameCss));

        Assert.Single(TokenDeclaration().Matches(frame));

        // Every AppCard (and so every SettingsSectionPanel) reads the token, whatever its density.
        var appCss = StripComments(File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "wwwroot", "app.css")));
        foreach (var cardClass in new[] { "app-card", "app-card--compact", "app-card--comfortable" })
        {
            Assert.Matches(@"(?m)^\." + Regex.Escape(cardClass) + @"\s*\{[^{}]*padding\s*:\s*var\(--settings-card-padding", appCss);
        }

        foreach (var path in SettingsStyleSheets().Where(path => !SamePath(path, FrameCss)))
        {
            Assert.DoesNotMatch(@"--settings-card-padding\s*:", File.ReadAllText(path));
        }
    }

    [Fact]
    public void SettingsStyles_DoNotSetPaddingOnACardClass_TheFrameTokenOwnsIt()
    {
        var offenders = new List<string>();
        foreach (var path in SettingsStyleSheets().Where(path => !SamePath(path, FrameCss)))
        {
            foreach (Match rule in CssRule().Matches(StripComments(File.ReadAllText(path))))
            {
                var selector = rule.Groups["selector"].Value.Trim();
                if (selector.StartsWith('@') || !PaddingDeclaration().IsMatch(rule.Groups["body"].Value))
                {
                    continue;
                }

                if (PaddedSelectorAllowlist.Keys.Any(selector.Contains))
                {
                    continue;
                }

                if (selector.Split(',').Any(part => CardSelector().IsMatch(part.Trim())))
                {
                    offenders.Add($"{Path.GetRelativePath(RepoRoot, path)}: {selector}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Card padding comes from --settings-card-padding in the page frame; these rules set their own: " + string.Join("; ", offenders));
    }

    private static IEnumerable<string> SettingsStyleSheets() =>
        Directory.EnumerateFiles(SettingsFolder, "*.razor.css", SearchOption.AllDirectories)
            .Append(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Pages", "Settings.razor.css"));

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static string StripComments(string css) => Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    [GeneratedRegex(@"--settings-card-padding\s*:")]
    private static partial Regex TokenDeclaration();

    [GeneratedRegex(@"(?<selector>[^{}]+)\{(?<body>[^{}]*)\}")]
    private static partial Regex CssRule();

    [GeneratedRegex(@"(?<![-\w])padding(-block|-inline|-top|-bottom|-left|-right)?\s*:")]
    private static partial Regex PaddingDeclaration();

    // A selector whose last compound is an outer card or panel class: app-card, settings-section-card, anything-card, anything__card, ...-panel.
    [GeneratedRegex(@"\.[\w-]*(card|panel)(?![\w-])(?:[:\[][^\s]*)?$")]
    private static partial Regex CardSelector();
}
