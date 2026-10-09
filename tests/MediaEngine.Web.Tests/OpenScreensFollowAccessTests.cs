using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>An open Dashboard screen follows access changes: a one-minute check, plus instant closing for big changes.</summary>
public sealed class OpenScreensFollowAccessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tuvima-open-screens-" + Guid.NewGuid().ToString("N"));
    private int _writes;

    public OpenScreensFollowAccessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* Best-effort temp cleanup; a locked folder only leaves scratch files behind. */ }
    }

    // ── The one-minute check ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Provider_ValidSession_StaysSignedIn()
    {
        var handler = new CountingHandler(_ => AllowedResponse());
        var provider = NewProvider(handler, WhoCanConnect.Anywhere);

        Assert.True(await provider.IsStillSignedInAsync(Principal("token", ClientIngressValues.Remote), default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Provider_RevokedSession_BecomesAnonymous()
    {
        var provider = NewProvider(new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), WhoCanConnect.Anywhere);

        Assert.False(await provider.IsStillSignedInAsync(Principal("token", ClientIngressValues.HomeNetwork), default));
    }

    [Fact]
    public async Task Provider_IngressNoLongerAllowed_BecomesAnonymousWithoutAskingTheEngine()
    {
        var handler = new CountingHandler(_ => AllowedResponse());
        var provider = NewProvider(handler, WhoCanConnect.ThisComputer);

        Assert.False(await provider.IsStillSignedInAsync(Principal("token", ClientIngressValues.HomeNetwork), default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Provider_HomeSessionSeenFromOutside_BecomesAnonymous()
    {
        var wrongPlace = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = JsonContent.Create(new { reason = ClientIngressValues.SignInAgainHere }),
        });
        var provider = NewProvider(wrongPlace, WhoCanConnect.Anywhere);

        Assert.False(await provider.IsStillSignedInAsync(Principal("token", ClientIngressValues.Remote), default));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Provider_EngineHiccup_DoesNotSignAnyoneOut(HttpStatusCode status)
    {
        var provider = NewProvider(new CountingHandler(_ => new HttpResponseMessage(status)), WhoCanConnect.Anywhere);

        Assert.True(await provider.IsStillSignedInAsync(Principal("token", ClientIngressValues.HomeNetwork), default));
    }

    [Fact]
    public async Task Provider_ScreenWithoutASignIn_IsAnonymous()
    {
        var provider = NewProvider(new CountingHandler(_ => AllowedResponse()), WhoCanConnect.Anywhere);

        Assert.False(await provider.IsStillSignedInAsync(new ClaimsPrincipal(new ClaimsIdentity()), default));
    }

    [Fact]
    public async Task Provider_SignOutScreen_PublishesAnAnonymousState()
    {
        var provider = NewProvider(new CountingHandler(_ => AllowedResponse()), WhoCanConnect.Anywhere);
        Task<AuthenticationState>? published = null;
        provider.AuthenticationStateChanged += state => published = state;

        provider.SignOutScreen();

        Assert.NotNull(published);
        Assert.False((await published!).User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task Provider_SignedOutByTheDoorRule_StopsForwardingItsSessionToken()
    {
        var provider = NewProvider(new CountingHandler(_ => AllowedResponse()), WhoCanConnect.ThisComputer, out var session);

        Assert.False(await provider.IsStillSignedInAsync(Principal("token", ClientIngressValues.Remote), default));

        var forwarding = session.CurrentForwardingState();
        Assert.Null(forwarding.SessionToken);
        Assert.True(forwarding.HasEstablishedSessionState); // so the ambient request fallback stays suppressed
    }

    [Fact]
    public async Task Provider_RevokedSession_StopsForwardingItsSessionToken()
    {
        var provider = NewProvider(new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), WhoCanConnect.Anywhere, out var session);

        Assert.False(await provider.IsStillSignedInAsync(Principal("token", ClientIngressValues.HomeNetwork), default));

        Assert.Null(session.CurrentForwardingState().SessionToken);
    }

    [Fact]
    public async Task Provider_HomeSessionSeenFromOutside_StopsForwardingItsSessionToken()
    {
        var wrongPlace = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = JsonContent.Create(new { reason = ClientIngressValues.SignInAgainHere }),
        });
        var provider = NewProvider(wrongPlace, WhoCanConnect.Anywhere, out var session);

        Assert.False(await provider.IsStillSignedInAsync(Principal("token", ClientIngressValues.Remote), default));

        Assert.Null(session.CurrentForwardingState().SessionToken);
    }

    [Fact]
    public async Task Provider_SignOutScreen_ClearsTheScreensCredentials()
    {
        var provider = NewProvider(new CountingHandler(_ => AllowedResponse()), WhoCanConnect.Anywhere, out var session);
        Assert.True(await provider.IsStillSignedInAsync(Principal("token", ClientIngressValues.HomeNetwork), default));
        Assert.Equal("token", session.CurrentForwardingState().SessionToken);

        provider.SignOutScreen();

        Assert.Null(session.CurrentForwardingState().SessionToken);
    }

    [Fact]
    public async Task CircuitHandler_RegistersOnOpenAndUnregistersOnClose()
    {
        var provider = NewProvider(new CountingHandler(_ => AllowedResponse()), WhoCanConnect.Anywhere, out var session);
        var user = Principal("token", ClientIngressValues.HomeNetwork);
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(user)));
        var registry = new OpenScreenRegistry();
        var handler = new OpenScreenCircuitHandler(registry, provider);

        await handler.OnCircuitOpenedAsync(null!, default);
        Assert.Equal(1, registry.Count);

        // Closing the registered screen signs this circuit out.
        Assert.Equal(1, registry.CloseWhere(screen => screen.Ingress == ClientIngressValues.HomeNetwork));
        Assert.False(((await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated) ?? false);
        Assert.Null(session.CurrentForwardingState().SessionToken);

        await handler.OnCircuitClosedAsync(null!, default);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task CircuitHandler_DoesNotRegisterAnAnonymousCircuit()
    {
        var provider = NewProvider(new CountingHandler(_ => AllowedResponse()), WhoCanConnect.Anywhere);
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
        var registry = new OpenScreenRegistry();

        await new OpenScreenCircuitHandler(registry, provider).OnCircuitOpenedAsync(null!, default);

        Assert.Equal(0, registry.Count);
    }

    // ── The registry ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_CloseWhere_ClosesOnlyMatchingScreens()
    {
        var registry = new OpenScreenRegistry();
        var account = Guid.NewGuid();
        var closed = new List<string>();
        registry.Register(Screen(account, ClientIngressValues.Remote), () => closed.Add("mine"));
        registry.Register(Screen(Guid.NewGuid(), ClientIngressValues.Remote), () => closed.Add("theirs"));

        var count = registry.CloseWhere(screen => screen.AccountId == account);

        Assert.Equal(1, count);
        Assert.Equal(["mine"], closed);
    }

    [Fact]
    public void Registry_ScreensUnregisterWhenTheyClose()
    {
        var registry = new OpenScreenRegistry();
        var registration = registry.Register(Screen(Guid.NewGuid(), ClientIngressValues.Remote), () => { });
        Assert.Equal(1, registry.Count);

        registration.Dispose();

        Assert.Equal(0, registry.Count);
        Assert.Equal(0, registry.CloseWhere(_ => true));
    }

    [Fact]
    public void Registry_OneScreenFailingToClose_DoesNotStopTheOthers()
    {
        var registry = new OpenScreenRegistry();
        var closed = 0;
        registry.Register(Screen(Guid.NewGuid(), ClientIngressValues.Remote), () => throw new InvalidOperationException("gone"));
        registry.Register(Screen(Guid.NewGuid(), ClientIngressValues.Remote), () => closed++);

        Assert.Equal(1, registry.CloseWhere(_ => true));
        Assert.Equal(1, closed);
    }

    [Theory]
    [InlineData(WhoCanConnect.ThisComputer, true, true)]
    [InlineData(WhoCanConnect.HomeNetwork, false, true)]
    [InlineData(WhoCanConnect.Anywhere, false, false)]
    public void Registry_LoweringWhoCanConnect_ClosesOnlyScreensFromFartherAway(string setting, bool homeCloses, bool remoteCloses)
    {
        var registry = new OpenScreenRegistry();
        var closed = new HashSet<string>();
        registry.Register(Screen(Guid.NewGuid(), ClientIngressValues.ThisComputer), () => closed.Add("computer"));
        registry.Register(Screen(Guid.NewGuid(), ClientIngressValues.HomeNetwork), () => closed.Add("home"));
        registry.Register(Screen(Guid.NewGuid(), ClientIngressValues.Remote), () => closed.Add("remote"));

        registry.CloseWhereIngressNotAllowed(setting);

        Assert.DoesNotContain("computer", closed);
        Assert.Equal(homeCloses, closed.Contains("home"));
        Assert.Equal(remoteCloses, closed.Contains("remote"));
    }

    // ── Big-change actions close the right screens ────────────────────────────────────────────────

    [Fact]
    public async Task RemovingAPerson_ClosesThatAccountsScreens()
    {
        var f = new Fixture();
        await f.Identity.DeleteManagedAccountResultAsync(f.AccountA);
        Assert.Equal(["a1", "a2"], f.Closed());
    }

    [Fact]
    public async Task RemovingAProfile_ClosesScreensUsingThatProfile()
    {
        var f = new Fixture();
        await f.Identity.DeleteManagedProfileResultAsync(f.ProfileA1);
        Assert.Equal(["a1"], f.Closed());
    }

    [Fact]
    public async Task RevokingAProfileGrant_ClosesThatAccountAndProfileOnly()
    {
        var f = new Fixture();
        await f.Identity.RevokeManagedProfileGrantResultAsync(f.AccountA, f.ProfileA2);
        Assert.Equal(["a2"], f.Closed());
    }

    [Fact]
    public async Task SigningOutADevice_ClosesScreensUsingThatSession()
    {
        var f = new Fixture();
        await f.Identity.RevokeSessionResultAsync(f.SessionB);
        Assert.Equal(["b"], f.Closed());
    }

    [Fact]
    public async Task SigningOutOtherSessions_KeepsTheCurrentSessionOpen()
    {
        var f = new Fixture(body: new RevokeOtherSessionsResponse(1));
        await f.Identity.RevokeOtherSessionsAsync(f.AccountA, f.SessionA1);
        Assert.Equal(["a2"], f.Closed());
    }

    [Fact]
    public async Task SigningOutOtherSessions_WithoutKnownIds_ClosesNothing()
    {
        var f = new Fixture(body: new RevokeOtherSessionsResponse(1));
        await f.Identity.RevokeOtherSessionsAsync(null, f.SessionA1);
        await f.Identity.RevokeOtherSessionsAsync(f.AccountA, null);
        Assert.Empty(f.Closed());
    }

    [Fact]
    public async Task DisablingAnAccount_ClosesThatAccountsScreens()
    {
        var f = new Fixture(body: AccountResponse(isEnabled: false));
        await f.Identity.UpdateManagedAccountResultAsync(f.AccountA, new UpdateManagedAccountRequest("a@example.test", IsEnabled: false, false));
        Assert.Equal(["a1", "a2"], f.Closed());
    }

    [Fact]
    public async Task EditingAnAccountThatStaysEnabled_ClosesNothing()
    {
        var f = new Fixture(body: AccountResponse(isEnabled: true));
        await f.Identity.UpdateManagedAccountResultAsync(f.AccountA, new UpdateManagedAccountRequest("a@example.test", IsEnabled: true, false));
        Assert.Empty(f.Closed());
    }

    [Fact]
    public async Task ResettingNetworkSettings_ClosesScreensFromFartherAwayThanTheDefault()
    {
        var registry = new OpenScreenRegistry();
        var closed = new List<string>();
        registry.Register(Screen(Guid.NewGuid(), ClientIngressValues.HomeNetwork), () => closed.Add("home"));
        registry.Register(Screen(Guid.NewGuid(), ClientIngressValues.Remote), () => closed.Add("remote"));
        var reset = new NetworkSettingsDto { WhoCanConnect = WhoCanConnect.HomeNetwork };
        using var http = new HttpClient(new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(reset) })) { BaseAddress = new Uri("http://engine.test") };
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance, openScreens: registry);

        Assert.NotNull(await client.ResetNetworkSettingsAsync());

        Assert.Equal(["remote"], closed);
    }

    [Fact]
    public async Task AFailedAction_ClosesNothing()
    {
        var f = new Fixture(status: HttpStatusCode.Forbidden);
        await f.Identity.DeleteManagedAccountResultAsync(f.AccountA);
        await f.Identity.RevokeSessionResultAsync(f.SessionB);
        Assert.Empty(f.Closed());
    }

    [Fact]
    public async Task LoweringWhoCanConnect_ClosesScreensFromFartherAway()
    {
        var registry = new OpenScreenRegistry();
        var closed = new List<string>();
        registry.Register(Screen(Guid.NewGuid(), ClientIngressValues.HomeNetwork), () => closed.Add("home"));
        registry.Register(Screen(Guid.NewGuid(), ClientIngressValues.Remote), () => closed.Add("remote"));
        using var http = new HttpClient(new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))) { BaseAddress = new Uri("http://engine.test") };
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance, openScreens: registry);

        var saved = await client.UpdateNetworkSettingsAsync(new NetworkSettingsDto { WhoCanConnect = WhoCanConnect.HomeNetwork });

        Assert.NotNull(saved);
        Assert.Equal(["remote"], closed);
    }

    [Fact]
    public async Task AFailedNetworkSave_ClosesNothing()
    {
        var registry = new OpenScreenRegistry();
        var closed = 0;
        registry.Register(Screen(Guid.NewGuid(), ClientIngressValues.Remote), () => closed++);
        using var http = new HttpClient(new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))) { BaseAddress = new Uri("http://engine.test") };
        var client = new EngineApiClient(http, NullLogger<EngineApiClient>.Instance, openScreens: registry);

        Assert.Null(await client.UpdateNetworkSettingsAsync(new NetworkSettingsDto { WhoCanConnect = WhoCanConnect.ThisComputer }));
        Assert.Equal(0, closed);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static class WhoCanConnect
    {
        public const string ThisComputer = "this_computer";
        public const string HomeNetwork = "home_network";
        public const string Anywhere = "anywhere";
    }

    private SessionRevalidatingAuthenticationStateProvider NewProvider(HttpMessageHandler handler, string whoCanConnect) =>
        NewProvider(handler, whoCanConnect, out _);

    private SessionRevalidatingAuthenticationStateProvider NewProvider(HttpMessageHandler handler, string whoCanConnect, out DashboardSessionAccessor session)
    {
        session = new DashboardSessionAccessor();
        var path = Path.Combine(_dir, "network.json");
        File.WriteAllText(path, $"{{\"who_can_connect\":\"{whoCanConnect}\"}}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(_writes++));
        var exposure = new ExposureSettingsReader(new DashboardConfigurationReader(_dir), _dir);
        return new SessionRevalidatingAuthenticationStateProvider(
            NullLoggerFactory.Instance,
            new DashboardIdentityClient(new Factory(handler)),
            session,
            exposure);
    }

    private static ClaimsPrincipal Principal(string token, string ingress) =>
        new(new ClaimsIdentity(
        [
            new Claim(DashboardEngineAuthenticationHandler.SessionTokenClaim, token),
            new Claim("tuvima:account_id", Guid.NewGuid().ToString("D")),
            new Claim("tuvima:active_profile_id", Guid.NewGuid().ToString("D")),
            new Claim("tuvima:session_id", Guid.NewGuid().ToString("D")),
            new Claim(DashboardPrincipalFactory.ClientIngressClaim, ingress),
        ], "test"));

    private static AccountAccessResponse AccountResponse(bool isEnabled) =>
        new(Guid.NewGuid(), "a@example.test", false, isEnabled, false, 1, [], [], [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    private static OpenScreen Screen(Guid account, string ingress, Guid? profile = null, Guid? session = null) =>
        new(Guid.NewGuid(), account, profile ?? Guid.NewGuid(), session ?? Guid.NewGuid(), OpenScreenRegistry.HashToken("token"), ingress);

    private static HttpResponseMessage AllowedResponse()
    {
        var account = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var authority = new DashboardAuthorityResponse(account, profile, true, true, 1, 1, true, true, null, 1, [], [], []);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new SessionValidationResponse
            {
                SessionId = Guid.NewGuid(),
                AccountId = account,
                ActiveProfileId = profile,
                DisplayName = "Profile",
                Authority = authority,
                AuthenticationMethod = "test",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            }),
        };
    }

    /// <summary>Two screens for account A (different profiles and sessions) and one for account B.</summary>
    private sealed class Fixture
    {
        public Guid AccountA { get; } = Guid.NewGuid();
        public Guid AccountB { get; } = Guid.NewGuid();
        public Guid ProfileA1 { get; } = Guid.NewGuid();
        public Guid ProfileA2 { get; } = Guid.NewGuid();
        public Guid SessionA1 { get; } = Guid.NewGuid();
        public Guid SessionA2 { get; } = Guid.NewGuid();
        public Guid SessionB { get; } = Guid.NewGuid();
        public DashboardIdentityClient Identity { get; }
        private readonly List<string> _closed = [];

        public Fixture(HttpStatusCode status = HttpStatusCode.OK, object? body = null)
        {
            var registry = new OpenScreenRegistry();
            registry.Register(Screen(AccountA, ClientIngressValues.HomeNetwork, ProfileA1, SessionA1), () => _closed.Add("a1"));
            registry.Register(Screen(AccountA, ClientIngressValues.HomeNetwork, ProfileA2, SessionA2), () => _closed.Add("a2"));
            registry.Register(Screen(AccountB, ClientIngressValues.HomeNetwork, Guid.NewGuid(), SessionB), () => _closed.Add("b"));
            var handler = new CountingHandler(_ => new HttpResponseMessage(status)
            {
                Content = body is null ? new StringContent(string.Empty) : JsonContent.Create(body, body.GetType()),
            });
            Identity = new DashboardIdentityClient(new Factory(handler), openScreens: registry);
        }

        public IReadOnlyList<string> Closed() => _closed.Order().ToArray();
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }
}
