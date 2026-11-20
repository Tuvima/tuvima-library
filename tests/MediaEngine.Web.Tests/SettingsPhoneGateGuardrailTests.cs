using System.Text.RegularExpressions;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Settings alignment D13 and WP7: a phone is not told to "use a computer or tablet" for Settings. Developer Tools are open to
/// administrators on a phone, with each tester drawn as stacked cards. The render half (the Developer Tools page on a phone shows
/// its content and no larger-screen wording) is <c>UiShellRenderTests.DeveloperToolsPhone.cs</c>, which needs the shell's render
/// harness; this class holds the static rules.
/// </summary>
public sealed partial class SettingsPhoneGateGuardrailTests
{
    private static readonly SettingsSection[] DeveloperSections =
    [
        SettingsSection.ProviderTester,
        SettingsSection.EnrichmentTester,
        SettingsSection.DevHarness,
    ];

    /// <summary>
    /// Sections that still carry a phone gate while the phone standards work (Look v3.2 WP6) removes them. The list only shrinks:
    /// a section outside it that becomes gated turns the test red.
    /// </summary>
    private static readonly SettingsSection[] StillGatedSections =
    [
        // removed by Look v3.2 phone standards WP6; remove from this list when that lands
        SettingsSection.AdminOverview,
        // removed by Look v3.2 phone standards WP6; remove from this list when that lands
        SettingsSection.Libraries,
        // removed by Look v3.2 phone standards WP6; remove from this list when that lands
        SettingsSection.Providers,
        // removed by Look v3.2 phone standards WP6; remove from this list when that lands
        SettingsSection.LibraryRules,
        // removed by Look v3.2 phone standards WP6; remove from this list when that lands
        SettingsSection.Playback,
        // removed by Look v3.2 phone standards WP6; remove from this list when that lands
        SettingsSection.Network,
        // removed by Look v3.2 phone standards WP6; remove from this list when that lands
        SettingsSection.LocalAi,
        // removed by Look v3.2 phone standards WP6; remove from this list when that lands
        SettingsSection.Plugins,
        // removed by Look v3.2 phone standards WP6; remove from this list when that lands
        SettingsSection.Backups,
    ];

    private static readonly string[] DeveloperTabFiles =
    [
        "ProviderTesterToolTab.razor",
        "EnrichmentTesterToolTab.razor",
        "DevHarnessTab.razor",
    ];

    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void TheDeveloperSections_AreFullyAvailableOnAPhone()
    {
        foreach (var section in DeveloperSections)
        {
            var availability = SettingsNav.GetMobileAvailability(section);
            Assert.NotEqual(SettingsMobileAvailability.DesktopOnly, availability);
            Assert.NotEqual(SettingsMobileAvailability.SummaryOnly, availability);
            Assert.True(SettingsNav.IsVisibleOnMobile(section), section.ToString());
            Assert.True(SettingsNav.IsMobileRouteAvailable(section), section.ToString());
        }
    }

    [Fact]
    public void NoSettingsPageOutsideTheStillGatedList_OffersALargerScreenMessage()
    {
        var offenders = new List<string>();
        foreach (var section in Enum.GetValues<SettingsSection>().Where(section => !StillGatedSections.Contains(section)))
        {
            if (SettingsNav.GetMobileAvailability(section) != SettingsMobileAvailability.Full || !SettingsNav.IsMobileRouteAvailable(section))
            {
                offenders.Add(section.ToString());
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A phone is not told to use a larger screen for Settings (Settings alignment D13). New phone gate on: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheStillGatedList_OnlyHoldsSectionsThatAreStillGated()
    {
        // Keeps the allowlist honest: once a section is open on a phone it must leave the list.
        var stale = StillGatedSections
            .Where(section => SettingsNav.GetMobileAvailability(section) == SettingsMobileAvailability.Full)
            .Select(section => section.ToString())
            .ToList();

        Assert.True(stale.Count == 0, "No longer gated, remove from the allowlist: " + string.Join(", ", stale));
    }

    [Theory]
    [InlineData("intentionally withheld from mobile")]
    [InlineData("desktop or tablet workflow")]
    public void TheDeveloperTools_NeverUseTheDesktopOnlyWording(string phrase)
    {
        foreach (var file in DeveloperTabFiles)
        {
            Assert.DoesNotContain(phrase, File.ReadAllText(TabPath(file)), StringComparison.OrdinalIgnoreCase);
        }

        // The wording belongs to the shell's generic gate for pages that are still gated, and lives nowhere else.
        var holders = Directory
            .EnumerateFiles(Path.Combine(RepoRoot, "src", "MediaEngine.Web"), "*.*", SearchOption.AllDirectories)
            .Where(path => (path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(phrase, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFileName(path))
            .ToList();

        Assert.All(holders, name => Assert.Equal("Settings.razor", name));
    }

    [Fact]
    public void EveryResultTableInTheTesters_BecomesCardsOnAPhone_WithARoleOnEveryColumn()
    {
        foreach (var file in DeveloperTabFiles)
        {
            var markup = File.ReadAllText(TabPath(file));
            foreach (Match table in AppTableBlock().Matches(markup))
            {
                Assert.Contains("CardsOn=\"AppTableCardsMode.Phone\"", table.Value, StringComparison.Ordinal);

                // A column is either given a card role or deliberately left to stack full width beneath the card, so the header
                // row must name at least a title and every role used in a header must also be used in the body.
                var header = HeaderRoles().Matches(table.Value).Select(m => m.Groups["r"].Value).ToHashSet();
                Assert.Contains("title", header);
                var bodyRoles = BodyRoles().Matches(table.Value).Select(m => m.Groups["r"].Value).ToHashSet();
                Assert.True(header.IsSubsetOf(bodyRoles), $"{file}: a header role has no body cell. Header {string.Join(",", header)}, body {string.Join(",", bodyRoles)}");
            }
        }
    }

    [Fact]
    public void TheTesters_DoNotLetThePageScrollSidewaysOnAPhone()
    {
        // No fixed-width wrappers: every table is cards on a phone and the page-level controls stack full width.
        foreach (var file in DeveloperTabFiles)
        {
            var markup = File.ReadAllText(TabPath(file));
            Assert.DoesNotContain("overflow-x", markup, StringComparison.OrdinalIgnoreCase);
        }

        // The testers' full-width actions and 48 px chips sit on shared App* components, so their phone rules live in app.css.
        var css = File.ReadAllText(Path.Combine(RepoRoot, "src", "MediaEngine.Web", "wwwroot", "app.css"));
        foreach (var cls in new[] { "pt-search-action", "enr-search-action", "dev-harness-action", "dev-harness-chip" })
        {
            Assert.Contains($":root[data-form-factor=\"phone\"] .{cls}", css, StringComparison.Ordinal);
        }

        // The 48 px touch target comes from the shared token, never a literal.
        Assert.Contains("min-height: var(--tl-touch-target-min)", css, StringComparison.Ordinal);
    }

    private static string TabPath(string file) =>
        Path.Combine(RepoRoot, "src", "MediaEngine.Web", "Components", "Settings", file);

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    [GeneratedRegex(@"<AppTable\b.*?</AppTable>", RegexOptions.Singleline)]
    private static partial Regex AppTableBlock();

    [GeneratedRegex(@"<th\b[^>]*data-card-role=""(?<r>[a-z]+)""")]
    private static partial Regex HeaderRoles();

    [GeneratedRegex(@"<(?:td|AppCssElement)\b[^>]*data-card-role=""(?<r>[a-z]+)""")]
    private static partial Regex BodyRoles();
}
