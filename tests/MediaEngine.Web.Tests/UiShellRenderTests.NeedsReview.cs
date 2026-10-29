using System.Reflection;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Shared;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Needs Review is for server administrators only (registry-retirement D12), and it no longer sits in the account menu (profile menu
/// spec): the menu in the real layout lists it for nobody, and the administrator reaches it from Settings.
/// </summary>
public sealed partial class UiShellRenderTests
{
    private const string NeedsReviewLink = "a[href='/settings/review']";

    private static DashboardAuthorityResponse ShellAuthority(bool effectiveAdministrator, string[] navigation, string[] actions)
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        return new DashboardAuthorityResponse(
            id, id, true, true, 1, 1, effectiveAdministrator, effectiveAdministrator, null, 1,
            [new AccountProfileGrantDto(id, id, "Test User", null, true, true, effectiveAdministrator,
                new ProfileAdminProtectionResponse(false, "None", null, 1), 1, DateTimeOffset.UtcNow)],
            navigation, actions);
    }

    private IRenderedComponent<MainLayout> RenderOpenAccountMenu(DashboardAuthorityResponse authority, ProfileViewModel profile)
    {
        // Services must all be registered before the first one is resolved.
        Services.AddSingleton<IEngineApiClient>(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel> { profile }))));
        // The layout re-validates the session with the Engine, so both the session and the Engine's answer must say
        // the same thing; the shared fixture's own pair describes a server administrator.
        var session = new DashboardSessionAccessor();
        session.Set("test-session-token", authority.AccountId, authority.ActiveProfileId, Guid.NewGuid(), authority);
        Services.AddScoped(_ => session);
        Services.AddSingleton<IHttpClientFactory>(new SettingsIdentityClientFactory(new DelayedLockHandler(authority, session)));

        var cut = Render<MainLayout>(parameters => parameters
            .Add(layout => layout.Body, builder => builder.AddMarkupContent(0, "<section>Body</section>")));
        // The active profile loads after the first render; wait until the layout has taken it.
        cut.WaitForAssertion(() => Assert.Equal(profile.Id,
            typeof(MainLayout).GetField("_activeProfileId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cut.Instance)));
        cut.Find(".top-nav-account-menu__trigger").Click();
        return cut;
    }

    [Fact]
    public void AccountMenu_NoLongerListsNeedsReview_EvenForAServerAdministrator()
    {
        var cut = RenderOpenAccountMenu(
            ShellAuthority(true, ["settings.administration"], ["access.manage"]), AdultProfile("Shy"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("a[href='/settings']")));
        Assert.Empty(cut.FindAll(NeedsReviewLink));
    }

    [Fact]
    public void AccountMenu_ShowsNoNeedsReviewToAHouseholdAdministrator()
    {
        var cut = RenderOpenAccountMenu(
            ShellAuthority(false, [], ["household.manage"]), AdultProfile("Household admin"));

        Assert.Empty(cut.FindAll(NeedsReviewLink));
        Assert.DoesNotContain("Needs Review", cut.Markup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AccountMenu_ShowsNoNeedsReviewOnAKidsProfile_EvenIfTheSessionStillCarriedAdministratorAccess(bool sessionSaysAdministrator)
    {
        var authority = sessionSaysAdministrator
            ? ShellAuthority(true, ["settings.administration"], ["access.manage"])
            : ShellAuthority(false, [], []);

        var cut = RenderOpenAccountMenu(authority, KidsProfile("Maya"));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".kids-badge")));
        Assert.Empty(cut.FindAll(NeedsReviewLink));
    }
}
