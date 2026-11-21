using System.Net;
using System.Net.Http.Json;
using Bunit;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Settings;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// "Share with friends" (spec remote-access WP5b, D13 and D15): the service asks the Engine's existing invitation system for a one-time
/// invitation with a password sign-in and a plain member profile, sets the household's libraries before the invitation exists, and
/// shows the result as a link and a QR. The Engine decides who may do any of it; these tests watch what the Dashboard asks for.
/// </summary>
public sealed class FriendInviteTests : AsyncBunitContext
{
    private static readonly Guid FriendsHousehold = Guid.NewGuid();
    private static readonly Guid MoviesLibrary = Guid.NewGuid();
    private static readonly Guid KidsLibrary = Guid.NewGuid();

    public FriendInviteTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
    }

    [Fact]
    public async Task ForANewHousehold_TheLibrariesAreSetBeforeTheInvitationExists_AndTheInvitationIsAPasswordOneWithAPlainMemberProfile()
    {
        var engine = new SpyEngine();
        var service = Service(engine, canInvite: true);

        var result = await service.CreateAsync(new FriendInviteRequest("Sam", "sam@example.com", null, "Friends", [KidsLibrary]));

        Assert.True(result.Succeeded);
        Assert.Equal(["POST /access/households", "PUT /access/households/" + FriendsHousehold.ToString("D") + "/libraries", "POST /access/users"],
            engine.Calls.Select(call => call.Key).ToArray());
        var userBody = engine.Calls[^1].Body;
        Assert.Contains("\"method\":\"invitation\"", userBody, StringComparison.Ordinal);
        Assert.Contains("\"is_kids\":false", userBody, StringComparison.Ordinal);
        Assert.Contains($"\"household_id\":\"{FriendsHousehold:D}\"", userBody, StringComparison.Ordinal);
        Assert.DoesNotContain("temporary_password", userBody.Replace("\"temporary_password\":null", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains(KidsLibrary.ToString("D"), engine.Calls[1].Body, StringComparison.Ordinal);
        Assert.Equal("https://library.example.com/auth/invite?code=ABCDE-FGHIJ", result.Link);
        Assert.NotNull(result.ExpiresAt);
    }

    [Fact]
    public async Task ForAnExistingHousehold_NoHouseholdIsMadeAndItsLibrariesAreLeftAlone()
    {
        var engine = new SpyEngine();
        var service = Service(engine, canInvite: true);

        var result = await service.CreateAsync(new FriendInviteRequest("Sam", "sam@example.com", FriendsHousehold, null, [KidsLibrary]));

        Assert.True(result.Succeeded);
        Assert.Equal(["POST /access/users"], engine.Calls.Select(call => call.Key).ToArray());
    }

    [Fact]
    public async Task WhenTheLibrariesCannotBeSet_NoInvitationIsMade()
    {
        var engine = new SpyEngine { RefuseLibraries = true };
        var service = Service(engine, canInvite: true);

        var result = await service.CreateAsync(new FriendInviteRequest("Sam", "sam@example.com", null, "Friends", [KidsLibrary]));

        Assert.False(result.Succeeded);
        Assert.Null(result.Link);
        Assert.DoesNotContain(engine.Calls, call => call.Key == "POST /access/users");
    }

    [Fact]
    public async Task WithoutTheRightToAddSignIns_NothingIsAsked_AndThereIsNoLink()
    {
        var engine = new SpyEngine();
        var service = Service(engine, canInvite: false);

        var result = await service.CreateAsync(new FriendInviteRequest("Sam", "sam@example.com", FriendsHousehold, null, []));

        Assert.False(service.CanInvite);
        Assert.False(result.Succeeded);
        Assert.Null(result.Link);
        Assert.Empty(engine.Calls);
    }

    [Fact]
    public async Task WithoutAnAwayFromHomeAddress_NoInvitationIsMade()
    {
        var engine = new SpyEngine();
        var service = Service(engine, canInvite: true, awayAddress: null);

        var result = await service.CreateAsync(new FriendInviteRequest("Sam", "sam@example.com", FriendsHousehold, null, []));

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(engine.Calls, call => call.Key == "POST /access/users");
    }

    [Fact]
    public void TheCard_IsNotDrawnForSomeoneWhoCannotAddSignIns()
    {
        Services.AddSingleton<IFriendInviteService>(new FriendInviteServiceSpy { CanInvite = false });

        var cut = Render<RemoteAccessInviteCard>();

        Assert.Empty(cut.FindAll("[data-remote-card='invite']"));
    }

    [Fact]
    public void WithoutAnAwayAddress_TheCardSaysToSetUpAWayInFirst_AndOffersNoButton()
    {
        Services.AddSingleton<IFriendInviteService>(new FriendInviteServiceSpy { CanInvite = true });

        var cut = Render<RemoteAccessInviteCard>();

        Assert.Contains("Set up a way in from outside first", cut.Find("[data-remote-card='invite']").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-remote-action='invite-open']"));
    }

    [Fact]
    public void CreatingAnInvitation_ShowsTheLinkAndAQr_WithNewHouseholdLibrariesTickedByDefault_AndUntickedOnesSentAsHidden()
    {
        var spy = new FriendInviteServiceSpy
        {
            CanInvite = true,
            Options = new FriendInviteOptions("https://library.example.com", [],
                [new AccessLibraryOptionDto(MoviesLibrary, "Movies", null, "watch"), new AccessLibraryOptionDto(KidsLibrary, "Kids films", null, "watch")], false),
        };
        Services.AddSingleton<IFriendInviteService>(spy);
        var cut = Render<RemoteAccessInviteCard>();

        cut.Find("[data-remote-action='invite-open']").Click();
        cut.Find("[data-invite-field='name'] input, input[data-invite-field='name']").Input("Sam");
        cut.Find("[data-invite-field='email'] input, input[data-invite-field='email']").Input("sam@example.com");
        Assert.Equal(2, cut.FindAll("[data-invite-libraries] input[type='checkbox']").Count);
        Assert.All(cut.FindAll("[data-invite-libraries] input[type='checkbox']"), box => Assert.True(box.HasAttribute("checked")));
        cut.Find($"[data-library-id='{KidsLibrary}'] input, input[data-library-id='{KidsLibrary}']").Change(false);
        cut.Find("[data-remote-action='invite-create']").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-invite-result]")));
        var request = Assert.Single(spy.Requests);
        Assert.Null(request.HouseholdId);
        Assert.Equal([KidsLibrary], request.HiddenLibraryIds.ToArray());
        var shown = cut.Find("[data-invite-result]");
        Assert.Contains("https://library.example.com/auth/invite?code=ABCDE-FGHIJ", shown.TextContent, StringComparison.Ordinal);
        Assert.Contains("<svg", shown.QuerySelector(".invitation-qr")!.InnerHtml, StringComparison.Ordinal);
        Assert.StartsWith("mailto:sam%40example.com", cut.Find("[data-remote-action='invite-email']").GetAttribute("href"), StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheEngineRefuses_TheCardSaysWhy_AndShowsNoLink()
    {
        var spy = new FriendInviteServiceSpy
        {
            CanInvite = true,
            Options = new FriendInviteOptions("https://library.example.com", [new HouseholdResponse(FriendsHousehold, "Friends", 0, 0, DateTimeOffset.UtcNow)], [], false),
            Answer = _ => FriendInviteResult.Failed("That email already has a sign-in."),
        };
        Services.AddSingleton<IFriendInviteService>(spy);
        var cut = Render<RemoteAccessInviteCard>();
        cut.Find("[data-remote-action='invite-open']").Click();
        cut.Find("[data-invite-field='name'] input, input[data-invite-field='name']").Input("Sam");
        cut.Find("[data-invite-field='email'] input, input[data-invite-field='email']").Input("sam@example.com");

        cut.Find("[data-remote-action='invite-create']").Click();

        cut.WaitForAssertion(() => Assert.Contains("already has a sign-in", cut.Find("[data-invite-error]").TextContent, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-invite-result]"));
        Assert.Equal(FriendsHousehold, Assert.Single(spy.Requests).HouseholdId);
    }

    private static FriendInviteService Service(SpyEngine engine, bool canInvite, string? awayAddress = "https://library.example.com")
    {
        var http = new TestFactory(engine);
        var session = new DashboardSessionAccessor();
        var account = Guid.NewGuid();
        var profile = Guid.NewGuid();
        session.Set("token", account, profile, Guid.NewGuid(), new DashboardAuthorityResponse(
            account, profile, true, true, 1, 1, true, true, null, 1, [], ["settings.administration"], canInvite ? ["access.manage"] : []));
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetNetworkAppAddressesAsync), _ =>
            Task.FromResult<NetworkAppAddressesDto?>(new NetworkAppAddressesDto { AwayFromHome = awayAddress })));
        return new FriendInviteService(new DashboardIdentityClient(http), new ConfirmedActionRunner(new ConfirmedActionRunnerTests.SpyConfirmer(false)), session, api);
    }

    private sealed class TestFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    /// <summary>A hand-written Engine: answers the few access routes the invitation uses and records each call in order.</summary>
    private sealed class SpyEngine : HttpMessageHandler
    {
        public List<(string Key, string Body)> Calls { get; } = [];

        public bool RefuseLibraries { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            var read = request.Method == HttpMethod.Get;
            if (!read)
            {
                Calls.Add(($"{request.Method} {path}", body));
            }

            return (request.Method.Method, path) switch
            {
                ("GET", "/access/households") => Json(new List<HouseholdResponse> { new(FriendsHousehold, "Friends", 0, 0, DateTimeOffset.UtcNow) }),
                ("GET", "/access/libraries") => Json(new List<AccessLibraryOptionDto> { new(MoviesLibrary, "Movies", null, "watch"), new(KidsLibrary, "Kids films", null, "watch") }),
                ("POST", "/access/households") => Json(new HouseholdResponse(FriendsHousehold, "Friends", 0, 0, DateTimeOffset.UtcNow)),
                ("PUT", _) when path.EndsWith("/libraries", StringComparison.Ordinal) => RefuseLibraries
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                    : Json(new HouseholdLibraryAccessResponse(FriendsHousehold, [KidsLibrary])),
                ("POST", "/access/users") => Json(new CreateUserResponse(
                    new UserSummaryResponse(Guid.NewGuid(), "sam@example.com", Guid.NewGuid(), "Sam", FriendsHousehold, "Friends", false, false, true, false),
                    new AccountInvitationResponse(Guid.NewGuid(), "ABCDE-FGHIJ", DateTimeOffset.UtcNow.AddDays(7)), null)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
