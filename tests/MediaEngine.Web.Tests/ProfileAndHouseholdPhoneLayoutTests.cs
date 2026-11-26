using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Source-level checks for the phone and tablet layout of My profile, Household and a person's page (spec profile-and-household D16, WP6).
/// They read the stylesheets and markup, so they prove the rules are in place, not how a browser draws them.
/// </summary>
public sealed class ProfileAndHouseholdPhoneLayoutTests
{
    private static readonly string Web = Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web");

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Web }.Concat(parts).ToArray()));

    [Fact]
    public void TheSharedTabRowNeverWrapsAndScrollsSideways()
    {
        var css = Read("wwwroot", "app.css");
        var block = Regex.Match(css, @"\.tl-tabs-toolbar\s*\{(?<body>[^}]*)\}").Groups["body"].Value;

        Assert.Contains("flex-wrap: nowrap", block, StringComparison.Ordinal);
        Assert.Contains("overflow-x: auto", block, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsTabRowsTightenTheirGapSoSixAdministratorTabsFitAt1024()
    {
        var css = Read("wwwroot", "app.css");

        Assert.Matches(
            new Regex(@"@media \(max-width: 1180px\)\s*\{\s*\.tl-tabs-toolbar\.settings-page-tabs\s*\{\s*gap: var\(--tl-space-4, 16px\);", RegexOptions.Singleline),
            css);
    }

    [Fact]
    public void TheFrameBackLinkIsA44PixelTargetOnAPhone()
    {
        var css = Read("Components", "Settings", "Frame", "SettingsPageFrame.razor.css");
        var phone = Regex.Match(css, @"@media \(max-width: 600px\)\s*\{(?<body>.*?)\n\}", RegexOptions.Singleline).Groups["body"].Value;

        Assert.Contains(".settings-frame__back", phone, StringComparison.Ordinal);
        Assert.Contains("min-height: var(--tl-tap-target-min, 44px)", phone, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheFrameDrawsABackLinkOnAPersonsPage()
    {
        // The preference pages draw their own "Back to" link only when they are the whole page, never inside a person's page.
        foreach (var page in new[] { "PlaybackSettingsPage", "ListeningSettingsPage", "ReadingSettingsPage" })
        {
            var markup = Read("Components", "Settings", page + ".razor");
            Assert.Contains("IsOtherProfile && !Embedded", markup, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("-back", Read("Components", "Settings", "ProfileDetailPage.razor"), StringComparison.Ordinal);
        Assert.DoesNotContain("-back", Read("Components", "Settings", "ProfilesTab.razor"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PlaybackSettingsPage.razor", 2)]
    [InlineData("ListeningSettingsPage.razor", 1)]
    [InlineData("ReadingSettingsPage.razor", 6)]
    [InlineData("HomeSettingsPage.razor", 1)]
    [InlineData("HomeLanesSettings.razor", 1)]
    [InlineData("VideoOptionsPanel.razor", 1)]
    public void OptionRowsWithAChoiceStackTheirControlUnderTheLabelOnAPhone(string file, int expected)
    {
        var markup = Read("Components", "Settings", file);

        Assert.Equal(expected, Regex.Matches(markup, @"<AppSettingRow StackOnPhone=""true""").Count);
    }

    [Fact]
    public void ReadingOptionsAllStackAndNoPreferenceRowIsLeftBesideItsChoice()
    {
        var markup = Read("Components", "Settings", "ReadingSettingsPage.razor");

        Assert.DoesNotMatch(@"<AppSettingRow Label=", markup);
    }

    [Fact]
    public void VideoQualityAndDeviceChoices_StackUnderTheirLabelOnAPhoneAndTakeTheFullWidth()
    {
        var css = Read("wwwroot", "app.css");
        var phone = Regex.Match(css, @"@media \(max-width: 600px\) \{\s*:root \.settings-playback-page \.playback-preference-row__select(?<body>.*?)\n\}", RegexOptions.Singleline).Groups["body"].Value;

        Assert.Contains("width: 100%", phone, StringComparison.Ordinal);
        Assert.Contains(".app-setting-row--stack-on-phone .app-setting-row__control > .app-field", phone, StringComparison.Ordinal);
    }

    [Fact]
    public void ChoiceSelectsAreWideEnoughForNoPreference_AndSegmentedChoicesNeverCutTheirLabels()
    {
        var playback = Read("Components", "Settings", "PlaybackSettingsPage.razor.css");
        var css = Read("wwwroot", "app.css");

        Assert.Matches(@"playback-preference-row__select \{\s*width: 200px;\s*min-width: 200px;", playback);
        foreach (var row in new[] { "reading", "playback" })
        {
            Assert.Matches(@"\." + row + @"-preference-row__control \.app-segmented-control__item \{ flex: 1 0 auto;", css);
            Assert.Contains("." + row + "-preference-row__control .app-segmented-control__item > span { overflow: visible; text-overflow: clip; }", css, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePreferenceSectionSwitchHasRoomBelowItBeforeTheSectionHeading()
    {
        var css = Read("wwwroot", "app.css");

        Assert.Matches(@"\.profile-preferences \.profile-detail__section-switch \{ margin-bottom: var\(--tl-space-5, 20px\); \}", css);
    }

    [Fact]
    public void OnAPhoneAPersonsPageKeepsOnlyTheBarsBackArrow_AndMyProfileAndHouseholdNameThemselvesInTheBar()
    {
        var settings = Read("Components", "Pages", "Settings.razor");
        var layout = Read("Shared", "MainLayout.razor");

        Assert.Contains("DeviceContext.IsPhone && IsProfileDetailRoute ? null : BackLink?.Href", settings, StringComparison.Ordinal);
        Assert.Contains("BarTitle?.Set(PhoneBarTitleText)", settings, StringComparison.Ordinal);
        Assert.Contains("_phoneBarTitle.Text ?? L[\"Nav_Settings\"].Value", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStackRuleRunsOnlyAtPhoneWidthAndStretchesTheControl()
    {
        var css = Read("Components", "Shared", "AppSettingRow.razor.css");
        var phone = Regex.Match(css, @"@media \(max-width: 600px\)\s*\{(?<body>.*)\}", RegexOptions.Singleline).Groups["body"].Value;

        Assert.Contains(".app-setting-row--stack-on-phone .app-setting-row__line", phone, StringComparison.Ordinal);
        Assert.Contains("flex-direction: column", phone, StringComparison.Ordinal);
        Assert.Contains("width: 100%", phone, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePreferenceSectionSwitchSharesTheFullWidthOnAPhone()
    {
        var css = Read("wwwroot", "app.css");
        var start = css.IndexOf(".profile-preferences { min-width: 0; }", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var rules = css.Substring(start, 700);

        Assert.Contains("@media (max-width: 600px)", rules, StringComparison.Ordinal);
        Assert.Contains(".profile-detail__section-switch { display: flex; width: 100%", rules, StringComparison.Ordinal);
        Assert.Contains("flex: 1 1 0; min-width: 0; min-height: var(--tl-tap-target-min, 44px)", rules, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMyProfileBannerButtonsStackOnAPhoneInsteadOfSqueezingEachOther()
    {
        var css = Read("Components", "Settings", "Overview", "OverviewPage.razor.css");
        var phone = Regex.Match(css, @"@media \(max-width: 600px\)\s*\{(?<body>.*?)\n\}", RegexOptions.Singleline).Groups["body"].Value;

        Assert.Contains("flex-direction: column", phone, StringComparison.Ordinal);
        Assert.Contains("align-items: stretch", phone, StringComparison.Ordinal);
    }

    [Fact]
    public void HouseholdOffersNoDragHandleOnAPhoneAndReorderLivesInTheMenu()
    {
        var markup = Read("Components", "Settings", "ProfilesTab.razor");

        Assert.Contains("CanReorderPeople(group) && !DeviceContext.IsPhone", markup, StringComparison.Ordinal);
        Assert.Contains("DeviceContext.IsPhone && Groups.Any(CanReorderPeople)", markup, StringComparison.Ordinal);
        Assert.Contains("ProfilesTab_Reorder", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewStylesheetsStayOnTheSupportedBreakpoints()
    {
        foreach (var file in new[]
        {
            Path.Combine("Components", "Settings", "Frame", "SettingsPageFrame.razor.css"),
            Path.Combine("Components", "Settings", "Overview", "OverviewPage.razor.css"),
        })
        {
            foreach (Match match in Regex.Matches(Read(file), @"@media[^{]*?(?:min|max)-width:\s*(?<value>\d+)px"))
            {
                Assert.Contains(match.Groups["value"].Value, new[] { "600", "840", "1180" });
            }
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
