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

/// <summary>
/// After a sign-in no profile is in use yet (users, profiles and households spec D3): the picker shows the sign-in's household as open
/// round pictures, and nothing opens until someone is clicked (there is no "Continue as" button). The sign-in's own profile only gets
/// keyboard focus. There is no "always open as" setting any more.
/// </summary>
public sealed class ProfilePickerPreselectionTests : AsyncBunitContext
{
    private static readonly Guid DadId = Guid.NewGuid();
    private static readonly Guid MomId = Guid.NewGuid();
    private static readonly Guid KimId = Guid.NewGuid();

    private readonly List<(HttpMethod Method, string Path)> _requests = [];
    private readonly List<ProfileViewModel> _profiles = [];
    private DashboardAuthorityResponse? _authority;

    public ProfilePickerPreselectionTests()
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
    public void ForDad_EveryoneIsShown_ThereIsNoContinueAs_AndNothingOpensUntilSomeoneIsClicked()
    {
        var cut = OpenPickerSignedInAs(DadId, Profile(DadId, "Dad"), Profile(MomId, "Mom"), Profile(KimId, "Kim", kids: true));

        Assert.Equal(["Dad", "Mom", "Kim, Kids profile"], CardNames(cut));
        Assert.DoesNotContain("Continue as", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".who-button--primary"));
        Assert.DoesNotContain(_requests, request => request.Method == HttpMethod.Post && request.Path == "/auth/session/switch-profile");
    }

    [Fact]
    public void ForMom_NothingOpensEither_AndThePeopleKeepTheEnginesOrder()
    {
        var cut = OpenPickerSignedInAs(MomId, Profile(DadId, "Dad"), Profile(MomId, "Mom"), Profile(KimId, "Kim", kids: true));

        Assert.Equal(["Dad", "Mom", "Kim, Kids profile"], CardNames(cut));
        Assert.DoesNotContain(_requests, request => request.Path == "/auth/session/switch-profile");
    }

    [Fact]
    public void ClickingAnotherProfile_OpensThatOne_AndOnlyThenTheEngineIsToldWhoWasPicked()
    {
        var cut = OpenPickerSignedInAs(MomId, Profile(DadId, "Dad"), Profile(MomId, "Mom"));

        cut.FindAll(".who-card").Single(card => card.GetAttribute("aria-label") == "Dad").Click();

        cut.WaitForAssertion(() => Assert.Single(_requests, request => request.Method == HttpMethod.Post && request.Path == "/auth/session/switch-profile"));
    }

    [Fact]
    public void AProfileWithAPin_AsksForItsPin_InsteadOfOpening()
    {
        var cut = OpenPickerSignedInAs(DadId, Profile(DadId, "Dad", hasPin: true), Profile(MomId, "Mom"));

        cut.FindAll(".who-card").Single(card => card.GetAttribute("aria-label")!.StartsWith("Dad", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("Enter Dad's PIN", cut.Find(".app-dialog-shell__title").TextContent));
        Assert.DoesNotContain(_requests, request => request.Path == "/auth/session/switch-profile");
    }

    [Fact]
    public void ThePickerOffersNoAlwaysOpenAsChoice_AndNeverAsksTheEngineForOne()
    {
        var cut = OpenPickerSignedInAs(DadId, Profile(DadId, "Dad"), Profile(MomId, "Mom"));

        Assert.DoesNotContain("Always open as", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll("input[type='checkbox']"));
        Assert.DoesNotContain(_requests, request => request.Path.Contains("device-profile", StringComparison.Ordinal));
    }

    [Fact]
    public void ForAHouseholdOfOneWithoutAPin_NothingOpensUntilTheyClickTheirPicture()
    {
        var cut = OpenPickerSignedInAs(DadId, Profile(DadId, "Dad"));

        Assert.Equal(["Dad"], CardNames(cut));
        Assert.DoesNotContain(_requests, request => request.Path == "/auth/session/switch-profile");

        cut.Find(".who-card").Click();

        cut.WaitForAssertion(() => Assert.Single(_requests, request => request.Method == HttpMethod.Post && request.Path == "/auth/session/switch-profile"));
    }

    private IRenderedComponent<ProfilePickerPage> OpenPickerSignedInAs(Guid ownProfile, params ProfileViewModel[] profiles)
    {
        _profiles.AddRange(profiles);
        Setup(ownProfile);
        var cut = Render<ProfilePickerPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".who-card")));

        return cut;
    }

    private static string[] CardNames(IRenderedComponent<ProfilePickerPage> cut) =>
        cut.FindAll(".who-card:not(.who-card--add)").Select(card => card.GetAttribute("aria-label")!).ToArray();

    private static ProfileViewModel Profile(Guid id, string name, bool kids = false, bool hasPin = false) =>
        new(id, name, "#C9922E", kids ? "RestrictedProfile" : "StandardUser", DateTimeOffset.UtcNow, HasPin: hasPin);

    private void Setup(Guid ownProfile)
    {
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(_profiles.ToList()))));

        // A pending session: the authority holds nothing but the household's profiles and "profile.switch".
        var grants = _profiles.Select(person => new AccountProfileGrantDto(person.Id, person.Id, person.DisplayName, null,
            true, true, true, new ProfileAdminProtectionResponse(false, "None", null, 1), 1, DateTimeOffset.UtcNow)).ToList();
        _authority = new DashboardAuthorityResponse(Guid.NewGuid(), ownProfile, true, true, 1, 1, false, false, null, 0, grants, [], ["profile.switch"]);
        var session = new DashboardSessionAccessor();
        session.Set("test-session-token", _authority.AccountId, ownProfile, Guid.NewGuid(), _authority);
        Services.AddScoped(_ => session);
    }

    private sealed class SignedOutProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class EngineHandler(ProfilePickerPreselectionTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (owner._requests)
            {
                owner._requests.Add((request.Method, path));
            }

            if (owner._authority is not { } authority)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (request.Method == HttpMethod.Post && path == "/auth/session/validate")
            {
                return Session(authority, authority.ActiveProfileId);
            }

            if (request.Method == HttpMethod.Post && path == "/auth/session/switch-profile")
            {
                var body = await request.Content!.ReadFromJsonAsync<SwitchProfileRequest>(cancellationToken: cancellationToken);
                return Session(authority, body!.ProfileId);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Session(DashboardAuthorityResponse authority, Guid profileId) =>
            new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SessionValidationResponse
                {
                    SessionId = Guid.NewGuid(),
                    AccountId = authority.AccountId,
                    ActiveProfileId = profileId,
                    Authority = authority,
                }),
            };
    }
}
