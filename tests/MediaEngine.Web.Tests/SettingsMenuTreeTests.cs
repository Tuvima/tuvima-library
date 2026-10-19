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
/// The Settings menu as one fixed-column tree (spec D2, D3, D40, D45): what the builder produces for each kind of
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
    public void ServerAdministrator_GetsFourGroupsInOrder_AndEveryRowHasAnIcon()
    {
        var groups = SettingsMenuBuilder.Build(Context(administrator: true));

        Assert.Equal(["Personal", "Server", "Manage", "Developer"], groups.Select(group => group.Label));
        var rows = Flatten(groups).ToArray();
        Assert.All(rows, row => Assert.False(string.IsNullOrWhiteSpace(row.Icon), row.Label));
    }

    [Fact]
    public void ParentRows_OpenTheirOwnMainPage_AndNoChildRepeatsThem()
    {
        var groups = SettingsMenuBuilder.Build(Context(administrator: true));

        var ingestion = Flatten(groups).Single(row => row.Label == "Library and Ingestion");
        Assert.Equal("/settings/ingestion", ingestion.Route);
        Assert.DoesNotContain(ingestion.Children!, child => child.Route == ingestion.Route);

        var users = Flatten(groups).Single(row => row.Label == "Users and Sign-in");
        Assert.Equal("/settings/access/users", users.Route);
        Assert.Equal(["Sign-in Methods"], users.Children!.Select(child => child.Label));

        Assert.Equal("/settings/preferences", Flatten(groups).Single(row => row.Label == "Preferences").Route);
        Assert.Equal("/settings/ai", Flatten(groups).Single(row => row.Label == "Local AI").Route);
        Assert.DoesNotContain(Flatten(groups), row => row.Label is "Overview" or "Users" or "Features" or "Providers");
    }

    [Fact]
    public void MemberAndHouseholdAdministrator_GetOnlyPersonal()
    {
        var member = SettingsMenuBuilder.Build(Context(administrator: false));
        Assert.Equal(["Personal"], member.Select(group => group.Label));

        var household = SettingsMenuBuilder.Build(Context(administrator: false) with { HouseholdAdministrator = true });
        Assert.Equal(["Personal"], household.Select(group => group.Label));
    }

    [Fact]
    public void Profiles_IsInTheMenuOnlyForAdministrators_AndHouseholdsOnlyForTheServerAdministrator()
    {
        var member = Flatten(SettingsMenuBuilder.Build(Context(administrator: false))).Select(row => row.Label).ToArray();
        Assert.DoesNotContain("Profiles", member);
        Assert.DoesNotContain("Households", member);

        var household = Flatten(SettingsMenuBuilder.Build(Context(administrator: false) with { HouseholdAdministrator = true })).Select(row => row.Label).ToArray();
        Assert.Contains("Profiles", household);
        Assert.DoesNotContain("Households", household);

        var server = Flatten(SettingsMenuBuilder.Build(Context(administrator: true))).ToArray();
        var profiles = server.Single(row => row.Label == "Profiles");
        Assert.Equal("/settings/profiles", profiles.Route);
        var households = Assert.Single(profiles.Children!);
        Assert.Equal("Households", households.Label);
        Assert.Equal("/settings/profiles/households", households.Route);
    }

    [Fact]
    public void NeedsReviewCount_MakesLibraryAndIngestionWaiting_AndRecentlyAddedIsNotInTheMenu()
    {
        var groups = SettingsMenuBuilder.Build(Context(administrator: true) with { ReviewCount = 3 });

        var ingestion = Flatten(groups).Single(row => row.Label == "Library and Ingestion");
        Assert.Equal(MediaSectionAttentionLevel.Waiting, ingestion.Attention);
        Assert.Equal("3", ingestion.Meta);
        Assert.True(ingestion.MetaIsNotification);
        Assert.DoesNotContain(Flatten(groups), row => row.Label == "Recently Added");
        Assert.Equal(MediaSectionAttentionLevel.None, Flatten(groups).Single(row => row.Label == "Tools and requirements").EffectiveAttention);
    }

    [Fact]
    public void LocalAiBlocked_MakesItsRowRedWithOff_AndItsGroupInheritsIt_ButToolsStayUnshaded()
    {
        var groups = SettingsMenuBuilder.Build(Context(administrator: true) with { LocalAiBlocked = true });

        var localAi = Flatten(groups).Single(row => row.Label == "Local AI");
        Assert.Equal(MediaSectionAttentionLevel.Blocked, localAi.Attention);
        Assert.Equal("Off", localAi.AttentionLabel);
        Assert.Equal(MediaSectionAttentionLevel.Blocked, groups.Single(group => group.Label == "Developer").Items.Max(row => row.EffectiveAttention));
        Assert.Equal(MediaSectionAttentionLevel.None, Flatten(groups).Single(row => row.Label == "Tools and requirements").EffectiveAttention);
        Assert.Equal(MediaSectionAttentionLevel.None, Flatten(groups).Single(row => row.Label == "Library and Ingestion").EffectiveAttention);
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
        Assert.DoesNotContain("Provider Tester", labels);
        Assert.DoesNotContain("Local Network", labels);
        Assert.DoesNotContain("Recently Added", labels);
        Assert.Contains("Library and Ingestion", labels);
        Assert.Contains("Playback", labels);
    }

    [Fact]
    public void FeatureSwitchedOff_HidesTheRowAndItsChildren()
    {
        var groups = SettingsMenuBuilder.Build(Context(administrator: true) with
        {
            IsSectionEnabled = section => section != SettingsSection.DevHarness,
        });

        var labels = Flatten(groups).Select(row => row.Label).ToArray();
        Assert.DoesNotContain("Developer Tools", labels);
        Assert.DoesNotContain("Provider Tester", labels);
        Assert.Contains("Plugins", labels);
    }

    [Fact]
    public void PhoneSelector_ListsTheSameTreeIndentedByDepth()
    {
        var groups = SettingsMenuBuilder.Build(Context(administrator: true));
        var selector = SettingsMenuBuilder.BuildPhoneSelector(groups);

        Assert.Equal(groups.Select(group => group.Label), selector.Select(group => group.Label));
        Assert.Equal(
            Flatten(groups).Select(row => row.Route),
            selector.SelectMany(group => group.Options).Select(option => option.Route));

        var server = selector.Single(group => group.Label == "Server");
        Assert.Equal(0, server.Options.Single(option => option.Label == "Library and Ingestion").Depth);
        Assert.Equal(1, server.Options.Single(option => option.Label == "Metadata Providers").Depth);
        Assert.Equal(2, server.Options.Single(option => option.Label == "Ingestion Flow").Depth);
    }

    [Fact]
    public void Shell_DrawsEveryRowWithChevronIconLabelAndBadgeSlots_LeavesIncluded()
    {
        var cut = RenderMenu(SettingsMenuBuilder.Build(Context(administrator: true)), "/settings/profiles");

        var rows = cut.FindAll(".media-section-shell__tree-row");
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            Assert.NotNull(row.QuerySelector(".media-section-shell__tree-chevron-slot"));
            Assert.NotNull(row.QuerySelector(".media-section-shell__tree-icon .tl-icon-root, .media-section-shell__tree-icon svg"));
            Assert.NotNull(row.QuerySelector(".media-section-shell__tree-label"));
            Assert.NotNull(row.QuerySelector(".media-section-shell__tree-badge"));
        }

        var integrations = rows.Single(row => row.QuerySelector(".media-section-shell__tree-label")!.TextContent == "Integrations");
        // A page with no children keeps the slot but leaves it blank.
        Assert.Null(integrations.QuerySelector(".media-section-shell__tree-chevron-slot button"));
        var preferences = rows.Single(row => row.QuerySelector(".media-section-shell__tree-label")!.TextContent == "Preferences");
        Assert.NotNull(preferences.QuerySelector(".media-section-shell__tree-chevron-slot button"));
    }

    [Fact]
    public void Shell_ParentClickOpensItsOwnPage_AndExpandsItsChildren()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = RenderMenu(SettingsMenuBuilder.Build(Context(administrator: true)), "/settings/profiles");

        Assert.Empty(cut.FindAll("a[href='/settings/playback']"));
        cut.Find("a[href='/settings/preferences']").Click();

        Assert.EndsWith("/settings/preferences", navigation.Uri, StringComparison.Ordinal);
        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("a[href='/settings/playback']"));
            var preferencesRow = cut.Find("a[href='/settings/preferences']").ParentElement!;
            Assert.Equal("true", preferencesRow.QuerySelector(".media-section-shell__tree-chevron-slot button[aria-controls$='children']")!.GetAttribute("aria-expanded"));
        });
    }

    [Fact]
    public void Shell_ChildrenIndentOneFixedStep_AndActivePageAndItsParentAreHighlighted()
    {
        var cut = RenderMenu(SettingsMenuBuilder.Build(Context(administrator: true)), "/settings/metadata/ingestion-flow");

        var flow = cut.Find("a[href='/settings/metadata/ingestion-flow']");
        Assert.Equal("page", flow.GetAttribute("aria-current"));
        var flowRow = flow.ParentElement!;
        Assert.Equal("2", flowRow.GetAttribute("data-tree-depth"));
        Assert.Contains("is-active", flowRow.ClassList);

        // The whole path down to the active page is open and the ancestors read as the active branch.
        Assert.Contains("is-branch-active", cut.Find("a[href='/settings/ingestion']").ParentElement!.ClassList);
        Assert.Contains("is-branch-active", cut.Find("a[href='/settings/metadata/providers']").ParentElement!.ClassList);
        Assert.Equal("1", cut.Find("a[href='/settings/libraries']").ParentElement!.GetAttribute("data-tree-depth"));
        Assert.Equal("0", cut.Find("a[href='/settings/ingestion']").ParentElement!.GetAttribute("data-tree-depth"));
    }

    [Fact]
    public void Shell_AccordionKeepsTheOpenPathAndFoldsTheOtherBranches()
    {
        var cut = RenderMenu(SettingsMenuBuilder.Build(Context(administrator: true)), "/settings/metadata/ingestion-flow");

        Assert.NotEmpty(cut.FindAll("a[href='/settings/libraries']"));
        cut.Find("a[href='/settings/ai']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("a[href='/settings/ai/vocabulary']"));
            Assert.Empty(cut.FindAll("a[href='/settings/libraries']"));
        });
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
            .Add(component => component.AccordionNavigation, true)
            .Add(component => component.FixedColumnTree, true)
            .Add(component => component.ShowMobileNavigation, false)
            .AddChildContent("<section>Settings content</section>"));
    }

    private static SettingsMenuContext Context(bool administrator) =>
        new(administrator, HouseholdAdministrator: false, IsMobile: false, ReviewCount: 0, SecurityNeedsAttention: false);

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
