using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The My household and Households views of Users &amp; Access: who sees what, and which Engine actions they call.</summary>
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
    public void HouseholdAdministrator_SeesOnlyTheirHouseholdPeople_AndNoServerActions()
    {
        UseSession(_handler.AdminAccountId, ["household.manage"], effectiveAdministrator: false);

        var cut = RenderHousehold();

        Assert.Contains("Sam", cut.Markup);
        Assert.Contains("Kid", cut.Markup);
        Assert.Contains("Child", cut.Markup);
        Assert.Contains("sam@home.test", cut.Markup);
        Assert.Contains("No sign-in of their own", cut.Markup);
        Assert.Contains(cut.FindAll("button"), button => button.TextContent.Trim() == "Add person");
        Assert.DoesNotContain("Invite someone outside your household", cut.Markup);
        Assert.DoesNotContain("Households", cut.Markup);
        Assert.DoesNotContain("Invite user", cut.Markup);
    }

    [Fact]
    public void ServerAdministrator_SeesTheirOwnHouseholdUnderMyHousehold_AndHouseholdsUnderTheServerView()
    {
        UseSession(_handler.AdminAccountId, ["access.manage"], effectiveAdministrator: true);

        var household = RenderHousehold();
        Assert.Contains("Sam", household.Markup);
        Assert.DoesNotContain("Invite someone outside your household", household.Markup);

        var server = Render<ManagedAccessUsers>(parameters => parameters.Add(component => component.Scope, "server"));
        server.WaitForAssertion(() =>
        {
            Assert.Contains("Invite someone outside your household", server.Markup);
            Assert.Contains("admin@home.test's household", server.Markup);
            Assert.Contains("3 of 8 people", server.Markup);
            Assert.DoesNotContain("No sign-in of their own", server.Markup);
        });

        server.FindAll("button").Single(button => button.TextContent.Trim() == "View people").Click();
        server.WaitForAssertion(() => Assert.Contains("No sign-in of their own", server.Markup));
    }

    [Theory]
    [InlineData("household")]
    [InlineData("server")]
    public void PlainMember_GetsNoManagementControls(string scope)
    {
        UseSession(_handler.AdminAccountId, [], effectiveAdministrator: false);

        var cut = Render<ManagedAccessUsers>(parameters => parameters.Add(component => component.Scope, scope));

        cut.WaitForAssertion(() => Assert.Contains("administrator access is required", cut.Markup, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(cut.FindAll("button"), button => button.TextContent.Trim() == "Add person");
        Assert.DoesNotContain("Sam", cut.Markup);
        Assert.DoesNotContain(_handler.Requests, request => request.Path.StartsWith("/access/", StringComparison.Ordinal));
    }

    [Fact]
    public void AddPerson_PostsToTheHousehold_AsAChildWhenAsked()
    {
        UseSession(_handler.AdminAccountId, ["household.manage"], effectiveAdministrator: false);
        var cut = RenderHousehold();

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Add person").Click();
        cut.WaitForAssertion(() => Assert.Contains("access-drawer__body", cut.Markup));
        cut.Find(".access-drawer__body input[type='text']").Input("Robin");
        cut.Find(".access-drawer__body input[type='checkbox']").Change(true);
        cut.FindAll(".access-drawer__footer button").Single(button => button.TextContent.Trim() == "Add person").Click();

        cut.WaitForAssertion(() => Assert.Contains("Robin was added to the household.", cut.Markup));
        var request = Assert.Single(_handler.Requests, request => request.Method == HttpMethod.Post && request.Path == $"/access/households/{_handler.HouseholdId:D}/people");
        var payload = JsonSerializer.Deserialize<AddHouseholdPersonRequest>(request.Body, JsonOptions)!;
        Assert.Equal("Robin", payload.DisplayName);
        Assert.True(payload.IsChild);
    }

    [Fact]
    public void SetPin_SendsTheNewPinForThatPerson()
    {
        UseSession(_handler.AdminAccountId, ["household.manage"], effectiveAdministrator: false);
        var cut = RenderHousehold();

        cut.Find("button[aria-label='Set PIN for Sam']").Click();
        cut.WaitForAssertion(() => Assert.Contains("PIN for Sam", cut.Markup));
        cut.Find(".access-drawer__body input[type='password']").Input("2468");
        cut.FindAll(".access-drawer__footer button").Single(button => button.TextContent.Trim() == "Save PIN").Click();

        cut.WaitForAssertion(() => Assert.Contains("Sam's PIN was saved.", cut.Markup));
        var request = Assert.Single(_handler.Requests, request => request.Method == HttpMethod.Put && request.Path == $"/auth/profiles/{_handler.SamProfileId:D}/pin");
        Assert.Equal("2468", JsonSerializer.Deserialize<SetProfilePinRequest>(request.Body, JsonOptions)!.Pin);
    }

    [Fact]
    public void GiveAndRemoveOwnSignIn_CallTheMatchingEngineActions()
    {
        UseSession(_handler.AdminAccountId, ["household.manage"], effectiveAdministrator: false);
        var cut = RenderHousehold();

        cut.Find("button[aria-label='Give Kid their own sign-in']").Click();
        cut.WaitForAssertion(() => Assert.Contains("access-drawer__body", cut.Markup));
        cut.Find(".access-drawer__body input[type='text']").Input("kid@home.test");
        cut.FindAll(".access-drawer__footer button").Single(button => button.TextContent.Trim() == "Create sign-in").Click();
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Method == HttpMethod.Post && request.Path == $"/access/profiles/{_handler.KidProfileId:D}/own-sign-in"));

        cut.Find("button[aria-label='Close user drawer']").Click();
        cut.Find("button[aria-label='Remove the sign-in of Sam']").Click();
        cut.WaitForAssertion(() => Assert.Contains("Remove sam@home.test?", cut.Markup));
        cut.FindAll(".access-drawer__footer button").Single(button => button.TextContent.Trim() == "Remove sign-in").Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Method == HttpMethod.Delete && request.Path == $"/access/accounts/{_handler.SamAccountId:D}/own-sign-in"));
    }

    [Fact]
    public void LibrariesAndLanes_OffersOnlyTheLibrariesTheEngineReturnsForTheHousehold()
    {
        UseSession(_handler.AdminAccountId, ["household.manage"], effectiveAdministrator: false);
        var cut = RenderHousehold();

        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Libraries & lanes").Click();
        cut.WaitForAssertion(() => Assert.Contains("access-drawer__body", cut.Markup));

        Assert.Contains("Household Books", cut.Find(".access-drawer__body").TextContent);
        Assert.DoesNotContain("Server Only Movies", cut.Markup);
        Assert.Contains(_handler.Requests, request => request.Method == HttpMethod.Get && request.Path == "/access/libraries");

        cut.FindAll(".access-drawer__footer button").Single(button => button.TextContent.Trim() == "Save permissions").Click();
        cut.WaitForAssertion(() => Assert.Contains("were saved.", cut.Markup));
        var request = Assert.Single(_handler.Requests, request => request.Method == HttpMethod.Put && request.Path == $"/access/accounts/{_handler.PartnerAccountId:D}/access");
        var payload = JsonSerializer.Deserialize<ReplaceAccountAccessRequest>(request.Body, JsonOptions)!;
        Assert.Equal([_handler.HouseholdLibrary.Id], payload.LibraryIds);
    }

    [Fact]
    public void AdministratorPin_IsAskedForWhenTheEngineRefusesTheChange()
    {
        _handler.PinStatus = HttpStatusCode.Forbidden;
        UseSession(_handler.AdminAccountId, ["household.manage"], effectiveAdministrator: false);
        var cut = RenderHousehold();

        cut.Find("button[aria-label='Set PIN for Sam']").Click();
        cut.WaitForAssertion(() => Assert.Contains("PIN for Sam", cut.Markup));
        cut.Find(".access-drawer__body input[type='password']").Input("2468");
        cut.FindAll(".access-drawer__footer button").Single(button => button.TextContent.Trim() == "Save PIN").Click();

        cut.WaitForAssertion(() => Assert.Contains("Your administrator PIN is needed for this change.", cut.Markup));
        cut.FindAll(".access-drawer__body input[type='password']").First().Input("1357");
        cut.FindAll(".access-drawer__body button").Single(button => button.TextContent.Trim() == "Unlock").Click();

        cut.WaitForAssertion(() => Assert.Contains("Unlocked. Try the change again.", cut.Markup));
        var unlock = Assert.Single(_handler.Requests, request => request.Method == HttpMethod.Post && request.Path == "/access/admin-unlock");
        Assert.Equal("1357", JsonSerializer.Deserialize<GrantAdminUnlockRequest>(unlock.Body, JsonOptions)!.Pin);
    }

    private IRenderedComponent<ManagedAccessUsers> RenderHousehold()
    {
        var cut = Render<ManagedAccessUsers>(parameters => parameters.Add(component => component.Scope, "household"));
        cut.WaitForAssertion(() => Assert.Contains("Sam", cut.Markup));
        return cut;
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
                return Json(new[] { Account(AdminAccountId, "admin@home.test", AdminProfileId, householdAdmin: true), Account(PartnerAccountId, "partner@home.test", AdminProfileId), SamAccount() });
            if (request.Method == HttpMethod.Get && path == "/access/profiles")
                return Json(new[]
                {
                    new ManagedProfileResponse(AdminProfileId, "Admin Person", "#7C4DFF", null, DateTimeOffset.UtcNow.AddDays(-3), HouseholdId),
                    new ManagedProfileResponse(KidProfileId, "Kid", "#7C4DFF", null, DateTimeOffset.UtcNow.AddDays(-2), HouseholdId, IsRestricted: true),
                    new ManagedProfileResponse(SamProfileId, "Sam", "#7C4DFF", null, DateTimeOffset.UtcNow.AddDays(-1), HouseholdId),
                });
            if (request.Method == HttpMethod.Get && path == "/access/libraries")
                return Json(new[] { HouseholdLibrary });
            if (request.Method == HttpMethod.Post && path == $"/access/households/{HouseholdId:D}/people")
            {
                var value = JsonSerializer.Deserialize<AddHouseholdPersonRequest>(body, JsonOptions)!;
                return Json(new ManagedProfileResponse(Guid.NewGuid(), value.DisplayName, "#7C4DFF", null, DateTimeOffset.UtcNow, HouseholdId, value.IsChild), HttpStatusCode.Created);
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/own-sign-in", StringComparison.Ordinal))
                return Json(new GiveOwnSignInResponse(Guid.NewGuid(), "kid@home.test", new AccountInvitationResponse(Guid.NewGuid(), "KQ7M4-XH2TA", DateTimeOffset.UtcNow.AddDays(1), null), null));
            if (request.Method == HttpMethod.Put && path.StartsWith("/auth/profiles/", StringComparison.Ordinal) && path.EndsWith("/pin", StringComparison.Ordinal))
                return new HttpResponseMessage(PinStatus);
            if (request.Method == HttpMethod.Post && path == "/access/admin-unlock")
                return Json(new GrantAdminUnlockResponse(true, null, 1));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private AccountAccessResponse SamAccount() => Account(SamAccountId, "sam@home.test", SamProfileId, inheritsFrom: AdminAccountId);

        private AccountAccessResponse Account(Guid id, string email, Guid profileId, bool householdAdmin = false, Guid? inheritsFrom = null) =>
            new(id, email, true, false, 1,
                [new("read", true), new("watch", true)],
                [new(HouseholdLibrary.Id, HouseholdLibrary.DisplayName, true)],
                [new AccountProfileGrantDto(id, profileId, "Profile", null, true, true, false,
                    new GrantAdminProtectionDto(false, "FixedDuration", 30, 1, false, null), 1, DateTimeOffset.UtcNow)],
                DateTimeOffset.UtcNow.AddMonths(-1) + TimeSpan.FromMinutes(id == AdminAccountId ? 0 : 5), DateTimeOffset.UtcNow, null,
                HouseholdId, GrantsInheritFromAccountId: inheritsFrom, HouseholdAdmin: householdAdmin);

        private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = JsonContent.Create(value) };
    }

    private sealed record RequestRecord(HttpMethod Method, string Path, string Body);
}
