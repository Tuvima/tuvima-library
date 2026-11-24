using Bunit;
using MediaEngine.Web.Components.Navigation;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Configuration;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The top-bar profile menu (profile and household spec D1, D9, D13 to D16): your own row is one button to My profile, then "Switch to"
/// with the other people, then Household (administrators), Settings, Recycle bin and Help, then Sign out when this browser signed in with
/// a password. Needs Review and My activity have left the adult menu, and "Who's watching?" is no longer a row. Each person's avatar
/// sits beside their name in the switcher, and the Secure account card stays out of the menu.
/// </summary>
public sealed partial class UiShellRenderTests
{
    private IRenderedComponent<TopNavAccountMenu> OpenProfileMenu(
        bool canManageProfiles,
        bool isKids = false,
        params ProfileViewModel[] profiles)
    {
        var active = profiles.Length > 0 ? profiles[0] : Profile("Shy");
        var cut = Render<TopNavAccountMenu>(parameters => parameters
            .Add(menu => menu.DisplayName, active.DisplayName)
            .Add(menu => menu.ActiveProfileId, active.Id)
            .Add(menu => menu.Profiles, profiles.Length > 0 ? profiles : new[] { active })
            .Add(menu => menu.CanManageProfiles, canManageProfiles)
            .Add(menu => menu.IsKids, isKids));
        cut.Find(".top-nav-account-menu__trigger").Click();
        return cut;
    }

    private static (string? Href, string Text)[] MenuItems(IRenderedComponent<TopNavAccountMenu> cut) =>
        [.. cut.FindAll("a[role=menuitem]").Select(link => (link.GetAttribute("href"), link.TextContent.Trim()))];

    [Fact]
    public void ProfileMenu_ForAMember_HasItsOwnRowToMyProfile_ButNoHouseholdNeedsReviewOrMyActivity()
    {
        var cut = OpenProfileMenu(canManageProfiles: false);

        var own = cut.Find("[data-testid=profile-menu-own-row]");
        Assert.Equal("/settings/overview", own.GetAttribute("href"));
        Assert.Equal("Shy", own.QuerySelector("strong")!.TextContent.Trim());
        Assert.Equal("Profile and preferences", own.QuerySelector("small")!.TextContent.Trim());
        Assert.NotNull(own.QuerySelector(".top-nav-account-menu__me-avatar svg"));
        Assert.NotNull(own.QuerySelector(".top-nav-account-menu__me-arrow"));

        var items = MenuItems(cut);
        // The separate "Profile" row is gone: the own row does that job.
        Assert.DoesNotContain(items, item => item.Text == "Profile");
        Assert.DoesNotContain(items, item => item.Text == "My activity");
        Assert.DoesNotContain(items, item => item.Href == "/settings/review");
        Assert.DoesNotContain(items, item => item.Text == "Who's watching?");
        Assert.Contains(items, item => item.Href == "/settings" && item.Text == "Settings");
        Assert.DoesNotContain(items, item => item.Text is "Household" or "Manage profiles");
        Assert.DoesNotContain(items, item => item.Text == "Profile settings");
        Assert.DoesNotContain(items, item => item.Href is "/settings/household" or "/settings/profiles");
    }

    [Fact]
    public void ProfileMenu_ForAnAdministrator_AlsoHasHousehold()
    {
        var cut = OpenProfileMenu(canManageProfiles: true);

        var items = MenuItems(cut);
        Assert.Contains(items, item => item == ("/settings/household", "Household"));
        Assert.DoesNotContain(items, item => item.Text == "Manage profiles");
        Assert.DoesNotContain(items, item => item.Href == "/settings/profiles");
    }

    [Fact]
    public void ProfileMenu_ListsOwnRowThenSwitchToThePeopleThenHouseholdSettingsRecycleBinAndHelp()
    {
        var shy = Profile("Shy");
        var sam = new ProfileViewModel(Guid.NewGuid(), "Sam", "#2EC98B", "Profile", DateTimeOffset.UtcNow);
        var cut = OpenProfileMenu(canManageProfiles: true, profiles: [shy, sam]);

        var order = cut.FindAll("[data-testid=profile-menu-own-row], [data-testid=profile-menu-switch-to], .profile-switcher__item, a[role=menuitem]:not([data-testid=profile-menu-own-row])")
            .Select(item => item.GetAttribute("data-testid") == "profile-menu-own-row" ? "own"
                : item.GetAttribute("data-testid") == "profile-menu-switch-to" ? "label:" + item.TextContent.Trim()
                : item.ClassList.Contains("profile-switcher__item") ? "person:" + item.QuerySelector(".profile-switcher__name")!.TextContent.Trim()
                : item.TextContent.Trim())
            .ToArray();

        Assert.Equal(["own", "label:Switch to", "person:Sam", "Household", "Settings", "Recycle bin", "Help"],
            order.Take(7).ToArray());
        Assert.DoesNotContain("person:Shy", order);
        Assert.DoesNotContain("Who's watching?", order);
    }

    [Fact]
    public void ProfileMenu_WithOnlyOneProfile_HasNoSwitchToHeadingOrList()
    {
        var cut = OpenProfileMenu(canManageProfiles: true);

        Assert.Empty(cut.FindAll("[data-testid=profile-menu-switch-to]"));
        Assert.Empty(cut.FindAll(".profile-switcher"));
    }

    [Fact]
    public void ProfileMenu_ForAKidsProfile_HasItsOwnRowToItsSimpleProfilePage_SwitchTo_AndHelpOnly()
    {
        var kid = new ProfileViewModel(Guid.NewGuid(), "Robin", "#2E8BC9", "RestrictedProfile", DateTimeOffset.UtcNow);
        var mom = Profile("Mom");
        var cut = OpenProfileMenu(canManageProfiles: true, isKids: true, profiles: [kid, mom]);

        var items = MenuItems(cut);
        Assert.Equal("/my-profile", cut.Find("[data-testid=profile-menu-own-row]").GetAttribute("href"));
        Assert.DoesNotContain(items, item => item.Href is not null && item.Href.StartsWith("/settings", StringComparison.Ordinal));
        Assert.DoesNotContain(items, item => item.Text is "Household" or "Settings" or "Recycle bin");
        // Own row and Help only (the switcher's people are buttons, not links).
        Assert.Equal(2, items.Length);
        Assert.Equal("Help", items[^1].Text);
        Assert.Equal("Switch to", cut.Find("[data-testid=profile-menu-switch-to]").TextContent.Trim());
        Assert.Single(cut.FindAll(".profile-switcher__item"));
    }

    [Fact]
    public void ProfileMenu_WhenTheEngineCannotBeReached_StillOpensWithTheOwnRowAndHelp_AndLeavesOutSwitchTo()
    {
        // The shell has the signed-in person's name but no household list and no active id.
        var cut = Render<TopNavAccountMenu>(parameters => parameters.Add(menu => menu.DisplayName, "Shy"));
        cut.Find(".top-nav-account-menu__trigger").Click();

        Assert.Equal("Shy", cut.Find("[data-testid=profile-menu-own-row] strong").TextContent.Trim());
        Assert.Contains(MenuItems(cut), item => item.Text == "Help");
        Assert.Empty(cut.FindAll("[data-testid=profile-menu-switch-to]"));
        Assert.Empty(cut.FindAll(".profile-switcher__item"));
    }

    [Fact]
    public void ProfileMenu_ShowsSignOutOnlyWhenThisBrowserSignedInWithAPassword_AndAlignedWithTheOtherRows()
    {
        var withPassword = Render<TopNavAccountMenu>(parameters => parameters.Add(menu => menu.DisplayName, "Shy").Add(menu => menu.ShowSignOut, true));
        withPassword.Find(".top-nav-account-menu__trigger").Click();
        var signOut = withPassword.Find(".top-nav-account-menu__signout");
        Assert.Contains("Sign out", signOut.TextContent, StringComparison.Ordinal);
        Assert.NotNull(signOut.QuerySelector(".top-nav-account-menu__item-icon"));

        var withoutPassword = Render<TopNavAccountMenu>(parameters => parameters.Add(menu => menu.DisplayName, "Shy").Add(menu => menu.ShowSignOut, false));
        withoutPassword.Find(".top-nav-account-menu__trigger").Click();
        Assert.Empty(withoutPassword.FindAll(".top-nav-account-menu__signout"));
        Assert.Empty(withoutPassword.FindAll("form[action='/auth/logout']"));
    }

    [Fact]
    public void TheShellOffersSignOutOnlyWhenNotSignedInWithoutAPassword_InBothTheTopMenuAndThePhoneMenu()
    {
        var layout = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRootForSource(), "src", "MediaEngine.Web", "Shared", "MainLayout.razor"));

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(layout, System.Text.RegularExpressions.Regex.Escape("ShowSignOut=\"@(!_thisComputerOnly)\"")).Count);
    }

    [Fact]
    public void ProfileMenu_GivesPeopleWithTheSameInitialAndSavedColorDifferentCircles()
    {
        var sam = Profile("Sam");
        var sue = Profile("Sue");
        var sid = Profile("Sid");

        var cut = OpenProfileMenu(canManageProfiles: false, profiles: [sam, sue, sid]);

        var own = cut.Find(".top-nav-account-menu__me-avatar circle").GetAttribute("fill");
        var fills = cut.FindAll(".profile-switcher__avatar circle").Select(circle => circle.GetAttribute("fill")).ToArray();
        Assert.Equal(2, fills.Length);
        Assert.Equal(3, new[] { own, fills[0], fills[1] }.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ProfileMenu_OnAPhone_IsAFullWidthSheetWithFortyEightPixelRows_AndNoSidewaysScroll()
    {
        var css = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRootForSource(), "src", "MediaEngine.Web", "Components", "Navigation", "TopNavAccountMenu.razor.css")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var phone = css[css.IndexOf("@media (max-width: 600px)", StringComparison.Ordinal)..];

        // 390 px is inside the 600 px phone range: the panel stops being capped at 18 rem and fills the sheet.
        Assert.Contains(".top-nav-account-menu__panel {\n        width: 100%;\n        max-width: none;", phone, StringComparison.Ordinal);
        Assert.Contains(".top-nav-account-menu__me {\n        min-height: 48px;", phone, StringComparison.Ordinal);
        Assert.Contains(".top-nav-account-menu__panel ::deep .top-nav-account-menu__item,\n    .top-nav-account-menu__panel ::deep .top-nav-account-menu__signout {\n        min-height: 48px;", phone, StringComparison.Ordinal);
        Assert.DoesNotContain("overflow-x: scroll", css, StringComparison.Ordinal);

        // Every row is the full width of the panel: own row, plain rows and Sign out here; the switcher's rows are 100% wide in app.css.
        foreach (var selector in new[] { ".top-nav-account-menu__me {", ".top-nav-account-menu__panel ::deep .top-nav-account-menu__item {", ".top-nav-account-menu__panel ::deep .top-nav-account-menu__signout {" })
        {
            var rule = css[css.IndexOf(selector, StringComparison.Ordinal)..];
            rule = rule[..rule.IndexOf('}', StringComparison.Ordinal)];
            Assert.Contains("width: 100%;", rule, StringComparison.Ordinal);
        }
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
    public void SettingsEntry_CarriesTheNeedsReviewCount_AndTheCountGoesAwayAtZero()
    {
        var withCount = Render<TopNavAccountMenu>(parameters => parameters
            .Add(menu => menu.DisplayName, "Shy")
            .Add(menu => menu.CanManageProfiles, true)
            .Add(menu => menu.ReviewCount, 14));
        withCount.Find(".top-nav-account-menu__trigger").Click();

        var settings = withCount.FindAll("a[role=menuitem]").Single(link => link.GetAttribute("href") == "/settings");
        var badge = settings.QuerySelector("[data-testid=settings-review-count]")!;
        Assert.Equal("14", badge.TextContent.Trim());
        Assert.Equal("14 need review", badge.GetAttribute("aria-label"));

        var none = OpenProfileMenu(canManageProfiles: true);
        Assert.Empty(none.FindAll("[data-testid=settings-review-count]"));
    }

    [Fact]
    public void TheShellGivesTheMenuTheCountOnlyToAdministrators_NeverToAKidsProfile()
    {
        var layout = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRootForSource(), "src", "MediaEngine.Web", "Shared", "MainLayout.razor"));

        Assert.Contains("ReviewCount=\"@(CanViewReview && !_isKids ? _reviewCount : 0)\"", layout, StringComparison.Ordinal);
    }

    private static string RepoRootForSource()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
