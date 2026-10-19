using Bunit;
using MediaEngine.Web.Components.Navigation;
using MediaEngine.Web.Services.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The Kids shell: no settings or review links in the account menu, plus a "Kids" badge.</summary>
public sealed class KidsShellTests : AsyncBunitContext
{
    private IRenderedComponent<TopNavAccountMenu> RenderOpenMenu(bool kids, bool canReview)
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<TopNavAccountMenu>(parameters => parameters
            .Add(p => p.DisplayName, "Maya")
            .Add(p => p.IsKids, kids)
            .Add(p => p.CanViewReview, canReview)
            .Add(p => p.ShowSignOut, true));
        cut.Find(".top-nav-account-menu__trigger").Click();
        return cut;
    }

    [Fact]
    public void KidsProfile_HasNoSettingsOrReviewLinks_AndShowsBadge()
    {
        var cut = RenderOpenMenu(kids: true, canReview: false);

        Assert.Empty(cut.FindAll("a[href='/settings']"));
        Assert.Empty(cut.FindAll("a[href='/settings/profiles']"));
        Assert.Empty(cut.FindAll("a[href^='/settings/overview']"));
        Assert.Empty(cut.FindAll("a[href^='/settings/recently-added']"));
        Assert.NotEmpty(cut.FindAll(".kids-badge"));
        Assert.NotEmpty(cut.FindAll(".top-nav-account-menu__signout"));
    }

    [Fact]
    public void KidsProfile_HasMyActivityOnItsOwnPage_AndNoMyProfile()
    {
        var cut = RenderOpenMenu(kids: true, canReview: false);

        var items = cut.FindAll("a[role=menuitem]").Select(link => (Href: link.GetAttribute("href"), Text: link.TextContent.Trim())).ToList();
        Assert.Contains(items, item => item.Href == "/my-activity" && item.Text == "My activity");
        Assert.DoesNotContain(items, item => item.Text == "My profile");
    }

    [Fact]
    public void AdultProfile_KeepsSettingsLinks_AndNoBadge()
    {
        var cut = RenderOpenMenu(kids: false, canReview: true);

        Assert.NotEmpty(cut.FindAll("a[href='/settings']"));
        Assert.NotEmpty(cut.FindAll("a[href='/settings/overview']"));
        Assert.Empty(cut.FindAll("a[href='/settings/profiles']"));
        Assert.Empty(cut.FindAll(".kids-badge"));
    }

    [Fact]
    public void AdultProfile_HasMyProfileAndMyActivity_InsteadOfProfileSettings()
    {
        var cut = RenderOpenMenu(kids: false, canReview: false);

        var items = cut.FindAll("a[role=menuitem]").Select(link => (Href: link.GetAttribute("href"), Text: link.TextContent.Trim())).ToList();
        Assert.Contains(items, item => item.Href == "/settings/overview" && item.Text == "My profile");
        Assert.Contains(items, item => item.Href == "/settings/overview/activity" && item.Text == "My activity");
        Assert.DoesNotContain(items, item => item.Text == "Profile settings");
        Assert.DoesNotContain(items, item => item.Text == "Manage profiles");
    }

    [Fact]
    public void MainLayout_TrimsNavigationForKidsOnly()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var layout = File.ReadAllText(Path.Combine(repo, "src", "MediaEngine.Web", "Shared", "MainLayout.razor"));

        Assert.Contains("_isKids = profile.IsRestricted;", layout);
        Assert.Contains("CanViewReview=\"@(CanViewReview && !_isKids)\"", layout);
        // The top bar's links, and so the phone menu and chips, come from the one catalogue; Kids keep Watch, Listen and Read.
        Assert.Contains("ShellNavigation.TopBarLinks(_isKids)", layout);
        Assert.Equal(["/watch", "/listen", "/read"], ShellNavigation.TopBarLinks(isKids: true).Select(link => link.Path).ToArray());
        Assert.DoesNotContain(ShellNavigation.TopBarLinks(isKids: true), link => link.Path is "/view" or "/collections");
    }
}
