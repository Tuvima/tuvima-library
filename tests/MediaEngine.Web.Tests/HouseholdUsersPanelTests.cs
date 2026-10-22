using System.Net;
using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>A household owner deleting the other users of their household, from Profiles.</summary>
public sealed class HouseholdUsersPanelTests : AsyncBunitContext
{
    private readonly PanelHandler _handler = new();

    public HouseholdUsersPanelTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IHttpClientFactory>(new ClientFactory(_handler));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped(_ => OwnerSession(_handler.MeId));
        Services.AddScoped<IItsYouConfirmer>(_ => new ConfirmedActionRunnerTests.SpyConfirmer(confirmed: false));
        Services.AddScoped<ConfirmedActionRunner>();
    }

    [Fact]
    public void OffersDeleteUserForOtherUsers_ButNotForYourselfOrAServerAdministrator()
    {
        var cut = Render<HouseholdUsersPanel>();

        cut.WaitForAssertion(() => Assert.Contains("Users in your household", cut.Markup));
        Assert.NotEmpty(cut.FindAll("button[aria-label='Delete user pat@home.test']"));
        Assert.NotEmpty(cut.FindAll("button[aria-label='Delete user lee@home.test']"));
        Assert.Empty(cut.FindAll("button[aria-label='Delete user admin@home.test']"));
        Assert.DoesNotContain("me@home.test", cut.Markup);
    }

    [Fact]
    public async Task OnAPhone_NothingToDeleteIsOffered()
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await device.InitialiseAsync(new DeviceContextSnapshot("mobile", "phone", true));
        Services.AddScoped(_ => device);

        var cut = Render<HouseholdUsersPanel>();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Path == "/access/accounts"));
        Assert.DoesNotContain("Users in your household", cut.Markup);
        Assert.Empty(cut.FindAll("button[aria-label^='Delete user']"));
    }

    [Fact]
    public void TheConfirmationNamesTheProfilesThatGo_AndDeletingSendsThePhotoChoice()
    {
        _handler.DeletionProfileNames = ["Pat", "Pat (Kids)"];
        var cut = Render<HouseholdUsersPanel>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Delete user pat@home.test']")));

        cut.Find("button[aria-label='Delete user pat@home.test']").Click();

        cut.WaitForAssertion(() => Assert.Contains(
            "Delete pat@home.test? This removes pat@home.test's sign-in and their profiles Pat and Pat (Kids), with their progress, bookmarks and lists. Media files are kept.",
            cut.Markup));
        Assert.True(cut.Find("input[aria-label='Move their photos to Shared']").HasAttribute("checked"));
        cut.Find("input[aria-label='Delete their photos']").Change(true);
        cut.FindAll("[data-user-deletion] button").Single(button => button.TextContent.Trim() == "Delete user").Click();

        cut.WaitForAssertion(() => Assert.Contains("pat@home.test was deleted. Media files were kept.", cut.Markup));
        Assert.Equal($"/access/accounts/{_handler.PatId:D}?photos=delete", Assert.Single(_handler.Deletes));
    }

    [Fact]
    public void AnEngineRefusalIsShownAsItIs_AndNothingIsDeleted()
    {
        _handler.DeleteStatus = HttpStatusCode.Conflict;
        _handler.DeleteDetail = "Lee is the last owner of this household. Make someone else an owner first.";
        var cut = Render<HouseholdUsersPanel>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button[aria-label='Delete user lee@home.test']")));
        cut.Find("button[aria-label='Delete user lee@home.test']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-user-deletion] button")));

        cut.FindAll("[data-user-deletion] button").Single(button => button.TextContent.Trim() == "Delete user").Click();

        cut.WaitForAssertion(() => Assert.Contains("Lee is the last owner of this household. Make someone else an owner first.", cut.Markup));
        Assert.DoesNotContain("was deleted", cut.Markup);
    }

    [Fact]
    public void TheRules_HideYourselfServerAdministratorsAndTheLastOwner()
    {
        var me = Account(Guid.NewGuid(), "me@home.test", owner: true);
        var lee = Account(Guid.NewGuid(), "lee@home.test", owner: true);
        var pat = Account(Guid.NewGuid(), "pat@home.test");
        var admin = Account(Guid.NewGuid(), "admin@home.test", administrator: true);
        var household = new[] { me, lee, pat, admin };

        Assert.False(HouseholdUserRules.CanDelete(me, me.Id, household));
        Assert.True(HouseholdUserRules.CanDelete(lee, me.Id, household));
        Assert.True(HouseholdUserRules.CanDelete(pat, me.Id, household));
        Assert.False(HouseholdUserRules.CanDelete(admin, me.Id, household));
        // With no other enabled owner, the one left cannot be deleted.
        Assert.False(HouseholdUserRules.CanDelete(lee, me.Id, [lee, pat]));
        Assert.True(HouseholdUserRules.CanDelete(pat, me.Id, [lee, pat]));
    }

    private static DashboardSessionAccessor OwnerSession(Guid accountId)
    {
        var profileId = Guid.NewGuid();
        var session = new DashboardSessionAccessor();
        session.Set("session", accountId, profileId, Guid.NewGuid(), new DashboardAuthorityResponse(
            accountId, profileId, true, true, 1, 1, false, false, null, 1, [], [], ["household.manage"]));
        return session;
    }

    private static AccountAccessResponse Account(Guid id, string email, bool owner = false, bool administrator = false) =>
        new(id, email, true, administrator, 1, [], [], [], DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow, null,
            HouseholdId: Guid.Parse("22222222-2222-2222-2222-222222222222"), HouseholdAdmin: owner);

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed record Request(HttpMethod Method, string Path);

    private sealed class PanelHandler : HttpMessageHandler
    {
        public Guid MeId { get; } = Guid.NewGuid();
        public Guid PatId { get; } = Guid.NewGuid();
        public Guid LeeId { get; } = Guid.NewGuid();
        public string[] DeletionProfileNames { get; set; } = ["Pat"];
        public HttpStatusCode DeleteStatus { get; set; } = HttpStatusCode.NoContent;
        public string? DeleteDetail { get; set; }
        public List<Request> Requests { get; } = [];
        public List<string> Deletes { get; } = [];
        private bool _patDeleted;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new(request.Method, path));
            if (request.Method == HttpMethod.Get && path == "/access/accounts")
            {
                var accounts = new List<AccountAccessResponse>
                {
                    Account(MeId, "me@home.test", owner: true),
                    Account(LeeId, "lee@home.test", owner: true),
                    Account(Guid.NewGuid(), "admin@home.test", administrator: true),
                };
                if (!_patDeleted)
                {
                    accounts.Add(Account(PatId, "pat@home.test"));
                }

                return Task.FromResult(Json(accounts));
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/deletion-preview", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(new AccountDeletionPreviewResponse(
                    DeletionProfileNames.Select(name => new AccountDeletionProfileDto(Guid.NewGuid(), name)).ToList())));
            }

            if (request.Method == HttpMethod.Delete)
            {
                Deletes.Add(path + request.RequestUri.Query);
                if (DeleteStatus != HttpStatusCode.NoContent)
                {
                    return Task.FromResult(new HttpResponseMessage(DeleteStatus) { Content = JsonContent.Create(new { detail = DeleteDetail }) });
                }

                _patDeleted = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
