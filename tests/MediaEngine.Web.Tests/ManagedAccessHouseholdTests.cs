using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Users page of Users and Sign-in lists sign-ins only. Households, people and PINs are looked after under Profiles, and the
/// page for the old "My household" address shows the same sign-in list.
/// </summary>
public sealed class ManagedAccessHouseholdTests : AsyncBunitContext
{
    private readonly HouseholdHandler _handler = new();

    public ManagedAccessHouseholdTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IHttpClientFactory>(new ClientFactory(_handler));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped<IItsYouConfirmer>(_ => new ConfirmedActionRunnerTests.SpyConfirmer(confirmed: false));
        Services.AddScoped<ConfirmedActionRunner>();
    }

    [Fact]
    public void ServerAdministrator_SeesEverySignInWithItsProfileAndHousehold_AndNoHouseholdsList()
    {
        UseSession(["access.manage"], effectiveAdministrator: true);

        var cut = Render<ManagedAccessUsers>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("admin@home.test", cut.Markup);
            Assert.Contains("sam@home.test", cut.Markup);
        });
        var rows = cut.FindAll(".access-users__row");
        Assert.Equal(["Admin Person", "Household 1"], rows[0].QuerySelectorAll(".access-users__profile, .access-users__household").Select(cell => cell.TextContent.Trim()));
        Assert.Equal(["Sam", "Household 2"], rows[1].QuerySelectorAll(".access-users__profile, .access-users__household").Select(cell => cell.TextContent.Trim()));

        // Households, people, PINs and profile access are looked after under Profiles, so Users carries none of them.
        Assert.Empty(cut.FindAll("[aria-label='Households']"));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() is "View people" or "Add person");
        Assert.DoesNotContain("Invite someone outside your household", cut.Markup);
    }

    [Theory]
    [InlineData("household")]
    [InlineData("users")]
    [InlineData("")]
    public void AccessTabs_HaveNoMyHouseholdTab_AndTheOldHouseholdAddressShowsTheUsersTab(string subsection)
    {
        UseSession(["access.manage"], effectiveAdministrator: true);

        var cut = Render<UsersAccessSettingsTab>(parameters => parameters.Add(component => component.Subsection, subsection));

        Assert.Equal("users", cut.Find(".users-access-settings__section").GetAttribute("data-access-section"));
        Assert.DoesNotContain("My household", cut.Markup);
        cut.WaitForAssertion(() => Assert.Contains("admin@home.test", cut.Markup));
    }

    [Fact]
    public void PlainMember_GetsNoManagementControls()
    {
        UseSession([], effectiveAdministrator: false);

        var cut = Render<ManagedAccessUsers>();

        cut.WaitForAssertion(() => Assert.Contains("administrator access is required", cut.Markup, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() is "Add a sign-in" or "Import a user");
        Assert.DoesNotContain("Sam", cut.Markup);
        Assert.DoesNotContain(_handler.Requests, request => request.StartsWith("/access/", StringComparison.Ordinal));
    }

    [Fact]
    public void ServerAdministrator_StillSetsATemporaryPassword_FromTheUsersList()
    {
        UseSession(["access.manage"], effectiveAdministrator: true);
        var cut = Render<ManagedAccessUsers>();
        cut.WaitForAssertion(() => Assert.Contains("sam@home.test", cut.Markup));

        cut.Find("button[aria-label='Actions for sam@home.test']").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[role='menuitem']"), item => item.TextContent.Trim() == "Set temporary password"));
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Set temporary password").Click();

        cut.WaitForAssertion(() => Assert.Contains("Set a temporary password", cut.Find(".access-drawer__body").TextContent));
    }

    private void UseSession(string[] actions, bool effectiveAdministrator)
    {
        var accountId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var session = new DashboardSessionAccessor();
        session.Set("session", accountId, profileId, Guid.NewGuid(), new DashboardAuthorityResponse(
            accountId, profileId, true, true, 1, 1, effectiveAdministrator, effectiveAdministrator, null, 1, [],
            effectiveAdministrator ? ["settings.administration"] : [], actions));
        Services.AddScoped(_ => session);
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class HouseholdHandler : HttpMessageHandler
    {
        private readonly Guid _household1 = Guid.NewGuid();
        private readonly Guid _household2 = Guid.NewGuid();
        private readonly Guid _adminProfile = Guid.NewGuid();
        private readonly Guid _samProfile = Guid.NewGuid();

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);

            if (request.Method == HttpMethod.Get && path == "/access/users")
            {
                return Task.FromResult(Json(new[]
                {
                    new UserSummaryResponse(Guid.NewGuid(), "admin@home.test", _adminProfile, "Admin Person", _household1, "Household 1", true, false, true, false),
                    new UserSummaryResponse(Guid.NewGuid(), "sam@home.test", _samProfile, "Sam", _household2, "Household 2", true, false, true, false),
                }));
            }

            if (request.Method == HttpMethod.Get && path == "/access/profiles")
            {
                return Task.FromResult(Json(new[]
                {
                    new ManagedProfileResponse(_adminProfile, "Admin Person", "#7C4DFF", null, DateTimeOffset.UtcNow, _household1),
                    new ManagedProfileResponse(_samProfile, "Sam", "#7C4DFF", null, DateTimeOffset.UtcNow, _household2),
                }));
            }

            if (request.Method == HttpMethod.Get && path == "/access/households")
            {
                return Task.FromResult(Json(new[]
                {
                    new HouseholdResponse(_household1, "Household 1", 1, 1, DateTimeOffset.UtcNow),
                    new HouseholdResponse(_household2, "Household 2", 1, 1, DateTimeOffset.UtcNow),
                }));
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
        }

        private static HttpResponseMessage Json<T>(T value) => new(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
