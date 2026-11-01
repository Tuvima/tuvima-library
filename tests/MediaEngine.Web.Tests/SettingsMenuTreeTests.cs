using Bunit;
using MediaEngine.Web.Components.MediaHub;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Settings menu as one flat list of pages (flat-menu spec D3, D40, D45): what the builder produces for each kind of
/// person, how the shell draws the rows, and that the phone page selector lists the same pages.
/// </summary>
public sealed class SettingsMenuTreeTests : AsyncBunitContext
{
    public SettingsMenuTreeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.CreateDefault());
    }

    [Fact]
    public void ServerAdministrator_GetsTheGroupsInOrder_AndEveryRowHasAnIcon()
    {
        // Personal, Library and Server come first, in that order; Developer Tools follow, as before.
        var groups = SettingsMenuBuilder.Build(Context(administrator: true));

        Assert.Equal(["Personal", "Library", "Server", "Developer"], groups.Select(group => group.Label));
        var rows = Flatten(groups).ToArray();
        Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.Icon), row.Label));
    }

    [Fact]
    public void EveryRow_IsAPageWithNoChildren_AndOpensItsFirstTab()
    {
        var groups = SettingsMenuBuilder.Build(Context(administrator: true));

        Assert.All(Flatten(groups), row => Assert.True(row.Children is null || row.Children.Count == 0, row.Label));
        Assert.Equal(
            ["Profile", "Manage Profiles", "Security & Devices"],
            groups[0].Items.Select(row => row.Label));
        Assert.Equal(["Libraries", "Playback"], groups.Single(group => group.Label == "Library").Items.Select(row => row.Label));
        Assert.Equal(
            ["General", "Users & Households", "Network & Remote Access", "Tools", "Local AI", "Plugins", "Integrations", "Backups", "System"],
            groups.Single(group => group.Label == "Server").Items.Select(row => row.Label));
        Assert.Equal(["Developer Tools"], groups.Single(group => group.Label == "Developer").Items.Select(row => row.Label));

        Assert.Equal("/settings/overview", Flatten(groups).Single(row => row.Label == "Profile").Route);
        Assert.Equal("/settings/profiles", Flatten(groups).Single(row => row.Label == "Manage Profiles").Route);
        Assert.Equal("/settings/libraries", Flatten(groups).Single(row => row.Label == "Libraries").Route);
        Assert.Equal("/settings/general", Flatten(groups).Single(row => row.Label == "General").Route);
        Assert.Equal("/settings/backups", Flatten(groups).Single(row => row.Label == "Backups").Route);
        Assert.Equal("/settings/system", Flatten(groups).Single(row => row.Label == "System").Route);
        Assert.Equal("/settings/ai/status", Flatten(groups).Single(row => row.Label == "Local AI").Route);
        Assert.Equal("/settings/integrations", Flatten(groups).Single(row => row.Label == "Integrations").Route);
        Assert.DoesNotContain(Flatten(groups), row => row.Label is "Preferences" or "Users" or "Features" or "Providers" or "Tools and requirements");
    }

    [Fact]
    public void APageStaysLitOnAllItsTabs_AndOnThePagesBeneathThem()
    {
        var groups = SettingsMenuBuilder.Build(Context(administrator: true));

        var libraries = Flatten(groups).Single(row => row.Label == "Libraries");
        Assert.False(libraries.Exact);
        Assert.Contains("/settings/libraries", libraries.ActiveRoutes!);
        Assert.Contains("/settings/matching", libraries.ActiveRoutes!);
        Assert.Contains("/settings/library-activity", libraries.ActiveRoutes!);
        Assert.Contains("/settings/review", libraries.ActiveRoutes!);
        Assert.Contains("/settings/rules", libraries.ActiveRoutes!);

        var profiles = Flatten(groups).Single(row => row.Label == "Manage Profiles");
        Assert.Contains("/settings/profiles", profiles.ActiveRoutes!);
    }

    [Fact]
    public void Member_GetsOnlyPersonal_AndKidsGetsOnlyProfile()
    {
        var member = SettingsMenuBuilder.Build(Context(administrator: false));
        Assert.Equal(["Personal"], member.Select(group => group.Label));
        Assert.Equal(["Profile", "Manage Profiles", "Security & Devices"], member[0].Items.Select(row => row.Label));

        var kids = SettingsMenuBuilder.Build(Context(administrator: false) with { RestrictedProfile = true });
        Assert.Equal(["Personal"], kids.Select(group => group.Label));
        Assert.Equal(["Profile"], kids[0].Items.Select(row => row.Label));
    }

    [Fact]
    public void UsersAndHouseholds_IsInTheMenuOnlyForTheServerAdministrator()
    {
        var member = Flatten(SettingsMenuBuilder.Build(Context(administrator: false))).Select(row => row.Label).ToArray();
        Assert.DoesNotContain("Users & Households", member);
        Assert.DoesNotContain("Integrations", member);

        var server = Flatten(SettingsMenuBuilder.Build(Context(administrator: true))).ToArray();
        var users = server.Single(row => row.Label == "Users & Households");
        Assert.Equal("/settings/access/users", users.Route);
        Assert.Contains("/settings/access", users.ActiveRoutes!);
        Assert.Equal("/settings/profiles", server.Single(row => row.Label == "Manage Profiles").Route);
    }

    [Fact]
    public void NeedsReviewCount_MakesLibrariesWaiting_AndRecentlyAddedIsNotInTheMenu()
    {
        var groups = SettingsMenuBuilder.Build(Context(administrator: true) with { ReviewCount = 3 });

        var libraries = Flatten(groups).Single(row => row.Label == "Libraries");
        Assert.Equal(MediaSectionAttentionLevel.Waiting, libraries.Attention);
        Assert.Null(libraries.Meta);
        Assert.False(libraries.MetaIsNotification);
        Assert.DoesNotContain(Flatten(groups), row => row.Label == "Recently Added");
        Assert.Equal(MediaSectionAttentionLevel.None, Flatten(groups).Single(row => row.Label == "System").EffectiveAttention);
    }

    [Fact]
    public void LocalAiBlocked_WithAnAiFeatureOn_MakesItsRowRedWithBroken_ButOtherPagesStayUnshaded()
    {
        var groups = SettingsMenuBuilder.Build(Context(administrator: true) with { LocalAiBlocked = true });

        var localAi = Flatten(groups).Single(row => row.Label == "Local AI");
        Assert.Equal(MediaSectionAttentionLevel.Blocked, localAi.Attention);
        Assert.Equal("Broken", localAi.AttentionLabel);
        Assert.Equal(MediaSectionAttentionLevel.Blocked, groups.Single(group => group.Label == "Server").Items.Max(row => row.EffectiveAttention));
        Assert.Equal(MediaSectionAttentionLevel.None, Flatten(groups).Single(row => row.Label == "System").EffectiveAttention);
        Assert.Equal(MediaSectionAttentionLevel.None, Flatten(groups).Single(row => row.Label == "Libraries").EffectiveAttention);
    }

    [Fact]
    public void LocalAiBlocked_WithNoAiFeatureSwitchedOn_IsNeverRed()
    {
        var groups = SettingsMenuBuilder.Build(Context(administrator: true) with { LocalAiBlocked = true, AiFeaturesOn = false });

        var localAi = Flatten(groups).Single(row => row.Label == "Local AI");
        Assert.Equal(MediaSectionAttentionLevel.None, localAi.EffectiveAttention);
        Assert.Null(localAi.AttentionLabel);
    }

    [Fact]
    public void OnAPhoneOrTablet_TheDeveloperAreaIsNotListedAtAll()
    {
        var groups = SettingsMenuBuilder.Build(Context(administrator: true) with { IsMobile = true });

        var labels = Flatten(groups).Select(row => row.Label).ToArray();
        Assert.DoesNotContain("Local AI", labels);
        Assert.DoesNotContain("Plugins", labels);
        Assert.DoesNotContain("Integrations", labels);
        Assert.DoesNotContain(groups, group => group.Label is "Developer");
        Assert.DoesNotContain(SettingsMenuBuilder.BuildPhoneSelector(groups), group => group.Label is "Developer");
    }

    [Fact]
    public void LocalAiBlocked_OnAPhone_DrawsNoShading()
    {
        var groups = SettingsMenuBuilder.Build(Context(administrator: true) with { LocalAiBlocked = true, IsMobile = true });

        Assert.All(Flatten(groups), row => Assert.Equal(MediaSectionAttentionLevel.None, row.EffectiveAttention));
    }

    [Fact]
    public void NothingWaiting_MeansNoShading()
    {
        var groups = SettingsMenuBuilder.Build(Context(administrator: true));

        Assert.All(Flatten(groups), row => Assert.Equal(MediaSectionAttentionLevel.None, row.EffectiveAttention));
    }

    [Fact]
    public void OnAPhone_RowsArePlain_AndOnlyPagesOfferedOnAPhoneAreListed()
    {
        var groups = SettingsMenuBuilder.Build(Context(administrator: true) with { ReviewCount = 3, IsMobile = true });
        var labels = Flatten(groups).Select(row => row.Label).ToArray();

        Assert.All(Flatten(groups), row => Assert.Equal(MediaSectionAttentionLevel.None, row.EffectiveAttention));
        Assert.DoesNotContain("Developer Tools", labels);
        Assert.DoesNotContain("Enrichment Tester", labels);
        Assert.DoesNotContain("Development data", labels);
        Assert.DoesNotContain("Recently Added", labels);
        Assert.Contains("Libraries", labels);
        Assert.Contains("Playback", labels);
    }

    [Fact]
    public void FeatureSwitchedOff_HidesTheRow()
    {
        var groups = SettingsMenuBuilder.Build(Context(administrator: true) with
        {
            IsSectionEnabled = section => section != SettingsSection.LocalAi,
        });

        var labels = Flatten(groups).Select(row => row.Label).ToArray();
        Assert.DoesNotContain("Local AI", labels);
        Assert.Contains("Plugins", labels);
        Assert.Contains("Developer Tools", labels);
    }

    [Fact]
    public void PhoneSelector_ListsTheSamePagesAsTheDesktopMenu_OneLineEach()
    {
        var groups = SettingsMenuBuilder.Build(Context(administrator: true));
        var selector = SettingsMenuBuilder.BuildPhoneSelector(groups);

        Assert.Equal(groups.Select(group => group.Label), selector.Select(group => group.Label));
        Assert.Equal(
            Flatten(groups).Select(row => row.Route),
            selector.SelectMany(group => group.Options).Select(option => option.Route));
        Assert.Equal(
            Flatten(groups).Select(row => row.Label),
            selector.SelectMany(group => group.Options).Select(option => option.Label));
    }

    [Fact]
    public void Shell_DrawsEveryRowWithIconLabelAndBadgeSlots_AndNoChevron()
    {
        var cut = RenderMenu(SettingsMenuBuilder.Build(Context(administrator: true)), "/settings/profiles");

        Assert.Contains("media-section-shell__tree--flat", cut.Find(".media-section-shell__tree").ClassList);
        var rows = cut.FindAll(".media-section-shell__tree-row");
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            Assert.NotNull(row.QuerySelector(".media-section-shell__tree-icon .tl-icon-root, .media-section-shell__tree-icon svg"));
            Assert.NotNull(row.QuerySelector(".media-section-shell__tree-label"));
            Assert.NotNull(row.QuerySelector(".media-section-shell__tree-badge"));
            Assert.Null(row.QuerySelector(".media-section-shell__tree-chevron-slot button"));
        }
    }

    [Fact]
    public void Shell_EveryRowShowsItsFullLabelOnHover_SoATruncatedLabelCanStillBeRead()
    {
        var cut = RenderMenu(SettingsMenuBuilder.Build(Context(administrator: true)), "/settings/profiles");

        var links = cut.FindAll("a.media-section-shell__tree-link");
        Assert.NotEmpty(links);
        foreach (var link in links)
        {
            var label = link.QuerySelector(".media-section-shell__tree-label")!.TextContent;
            Assert.Equal(label, link.GetAttribute("title"));
        }
    }

    [Fact]
    public void Shell_ClickingARowOpensItsPage_AndTheRowIsHighlighted()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = RenderMenu(SettingsMenuBuilder.Build(Context(administrator: true)), "/settings/profiles");

        cut.Find("a[href='/settings/security']").Click();

        Assert.EndsWith("/settings/security", navigation.Uri, StringComparison.Ordinal);
        cut.WaitForAssertion(() =>
        {
            var row = cut.Find("a[href='/settings/security']");
            Assert.Equal("page", row.GetAttribute("aria-current"));
            Assert.Contains("is-active", row.ParentElement!.ClassList);
        });
    }

    [Theory]
    [InlineData("/settings/matching", "/settings/libraries")]
    [InlineData("/settings/library-activity", "/settings/libraries")]
    [InlineData("/settings/security/devices", "/settings/security")]
    [InlineData("/settings/backups/destinations", "/settings/backups")]
    [InlineData("/settings/system/advanced", "/settings/system")]
    public void Shell_APageRowStaysLit_WhenOneOfItsTabsOrPagesBeneathItIsOpen(string openRoute, string rowRoute)
    {
        var cut = RenderMenu(SettingsMenuBuilder.Build(Context(administrator: true)), openRoute);

        var row = cut.Find($"a[href='{rowRoute}']");
        Assert.Contains("is-active", row.ParentElement!.ClassList);
        Assert.Single(cut.FindAll(".media-section-shell__tree-row.is-active"));
    }

    [Fact]
    public void Shell_WaitingAndBlockedRowsGetTheirTints_AndAParentTakesTheMostUrgentChild()
    {
        var groups = new[]
        {
            new MediaSectionNavigationGroup("Server",
            [
                new("Library", "/settings/ingestion", AppMaterialIcons.Outlined.VideoLibrary, Exact: true,
                    Children:
                    [
                        new("Recently Added", "/settings/recently-added", AppMaterialIcons.Outlined.History, Meta: "3", Exact: true,
                            MetaIsNotification: true, Attention: MediaSectionAttentionLevel.Waiting),
                        new("Libraries", "/settings/libraries", AppMaterialIcons.Outlined.Folder, Exact: true),
                    ]),
                new("Local AI", "/settings/ai", AppMaterialIcons.Outlined.Memory, Exact: true,
                    Attention: MediaSectionAttentionLevel.Blocked, AttentionLabel: "Off"),
                new("Plugins", "/settings/plugins", AppMaterialIcons.Outlined.Extension, Exact: true,
                    Children:
                    [
                        new("Catalog", "/settings/plugins/catalog", AppMaterialIcons.Outlined.Verified, Exact: true,
                            Attention: MediaSectionAttentionLevel.Waiting),
                        new("Danger", "/settings/plugins/danger", AppMaterialIcons.Outlined.Delete, Exact: true,
                            Attention: MediaSectionAttentionLevel.Blocked, AttentionLabel: "Stopped"),
                    ]),
            ]),
        };

        var cut = RenderMenu(groups, "/settings/recently-added");

        var library = cut.Find("a[href='/settings/ingestion']").ParentElement!;
        Assert.Contains("is-attention-waiting", library.ClassList);
        Assert.Contains("is-attention-waiting", cut.Find("a[href='/settings/recently-added']").ParentElement!.ClassList);
        Assert.DoesNotContain("is-attention-waiting", cut.Find("a[href='/settings/libraries']").ParentElement!.ClassList);

        var ai = cut.Find("a[href='/settings/ai']").ParentElement!;
        Assert.Contains("is-attention-blocked", ai.ClassList);
        Assert.Contains("Off", ai.TextContent);

        // Blocked outranks Waiting, so a collapsed parent shows red when one child cannot work.
        var plugins = cut.Find("a[href='/settings/plugins']").ParentElement!;
        Assert.Contains("is-attention-blocked", plugins.ClassList);
        Assert.DoesNotContain("is-attention-waiting", plugins.ClassList);
    }

    [Fact]
    public void Shell_PhoneRowsCarryNoAttentionClasses()
    {
        var cut = RenderMenu(SettingsMenuBuilder.Build(Context(administrator: true) with { ReviewCount = 3, IsMobile = true }), "/settings/recently-added");

        Assert.Empty(cut.FindAll(".is-attention-waiting, .is-attention-blocked"));
    }

    [Fact]
    public void Shell_WithoutTheFixedColumnMode_KeepsTheOriginalRows()
    {
        var navigation = new[]
        {
            new MediaSectionNavigationGroup("Library",
            [
                new("Discover", "/read", AppMaterialIcons.Outlined.Explore, Exact: true),
                new("Books", "/read/books", AppMaterialIcons.Outlined.MenuBook, Attention: MediaSectionAttentionLevel.Waiting),
            ]),
        };

        var cut = Render<MediaSectionShell>(parameters => parameters
            .Add(component => component.Title, "Read")
            .Add(component => component.NavigationGroups, navigation)
            .AddChildContent("<section>Read</section>"));

        Assert.Equal(2, cut.FindAll("a.media-section-shell__rail-item").Count);
        Assert.Empty(cut.FindAll(".media-section-shell__tree"));
        Assert.Empty(cut.FindAll(".is-attention-waiting"));
    }

    private IRenderedComponent<MediaSectionShell> RenderMenu(IReadOnlyList<MediaSectionNavigationGroup> groups, string route)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(route);
        return Render<MediaSectionShell>(parameters => parameters
            .Add(component => component.Title, "Settings")
            .Add(component => component.NavigationGroups, groups)
            .Add(component => component.FixedColumnTree, true)
            .Add(component => component.ShowMobileNavigation, false)
            .AddChildContent("<section>Settings content</section>"));
    }

    [Fact]
    public void ToolsRow_ShowsRedWithTheReasonWhenARequiredToolIsMissing_AndStaysPlainOtherwise()
    {
        var red = Flatten(SettingsMenuBuilder.Build(Context(administrator: true) with { ToolsMissingReason = "FFmpeg missing" }))
            .Single(item => item.Label == "Tools");
        var plain = Flatten(SettingsMenuBuilder.Build(Context(administrator: true)))
            .Single(item => item.Label == "Tools");

        Assert.Equal(MediaSectionAttentionLevel.Blocked, red.EffectiveAttention);
        Assert.Equal("FFmpeg missing", red.AttentionLabel);
        Assert.Equal(MediaSectionAttentionLevel.None, plain.EffectiveAttention);
    }

    private static SettingsMenuContext Context(bool administrator) =>
        new(administrator, IsMobile: false, ReviewCount: 0, SecurityNeedsAttention: false);

    private static IEnumerable<MediaSectionNavigationItem> Flatten(IEnumerable<MediaSectionNavigationGroup> groups) =>
        groups.SelectMany(group => Flatten(group.Items));

    private static IEnumerable<MediaSectionNavigationItem> Flatten(IEnumerable<MediaSectionNavigationItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            if (item.Children is not null)
            {
                foreach (var child in Flatten(item.Children))
                {
                    yield return child;
                }
            }
        }
    }
}
