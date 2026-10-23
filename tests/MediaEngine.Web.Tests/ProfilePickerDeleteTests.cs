using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>"Manage profiles" on the picker offers Delete by the same rules as the profile page: the Engine's roles decide it.</summary>
public sealed class ProfilePickerDeleteTests : AsyncBunitContext
{
    private static readonly Guid HouseholdId = Guid.NewGuid();
    private static readonly Guid OwnId = Guid.NewGuid();
    private static readonly Guid SamId = Guid.NewGuid();
    private static readonly Guid KimId = Guid.NewGuid();

    private readonly List<ManagedProfileResponse> _managed = [];
    private string? _deleteRefusal;
    private string? _addRefusal;

    public ProfilePickerDeleteTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddScoped<UniverseStateContainer>();
        Services.AddScoped<ActiveProfileSessionService>();
        Services.AddScoped<UIOrchestratorService>();
        Services.AddSingleton<IHttpClientFactory>(new ClientFactory(new EngineHandler(this)));
        Services.AddScoped<DashboardIdentityClient>();
        Services.AddScoped<IItsYouConfirmer>(_ => new ConfirmedActionRunnerTests.SpyConfirmer(confirmed: false));
        Services.AddScoped<ConfirmedActionRunner>();
        Services.AddScoped<AuthenticationStateProvider>(_ => new SignedOutProvider());
    }

    [Fact]
    public void Delete_IsOffered_ForAnOrdinaryPerson()
    {
        var cut = OpenEditorFor("Sam", householdAdministrator: true, serverAdministrator: false, [
            Person(OwnId, "Test User", householdAdmin: true), Person(SamId, "Sam"), Person(KimId, "Kim")]);

        Assert.NotEmpty(cut.FindAll(".profile-editor__delete"));
    }

    [Fact]
    public void Delete_IsNotOffered_ForTheLastHouseholdAdministrator()
    {
        var cut = OpenEditorFor("Sam", householdAdministrator: false, serverAdministrator: true, [
            Person(OwnId, "Test User", serverAdmin: true), Person(SamId, "Sam", householdAdmin: true), Person(KimId, "Kim")]);

        Assert.Empty(cut.FindAll(".profile-editor__delete"));
    }

    [Fact]
    public void Delete_IsNotOffered_ToAHouseholdAdministrator_ForAServerAdministratorsProfile()
    {
        var cut = OpenEditorFor("Sam", householdAdministrator: true, serverAdministrator: false, [
            Person(OwnId, "Test User", householdAdmin: true), Person(SamId, "Sam", serverAdmin: true), Person(KimId, "Kim", serverAdmin: true)]);

        Assert.Empty(cut.FindAll(".profile-editor__delete"));
    }

    [Fact]
    public void Delete_IsOffered_ToAServerAdministrator_ForAnotherServerAdministrator_WhileAnotherRemains()
    {
        var cut = OpenEditorFor("Sam", householdAdministrator: false, serverAdministrator: true, [
            Person(OwnId, "Test User", serverAdmin: true, householdAdmin: true), Person(SamId, "Sam", serverAdmin: true), Person(KimId, "Kim")]);

        Assert.NotEmpty(cut.FindAll(".profile-editor__delete"));
    }

    [Fact]
    public void Delete_RefusedByTheEngine_ShowsItsSentenceAsItIs_AndPromisesTheSignInGoesToo()
    {
        _deleteRefusal = "A household always keeps one household administrator.";
        var cut = OpenEditorFor("Sam", householdAdministrator: true, serverAdministrator: false, [
            Person(OwnId, "Test User", householdAdmin: true), Person(SamId, "Sam"), Person(KimId, "Kim")]);

        cut.Find(".profile-editor__delete").Click();
        Assert.Contains("If they have a sign-in, it is removed too.", cut.Find(".profile-editor__confirm").TextContent);
        cut.FindAll(".profile-editor__confirm button").Single(button => button.TextContent.Trim() == "Delete").Click();

        cut.WaitForAssertion(() => Assert.Equal(_deleteRefusal, cut.Find(".who-page__error").TextContent.Trim()));
        Assert.NotEmpty(cut.FindAll("#profile-editor-title"));
    }

    [Fact]
    public void AddingPastEight_ShowsTheEnginesSentenceAsItIs()
    {
        _addRefusal = "A household can have up to 8 people.";
        _managed.AddRange([Person(OwnId, "Test User", householdAdmin: true), Person(SamId, "Sam")]);
        Setup(householdAdministrator: true, serverAdministrator: false);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/who?manage=1");

        var cut = Render<ProfilePickerPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".who-card--add")));
        cut.Find(".who-card--add").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("input.profile-editor__name")));
        cut.Find("input.profile-editor__name").Input("Robin");
        cut.FindAll(".who-button--primary").Single(button => button.TextContent.Trim() == "Add profile").Click();

        cut.WaitForAssertion(() => Assert.Equal(_addRefusal, cut.Find(".who-page__error").TextContent.Trim()));
    }

    private IRenderedComponent<ProfilePickerPage> OpenEditorFor(string name, bool householdAdministrator, bool serverAdministrator, IEnumerable<ManagedProfileResponse> people)
    {
        _managed.AddRange(people);
        Setup(householdAdministrator, serverAdministrator);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/who?manage=1");

        var cut = Render<ProfilePickerPage>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".who-card")));
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".who-card"), card => card.GetAttribute("aria-label")!.StartsWith($"Edit {name}", StringComparison.Ordinal)));
        cut.FindAll(".who-card").First(card => card.GetAttribute("aria-label")!.StartsWith($"Edit {name}", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#profile-editor-title")));
        return cut;
    }

    private void Setup(bool householdAdministrator, bool serverAdministrator)
    {
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ =>
                Task.FromResult(_managed.Select(person => new ProfileViewModel(person.Id, person.DisplayName, "#C9922E", "Profile", DateTimeOffset.UtcNow)).ToList())));
        Services.AddSingleton(api);

        string[] actions = serverAdministrator ? ["access.manage"] : householdAdministrator ? ["household.manage"] : [];
        var grants = _managed.Select(person => new AccountProfileGrantDto(person.Id, person.Id, person.DisplayName, null,
            true, true, true, new GrantAdminProtectionDto(false, "None", null, 1), 1, DateTimeOffset.UtcNow)).ToList();
        var authority = new DashboardAuthorityResponse(Guid.NewGuid(), OwnId, true, true, 1, 1, serverAdministrator, serverAdministrator, null, 1,
            grants, serverAdministrator ? ["settings.administration"] : [], actions);
        var session = new DashboardSessionAccessor();
        session.Set("test-session-token", authority.AccountId, OwnId, Guid.NewGuid(), authority);
        Services.AddScoped(_ => session);
        Authority = authority;
    }

    private DashboardAuthorityResponse? Authority { get; set; }

    private static ManagedProfileResponse Person(Guid id, string name, bool householdAdmin = false, bool serverAdmin = false) =>
        new(id, name, "#C9922E", null, DateTimeOffset.UtcNow.AddMinutes(id == OwnId ? -3 : 0), HouseholdId, false,
            IsServerAdmin: serverAdmin, IsHouseholdAdmin: householdAdmin,
            User: householdAdmin || serverAdmin ? new ManagedProfileUserDto(Guid.NewGuid(), $"{name.Replace(' ', '.')}@home.test") : null);

    private sealed class SignedOutProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class EngineHandler(ProfilePickerDeleteTests owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/access/profiles")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(owner._managed) });
            }

            if (request.Method == HttpMethod.Post && path == "/auth/session/validate" && owner.Authority is { } authority)
            {
                // The same person is still signed in with the same access when the page refreshes its authority.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SessionValidationResponse
                    {
                        SessionId = Guid.NewGuid(),
                        AccountId = authority.AccountId,
                        ActiveProfileId = authority.ActiveProfileId,
                        Authority = authority,
                    }),
                });
            }

            if (request.Method == HttpMethod.Delete && path.StartsWith("/access/profiles/", StringComparison.Ordinal) && owner._deleteRefusal is { } deleteRefusal)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { detail = deleteRefusal }) });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/people", StringComparison.Ordinal) && owner._addRefusal is { } addRefusal)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { detail = addRefusal }) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
