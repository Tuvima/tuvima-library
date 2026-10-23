using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The Users page of Users and Sign-in: accounts only. Households, people, PINs and sign-ins live under Profiles.</summary>
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
    public void ServerAdministrator_SeesNoHouseholdsList_InUsers_ButStillInvitesOutsideTheHousehold()
    {
        UseSession(_handler.AdminAccountId, ["access.manage"], effectiveAdministrator: true);

        var cut = Render<ManagedAccessUsers>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Invite someone outside your household", cut.Markup);
            Assert.Contains("admin@home.test", cut.Markup);
        });

        // Households, people, PINs and sign-ins are looked after under Profiles, so Users carries no Households list.
        Assert.DoesNotContain("Households", cut.Markup);
        Assert.DoesNotContain("3 of 8 people", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() is "View people" or "Add person");
        Assert.DoesNotContain("No sign-in of their own", cut.Markup);
    }

    [Theory]
    [InlineData("household")]
    [InlineData("users")]
    [InlineData("")]
    public void AccessTabs_HaveNoMyHouseholdTab_AndTheOldHouseholdAddressShowsTheUsersTab(string subsection)
    {
        UseSession(_handler.AdminAccountId, ["access.manage"], effectiveAdministrator: true);

        var cut = Render<UsersAccessSettingsTab>(parameters => parameters.Add(component => component.Subsection, subsection));

        Assert.Equal("users", cut.Find(".users-access-settings__section").GetAttribute("data-access-section"));
        Assert.DoesNotContain("My household", cut.Markup);
        cut.WaitForAssertion(() => Assert.Contains("admin@home.test", cut.Markup));
        Assert.DoesNotContain("Households", cut.Markup);
    }

    [Fact]
    public void PlainMember_GetsNoManagementControls()
    {
        UseSession(_handler.AdminAccountId, [], effectiveAdministrator: false);

        var cut = Render<ManagedAccessUsers>();

        cut.WaitForAssertion(() => Assert.Contains("administrator access is required", cut.Markup, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Add person");
        Assert.DoesNotContain("Sam", cut.Markup);
        Assert.DoesNotContain(_handler.Requests, request => request.Path.StartsWith("/access/", StringComparison.Ordinal));
    }

    [Fact]
    public void ServerAdministrator_StillSetsATemporaryPassword_FromTheUsersList()
    {
        UseSession(_handler.AdminAccountId, ["access.manage"], effectiveAdministrator: true);
        var cut = Render<ManagedAccessUsers>();
        cut.WaitForAssertion(() => Assert.Contains("sam@home.test", cut.Markup));

        cut.Find("button[aria-label='Actions for sam@home.test']").Click();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[role='menuitem']"), item => item.TextContent.Trim() == "Set temporary password"));
        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Set temporary password").Click();

        cut.WaitForAssertion(() => Assert.Contains("Set a temporary password", cut.Find(".access-drawer__body").TextContent));
    }

    private void UseSession(Guid accountId, string[] actions, bool effectiveAdministrator)
    {
        var profileId = Guid.NewGuid();
        var session = new DashboardSessionAccessor();
        session.Set("session", accountId, profileId, Guid.NewGuid(), new DashboardAuthorityResponse(
            accountId, profileId, true, true, 1, 1, effectiveAdministrator, effectiveAdministrator, null, 1, [],
            effectiveAdministrator ? ["settings.administration"] : [], actions));
        Services.AddScoped(_ => session);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class HouseholdHandler : HttpMessageHandler
    {
        public Guid HouseholdId { get; } = Guid.NewGuid();
        public Guid AdminAccountId { get; } = Guid.NewGuid();
        public Guid PartnerAccountId { get; } = Guid.NewGuid();
        public Guid SamAccountId { get; } = Guid.NewGuid();
        public Guid AdminProfileId { get; } = Guid.NewGuid();
        public Guid KidProfileId { get; } = Guid.NewGuid();
        public Guid SamProfileId { get; } = Guid.NewGuid();
        public AccessLibraryOptionDto HouseholdLibrary { get; } = new(Guid.NewGuid(), "Household Books", "Books", "read");
        public HttpStatusCode PinStatus { get; set; } = HttpStatusCode.NoContent;
        public List<RequestRecord> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method, path, body));

            if (request.Method == HttpMethod.Get && path == "/access/accounts")
            {
                return Json(new[] { Account(AdminAccountId, "admin@home.test", AdminProfileId, householdAdmin: true), Account(PartnerAccountId, "partner@home.test", AdminProfileId), SamAccount() });
            }

            if (request.Method == HttpMethod.Get && path == "/access/profiles")
            {
                return Json(new[]
                {
                    new ManagedProfileResponse(AdminProfileId, "Admin Person", "#7C4DFF", null, DateTimeOffset.UtcNow.AddDays(-3), HouseholdId),
                    new ManagedProfileResponse(KidProfileId, "Kid", "#7C4DFF", null, DateTimeOffset.UtcNow.AddDays(-2), HouseholdId, IsRestricted: true),
                    new ManagedProfileResponse(SamProfileId, "Sam", "#7C4DFF", null, DateTimeOffset.UtcNow.AddDays(-1), HouseholdId),
                });
            }

            if (request.Method == HttpMethod.Get && path == "/access/libraries")
            {
                return Json(new[] { HouseholdLibrary });
            }

            if (request.Method == HttpMethod.Post && path == $"/access/households/{HouseholdId:D}/people")
            {
                var value = JsonSerializer.Deserialize<AddHouseholdPersonRequest>(body, JsonOptions)!;
                return Json(new ManagedProfileResponse(Guid.NewGuid(), value.DisplayName, "#7C4DFF", null, DateTimeOffset.UtcNow, HouseholdId, value.IsChild), HttpStatusCode.Created);
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/own-sign-in", StringComparison.Ordinal))
            {
                return Json(new GiveOwnSignInResponse(Guid.NewGuid(), "kid@home.test", new AccountInvitationResponse(Guid.NewGuid(), "KQ7M4-XH2TA", DateTimeOffset.UtcNow.AddDays(1), null), null));
            }

            if (request.Method == HttpMethod.Put && path.StartsWith("/auth/profiles/", StringComparison.Ordinal) && path.EndsWith("/pin", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(PinStatus);
            }

            if (request.Method == HttpMethod.Post && path == "/access/admin-unlock")
            {
                return Json(new GrantAdminUnlockResponse(true, null, 1));
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private AccountAccessResponse SamAccount() => Account(SamAccountId, "sam@home.test", SamProfileId, inheritsFrom: AdminAccountId);

        private AccountAccessResponse Account(Guid id, string email, Guid profileId, bool householdAdmin = false, Guid? inheritsFrom = null) =>
            new(id, email, true, false, 1,
                [new("read", true), new("watch", true)],
                [],
                [new AccountProfileGrantDto(id, profileId, "Profile", null, true, true, false,
                    new GrantAdminProtectionDto(false, "FixedDuration", 30, 1), 1, DateTimeOffset.UtcNow)],
                DateTimeOffset.UtcNow.AddMonths(-1) + TimeSpan.FromMinutes(id == AdminAccountId ? 0 : 5), DateTimeOffset.UtcNow, null,
                HouseholdId, GrantsInheritFromAccountId: inheritsFrom, HouseholdAdmin: householdAdmin);

        private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = JsonContent.Create(value) };
    }

    private sealed record RequestRecord(HttpMethod Method, string Path, string Body);
}
