using Bunit;
using MediaEngine.Web.Components.Navigation;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Configuration;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The top-bar profile menu (Settings revamp D26): My profile and My activity replace Profile settings, each person's avatar
/// sits beside their name in the switcher, and the Secure account card stays on top.
/// </summary>
public sealed partial class UiShellRenderTests
{
    private IRenderedComponent<TopNavAccountMenu> OpenProfileMenu(
        bool canManageProfiles,
        bool isKids = false,
        bool secureAccount = false,
        params ProfileViewModel[] profiles)
    {
        var active = profiles.Length > 0 ? profiles[0] : Profile("Shy");
        var cut = Render<TopNavAccountMenu>(parameters => parameters
            .Add(menu => menu.DisplayName, active.DisplayName)
            .Add(menu => menu.ActiveProfileId, active.Id)
            .Add(menu => menu.Profiles, profiles.Length > 0 ? profiles : new[] { active })
            .Add(menu => menu.CanManageProfiles, canManageProfiles)
            .Add(menu => menu.IsKids, isKids)
            .Add(menu => menu.ShowSecureAccount, secureAccount));
        cut.Find(".top-nav-account-menu__trigger").Click();
        return cut;
    }

    private static (string? Href, string Text)[] MenuItems(IRenderedComponent<TopNavAccountMenu> cut) =>
        [.. cut.FindAll("a[role=menuitem]").Select(link => (link.GetAttribute("href"), link.TextContent.Trim()))];

    [Fact]
    public void ProfileMenu_ForAMember_HasMyProfileAndMyActivity_ButNoManageProfiles()
    {
        var cut = OpenProfileMenu(canManageProfiles: false);

        var items = MenuItems(cut);
        Assert.Contains(items, item => item == ("/settings/overview", "My profile"));
        Assert.Contains(items, item => item == ("/settings/overview/activity", "My activity"));
        Assert.Contains(items, item => item.Href == "/settings" && item.Text == "Settings");
        Assert.DoesNotContain(items, item => item.Text == "Manage profiles");
        Assert.DoesNotContain(items, item => item.Text == "Profile settings");
        Assert.DoesNotContain(items, item => item.Href == "/settings/profiles");
    }

    [Fact]
    public void ProfileMenu_ForAnAdministrator_AlsoHasManageProfiles()
    {
        var cut = OpenProfileMenu(canManageProfiles: true);

        var items = MenuItems(cut);
        Assert.Contains(items, item => item == ("/settings/overview", "My profile"));
        Assert.Contains(items, item => item == ("/settings/overview/activity", "My activity"));
        Assert.Contains(items, item => item == ("/settings/profiles", "Manage profiles"));
    }

    [Fact]
    public void ProfileMenu_ForAKidsProfile_HasOnlyMyActivity_OnItsOwnPage()
    {
        var cut = OpenProfileMenu(canManageProfiles: false, isKids: true);

        var items = MenuItems(cut);
        Assert.Contains(items, item => item == ("/my-activity", "My activity"));
        Assert.DoesNotContain(items, item => item.Text == "My profile");
        Assert.DoesNotContain(items, item => item.Href is not null && item.Href.StartsWith("/settings", StringComparison.Ordinal));
    }

    [Fact]
    public void ProfileSwitcher_ShowsEachPersonsAvatarBesideTheirName()
    {
        var shy = Profile("Shy");
        var kid = new ProfileViewModel(Guid.NewGuid(), "Robin", "#2E8BC9", "RestrictedProfile", DateTimeOffset.UtcNow);
        var photo = new ProfileViewModel(Guid.NewGuid(), "Sam", "#2EC98B", "Profile", DateTimeOffset.UtcNow, AvatarImageUrl: "/engine-image/stream/avatar.jpg");

        var cut = OpenProfileMenu(canManageProfiles: false, profiles: [shy, kid, photo]);

        var rows = cut.FindAll(".profile-switcher__item");
        Assert.Equal(2, rows.Count);
        Assert.Equal(["Robin", "Sam"], rows.Select(row => row.QuerySelector(".profile-switcher__name")!.TextContent.Trim()).ToArray());
        Assert.All(rows, row => Assert.NotNull(row.QuerySelector(".profile-switcher__avatar svg, .profile-switcher__avatar img")));
        Assert.Equal("/engine-image/stream/avatar.jpg", rows[1].QuerySelector(".profile-switcher__avatar img")!.GetAttribute("src"));
        Assert.Contains("R", rows[0].QuerySelector(".profile-switcher__avatar svg text")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPage_OverviewApps_SitsUnderPersonalAndOverviewInTheBreadcrumb()
    {
        Services.AddSingleton(new AppStoreLinks());
        Services.GetRequiredService<NavigationManager>().NavigateTo("/settings/overview/apps");

        var cut = Render<Settings>(parameters => parameters
            .Add(page => page.Section, "overview")
            .Add(page => page.Subsection, "apps"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-settings-overview-apps]")));
        var crumbs = cut.FindAll(".settings-breadcrumbs a, .settings-breadcrumbs span").Select(item => item.TextContent.Trim()).ToArray();
        Assert.Equal(["Settings", "Personal", "Overview", "Apps"], crumbs);
        Assert.Equal("/settings/overview", cut.Find(".settings-breadcrumbs a[href='/settings/overview']").GetAttribute("href"));
        Assert.Equal("Apps", cut.Find(".settings-breadcrumbs [aria-current=page]").TextContent.Trim());
    }

    [Fact]
    public void ProfileMenu_KeepsTheSecureAccountCardAboveTheName()
    {
        var cut = OpenProfileMenu(canManageProfiles: false, secureAccount: true);

        var panel = cut.Find(".top-nav-account-menu__panel");
        Assert.True(panel.Children[0].ClassList.Contains("top-nav-account-menu__secure-card"));
        Assert.True(panel.Children[1].ClassList.Contains("top-nav-account-menu__header"));
    }
}
