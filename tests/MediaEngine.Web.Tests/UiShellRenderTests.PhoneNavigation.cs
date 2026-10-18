using System.Reflection;
using Bunit;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Web.Components.Navigation;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Shared;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The phone navigation shell (form factor phone, under 600 px): a three-tab floating dock, a top bar with a hamburger
/// and the icon-only logo, the section chips under it, and the full-screen menu generated from the desktop catalogue.
/// Tablet, desktop and TV keep what they had.
/// </summary>
public sealed partial class UiShellRenderTests
{
    private static readonly DeviceContextSnapshot PhoneDevice = new("mobile", "phone", true);
    private static readonly DeviceContextSnapshot TvDevice = new("television", "tv", false);

    private async Task<IRenderedComponent<MainLayout>> RenderLayoutAsync(DeviceContextSnapshot? device = null, params ProfileViewModel[] profiles)
    {
        if (profiles.Length > 0)
        {
            Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
                stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(profiles.ToList()))));
        }

        var cut = Render<MainLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<section>Body</section>")));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".tl-appbar")));
        if (device is not null)
        {
            var service = Services.GetRequiredService<DeviceContextService>();
            await cut.InvokeAsync(() => service.SwitchDeviceAsync(device));
        }

        if (profiles.Length > 0)
        {
            // The active profile loads after the first render; wait until the layout has taken it.
            var layoutType = typeof(MainLayout);
            cut.WaitForAssertion(() => Assert.Equal(profiles[0].Id,
                layoutType.GetField("_activeProfileId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cut.Instance)));
            if (profiles.Length > 1)
            {
                // The Dashboard session only grants the first test profile, so the household list is set directly.
                layoutType.GetField("_profiles", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(cut.Instance, profiles.ToList());
                await cut.InvokeAsync(() => cut.Render());
            }
        }

        return cut;
    }

    private static ProfileViewModel KidsProfile(string name) =>
        new(Guid.Parse("00000000-0000-0000-0000-000000000001"), name, "#7c4dff", "RestrictedProfile", DateTimeOffset.UtcNow);

    private static ProfileViewModel AdultProfile(string name, Guid? id = null) =>
        new(id ?? Guid.Parse("00000000-0000-0000-0000-000000000001"), name, "#C9922E", "Administrator", DateTimeOffset.UtcNow);

    private static string[] Hrefs(IEnumerable<AngleSharp.Dom.IElement> links) => links.Select(link => link.GetAttribute("href")!).ToArray();

    private static string[] Labels(IEnumerable<AngleSharp.Dom.IElement> links) => links.Select(link => link.TextContent.Trim()).ToArray();

    private static string[] AccountRows(IRenderedComponent<MainLayout> cut) =>
        cut.FindAll(".phone-nav-menu__nav ul:last-of-type li").Select(item => item.TextContent.Trim()).ToArray();

    [Fact]
    public async Task MainLayout_ShowsThreeTabDockHamburgerChipsAndIconLogoOnPhonesWithNoSearchIcon()
    {
        var cut = await RenderLayoutAsync(PhoneDevice);

        cut.WaitForAssertion(() =>
        {
            // Dock: exactly Home, Search, My Library; My Library opens the personal page.
            var links = cut.FindAll(".layout-shell__intent-dock-link");
            Assert.Equal(["/", "/search", "/my-library"], Hrefs(links));
            Assert.Equal(["Home", "Search", "My Library"], Labels(links));

            // Top bar: hamburger on the left, icon-only logo, one Search (the dock tab), no Downloads in the browser.
            var hamburger = Assert.Single(cut.FindAll(".layout-shell__menu-button"));
            Assert.Equal("Open navigation", hamburger.GetAttribute("aria-label"));
            Assert.Equal("dialog", hamburger.GetAttribute("aria-haspopup"));
            Assert.Equal("false", hamburger.GetAttribute("aria-expanded"));
            Assert.Equal("/images/tuvima-icon.svg", cut.Find(".layout-shell__brand-icon").GetAttribute("src"));
            Assert.Empty(cut.FindAll(".layout-shell__brand-lockup"));
            Assert.Empty(cut.FindAll(".layout-shell__search-action"));
            Assert.Empty(cut.FindAll(".phone-downloads-action"));
            Assert.Single(cut.FindAll(".top-nav-account-menu__trigger"));

            // Chips under the bar: the lanes in the desktop top bar's order.
            Assert.Equal(["Read", "Watch", "Listen", "View", "Collections"], Labels(cut.FindAll(".phone-section-chips__chip")));
        });
    }

    [Theory]
    [InlineData("web", "tablet")]
    [InlineData("web", "desktop")]
    public async Task MainLayout_TabletAndDesktopKeepTheTopNavigationWithNoDockHamburgerOrChips(string deviceClass, string formFactor)
    {
        var cut = await RenderLayoutAsync(new(deviceClass, formFactor, false));

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".layout-shell__intent-dock-link"));
            Assert.Empty(cut.FindAll(".layout-shell__menu-button"));
            Assert.Empty(cut.FindAll(".phone-section-chips"));
            Assert.Empty(cut.FindAll(".phone-nav-menu"));
            Assert.Empty(cut.FindAll(".phone-downloads-action"));
            Assert.Equal("/images/library.svg", cut.Find(".layout-shell__brand-lockup").GetAttribute("src"));
            // The icon logo is rendered only as the compact variant, which CSS shows at 840px and under.
            Assert.Equal("/images/tuvima-icon.svg", cut.Find(".layout-shell__brand-icon-shell--compact .layout-shell__brand-icon").GetAttribute("src"));
            Assert.Single(cut.FindAll(".layout-shell__brand-icon"));
            Assert.Single(cut.FindAll(".layout-shell__search-action"));
            Assert.Equal(["/read", "/watch", "/listen", "/view", "/collections"], Hrefs(cut.FindAll(".layout-shell__nav-link")));
        });
    }

    [Fact]
    public async Task PhoneMenu_ListsExactlyTheDesktopCatalogueForTheSameProfile()
    {
        var cut = await RenderLayoutAsync(PhoneDevice, AdultProfile("Shy"));

        cut.Find(".layout-shell__menu-button").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".phone-nav-menu")));
        var destinations = cut.FindAll(".phone-nav-menu__nav ul:first-of-type a");
        var desktop = cut.FindAll(".layout-shell__nav-link");
        // Home, My Library and Recently added lead; then the very lanes, in the very order, the desktop top bar renders.
        Assert.Equal(["/", "/my-library", "/recently-added", .. Hrefs(desktop)], Hrefs(destinations));
        Assert.Equal(["Home", "My Library", "Recently added", .. Labels(desktop).Select(label => label.ToUpperInvariant())],
            Labels(destinations).Select((label, index) => index < 3 ? label : label.ToUpperInvariant()).ToArray());
        Assert.All(destinations, link => Assert.NotEmpty(link.QuerySelectorAll("svg")));

        // Below the divider: Settings and Sign out (Switch profile only appears with more than one profile).
        Assert.Single(cut.FindAll(".phone-nav-menu__divider"));
        Assert.Equal(["Settings", "Sign out"], AccountRows(cut));
        Assert.Equal("/auth/logout", cut.Find(".phone-nav-menu__signout-form").GetAttribute("action"));
    }

    [Fact]
    public async Task PhoneMenu_OffersSwitchProfileOnlyWithMoreThanOneProfile()
    {
        var cut = await RenderLayoutAsync(PhoneDevice, AdultProfile("Shy"), AdultProfile("Sam", Guid.NewGuid()));

        cut.Find(".layout-shell__menu-button").Click();

        cut.WaitForAssertion(() => Assert.Equal(["Settings", "Switch profile", "Sign out"], AccountRows(cut)));
    }

    [Fact]
    public async Task PhoneMenu_ForAKidsProfileHasTheSameThreeTabsAndOnlyPermittedEntries()
    {
        var cut = await RenderLayoutAsync(PhoneDevice, KidsProfile("Maya"));
        cut.WaitForAssertion(() => Assert.Equal(["Watch", "Listen", "Read"], Labels(cut.FindAll(".phone-section-chips__chip"))));

        var links = cut.FindAll(".layout-shell__intent-dock-link");
        Assert.Equal(["/", "/search", "/my-library"], Hrefs(links));
        Assert.Equal(["Home", "Search", "My Library"], Labels(links));

        cut.Find(".layout-shell__menu-button").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(["Home", "My Library", "Watch", "Listen", "Read"], Labels(cut.FindAll(".phone-nav-menu__nav ul:first-of-type a")));
            Assert.Equal(["Sign out"], AccountRows(cut));
            Assert.DoesNotContain("Settings", cut.Find(".phone-nav-menu").TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("Recently added", cut.Find(".phone-nav-menu").TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PhoneChrome_NeverMentionsDocumentsOrPhotosForAProfileWithDocumentsOff()
    {
        // The active profile may not see Documents: the View scopes say so, exactly as the View shell reads them.
        var scopes = new ViewScopeResolutionDto(
            new ViewResolvedScopeDto(ViewScopeKind.Mine, Guid.Parse("00000000-0000-0000-0000-000000000001"), false),
            [], CanViewDocuments: false);
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetViewScopesAsync), _ => Task.FromResult<ViewScopeResolutionDto?>(scopes))));
        Services.AddSingleton<ViewWorkspaceService>();
        Services.AddScoped<ActiveProfileAccessor>();
        var workspace = Services.GetRequiredService<ViewWorkspaceService>();
        await workspace.InitializeAsync();
        Assert.False(workspace.Scopes!.CanViewDocuments);

        var cut = await RenderLayoutAsync(PhoneDevice);
        cut.Find(".layout-shell__menu-button").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".phone-nav-menu")));
        foreach (var chrome in new[] { ".layout-shell__appbar", ".phone-nav-menu", ".layout-shell__intent-dock" })
        {
            var markup = string.Concat(cut.FindAll(chrome).Select(element => element.OuterHtml));
            Assert.DoesNotContain("Documents", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Photos", markup, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/view/", markup, StringComparison.OrdinalIgnoreCase);
        }

        // View itself is one entry in the menu and one chip.
        Assert.Single(cut.FindAll(".phone-nav-menu a[href='/view']"));
        Assert.Single(cut.FindAll(".phone-section-chips a[href='/view']"));
    }

    [Fact]
    public async Task PhoneMenu_ClosesFromTheCloseButtonEscapeBackAndChoosingAnEntry()
    {
        var cut = await RenderLayoutAsync(PhoneDevice);
        var nav = Services.GetRequiredService<NavigationManager>();

        void Open()
        {
            cut.Find(".layout-shell__menu-button").Click();
            cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".phone-nav-menu")));
            Assert.Equal("true", cut.Find(".layout-shell__menu-button").GetAttribute("aria-expanded"));
        }

        void AssertClosed() => cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".phone-nav-menu"));
            Assert.Equal("false", cut.Find(".layout-shell__menu-button").GetAttribute("aria-expanded"));
        });

        // Close button.
        Open();
        cut.Find(".phone-nav-menu__close").Click();
        AssertClosed();

        // Escape: the dialog frame reports it through the same cancel callback it uses for every dialog.
        Open();
        await cut.InvokeAsync(() => cut.FindComponent<AppDialogFrame>().Instance.RequestCancel());
        AssertClosed();

        // Back: the browser removed the menu's own history entry and tells the menu.
        Open();
        await cut.InvokeAsync(() => cut.FindComponent<PhoneNavigationMenu>().Instance.CloseFromBack());
        AssertClosed();

        // Choosing an entry navigates and closes.
        Open();
        cut.FindAll(".phone-nav-menu__nav ul:first-of-type a")[5].Click();
        AssertClosed();
        Assert.EndsWith("/listen", nav.Uri);
    }

    [Fact]
    public async Task PhoneMenu_LinksToMyLibraryAndTheDockTabIsSelectedThereAndOnItsGrids()
    {
        var cut = await RenderLayoutAsync(PhoneDevice);
        var nav = Services.GetRequiredService<NavigationManager>();

        // The layout keeps re-rendering while its session and profile load, so each click finds its element afresh and
        // retries if a render replaced it between the find and the click.
        cut.WaitForAssertion(() => cut.Find(".layout-shell__menu-button").Click());
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".phone-nav-menu")));
        Assert.Equal("/my-library", cut.FindAll(".phone-nav-menu__nav ul:first-of-type a")[1].GetAttribute("href"));
        cut.WaitForAssertion(() => cut.FindAll(".phone-nav-menu__nav ul:first-of-type a")[1].Click());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".phone-nav-menu")));
        Assert.EndsWith("/my-library", nav.Uri);

        // The dock's My Library tab is the selected one on the page, and stays selected on the personal grids behind See All.
        foreach (var path in new[] { "/my-library", "/for-me?view=liked" })
        {
            await cut.InvokeAsync(() => nav.NavigateTo(path));
            cut.WaitForAssertion(() =>
            {
                var selected = cut.FindAll(".layout-shell__intent-dock-link.is-active");
                Assert.Equal("My Library", Assert.Single(selected).TextContent.Trim());
                Assert.Equal("page", selected[0].GetAttribute("aria-current"));
            });
        }

        await cut.InvokeAsync(() => nav.NavigateTo("/"));
        cut.WaitForAssertion(() => Assert.Equal("Home", Assert.Single(cut.FindAll(".layout-shell__intent-dock-link.is-active")).TextContent.Trim()));
    }

    [Fact]
    public async Task PhoneMenu_IsALabelledModalSheetAndMarksTheCurrentEntry()
    {
        var cut = await RenderLayoutAsync(PhoneDevice);
        await cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo("/watch"));
        cut.Find(".layout-shell__menu-button").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".phone-nav-menu")));
        var dialog = cut.Find("dialog.phone-nav-menu-dialog");
        Assert.Contains("app-native-dialog--fullscreen", dialog.ClassName);
        Assert.Equal("Menu", dialog.GetAttribute("aria-label"));
        Assert.False(string.IsNullOrEmpty(cut.Find(".phone-nav-menu__title").Id));
        Assert.Equal("Close navigation menu", cut.Find(".phone-nav-menu__close").GetAttribute("aria-label"));
        Assert.Equal("Menu", cut.Find(".phone-nav-menu__nav").GetAttribute("aria-label"));
        var current = Assert.Single(cut.FindAll(".phone-nav-menu a[aria-current='page']"));
        Assert.Equal("/watch", current.GetAttribute("href"));
    }

    [Fact]
    public async Task PhoneChips_AreSelectedForTheCurrentSectionAndHaveOneTabStop()
    {
        var cut = await RenderLayoutAsync(PhoneDevice);
        var nav = Services.GetRequiredService<NavigationManager>();

        AngleSharp.Dom.IElement[] Chips() => cut.FindAll(".phone-section-chips__chip").ToArray();

        // Off a section (Home) nothing is selected and the first chip is the one tab stop.
        Assert.All(Chips(), chip => Assert.Null(chip.GetAttribute("aria-current")));
        Assert.Equal(["0", "-1", "-1", "-1", "-1"], Chips().Select(chip => chip.GetAttribute("tabindex")!).ToArray());

        await cut.InvokeAsync(() => nav.NavigateTo("/watch/movies"));
        cut.WaitForAssertion(() =>
        {
            var selected = Assert.Single(Chips(), chip => chip.ClassList.Contains("is-selected"));
            Assert.Equal("Watch", selected.TextContent.Trim());
            Assert.Equal("page", selected.GetAttribute("aria-current"));
            Assert.Equal(["-1", "0", "-1", "-1", "-1"], Chips().Select(chip => chip.GetAttribute("tabindex")!).ToArray());
        });
    }

    [Fact]
    public void PhoneDownloadsAction_ShowsOnlyWhereDownloadsWork()
    {
        // The browser Dashboard ships with the explain-only switch, so there is no icon.
        Assert.Empty(Render<PhoneDownloadsAction>().FindAll("a"));
        Assert.Empty(Render<PhoneDownloadsAction>(p => p.Add(c => c.Mode, DownloadsTabMode.Hidden)).FindAll("a"));

        var native = Render<PhoneDownloadsAction>(p => p.Add(c => c.Mode, DownloadsTabMode.Available));
        var link = Assert.Single(native.FindAll("a.phone-downloads-action"));
        Assert.Equal("/downloads", link.GetAttribute("href"));
        Assert.Equal("Downloads", link.GetAttribute("aria-label"));
    }
}
