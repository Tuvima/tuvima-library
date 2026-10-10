using Bunit;
using MediaEngine.Web.Components.Navigation;
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
        Assert.Empty(cut.FindAll("a[href='/settings/profile']"));
        Assert.Empty(cut.FindAll("a[href^='/settings/recently-added']"));
        Assert.NotEmpty(cut.FindAll(".kids-badge"));
        Assert.NotEmpty(cut.FindAll(".top-nav-account-menu__signout"));
    }

    [Fact]
    public void AdultProfile_KeepsSettingsLinks_AndNoBadge()
    {
        var cut = RenderOpenMenu(kids: false, canReview: true);

        Assert.NotEmpty(cut.FindAll("a[href='/settings']"));
        Assert.NotEmpty(cut.FindAll("a[href='/settings/profile']"));
        Assert.Empty(cut.FindAll(".kids-badge"));
    }

    [Fact]
    public void MainLayout_TrimsNavigationForKidsOnly()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var layout = File.ReadAllText(Path.Combine(repo, "src", "MediaEngine.Web", "Shared", "MainLayout.razor"));

        Assert.Contains("_isKids = profile.IsRestricted;", layout);
        Assert.Contains("CanViewReview=\"@(CanViewReview && !_isKids)\"", layout);
        Assert.Contains("_kidsNavPaths = [\"/watch\", \"/listen\", \"/read\"]", layout);
        Assert.DoesNotContain("/view\", \"/collections\"", layout.Split("_kidsNavPaths")[1].Split(';')[0]);
    }
}
