using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>An open Dashboard screen follows access changes: a one-minute check, plus instant closing for big changes.</summary>
public sealed class OpenScreensFollowAccessTests
{
    // ── The one-minute check ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Provider_ValidSession_StaysSignedIn()
    {
        var handler = new CountingHandler(_ => AllowedResponse());
        var provider = NewProvider(handler);

        Assert.True(await provider.IsStillSignedInAsync(Principal("token", ClientIngressValues.Remote), default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Provider_RevokedSession_BecomesAnonymous()
    {
        var provider = NewProvider(new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        Assert.False(await provider.IsStillSignedInAsync(Principal("token", ClientIngressValues.HomeNetwork), default));
    }

    [Fact]
    public async Task Provider_HomeSessionSeenFromOutside_BecomesAnonymous()
    {
        var wrongPlace = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = JsonContent.Create(new { reason = ClientIngressValues.SignInAgainHere }),
        });
        var provider = NewProvider(wrongPlace);

        Assert.False(await provider.IsStillSignedInAsync(Principal("token", ClientIngressValues.Remote), default));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Provider_EngineHiccup_DoesNotSignAnyoneOut(HttpStatusCode status)
    {
        var provider = NewProvider(new CountingHandler(_ => new HttpResponseMessage(status)));

        Assert.True(await provider.IsStillSignedInAsync(Principal("token", ClientIngressValues.HomeNetwork), default));
    }

    [Fact]
    public async Task Provider_ScreenWithoutASignIn_IsAnonymous()
    {
        var provider = NewProvider(new CountingHandler(_ => AllowedResponse()));

        Assert.False(await provider.IsStillSignedInAsync(new ClaimsPrincipal(new ClaimsIdentity()), default));
    }

    [Fact]
    public async Task Provider_SignOutScreen_PublishesAnAnonymousState()
    {
        var provider = NewProvider(new CountingHandler(_ => AllowedResponse()));
        Task<AuthenticationState>? published = null;
        provider.AuthenticationStateChanged += state => published = state;

        provider.SignOutScreen();

        Assert.NotNull(published);
        Assert.False((await published!).User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task Provider_RevokedSession_StopsForwardingItsSessionToken()
    {
        var provider = NewProvider(new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), out var session);

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
        var provider = NewProvider(wrongPlace, out var session);

        Assert.False(await provider.IsStillSignedInAsync(Principal("token", ClientIngressValues.Remote), default));

        Assert.Null(session.CurrentForwardingState().SessionToken);
    }

    [Fact]
    public async Task Provider_SignOutScreen_ClearsTheScreensCredentials()
    {
        var provider = NewProvider(new CountingHandler(_ => AllowedResponse()), out var session);
        Assert.True(await provider.IsStillSignedInAsync(Principal("token", ClientIngressValues.HomeNetwork), default));
        Assert.Equal("token", session.CurrentForwardingState().SessionToken);

        provider.SignOutScreen();

        Assert.Null(session.CurrentForwardingState().SessionToken);
    }

    [Fact]
    public async Task CircuitHandler_RegistersOnOpenAndUnregistersOnClose()
    {
        var provider = NewProvider(new CountingHandler(_ => AllowedResponse()), out var session);
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
        var provider = NewProvider(new CountingHandler(_ => AllowedResponse()));
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
    public async Task AFailedAction_ClosesNothing()
    {
        var f = new Fixture(status: HttpStatusCode.Forbidden);
        await f.Identity.DeleteManagedAccountResultAsync(f.AccountA);
        await f.Identity.RevokeSessionResultAsync(f.SessionB);
        Assert.Empty(f.Closed());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private SessionRevalidatingAuthenticationStateProvider NewProvider(HttpMessageHandler handler) =>
        NewProvider(handler, out _);

    private SessionRevalidatingAuthenticationStateProvider NewProvider(HttpMessageHandler handler, out DashboardSessionAccessor session)
    {
        session = new DashboardSessionAccessor();
        return new SessionRevalidatingAuthenticationStateProvider(
            NullLoggerFactory.Instance,
            new DashboardIdentityClient(new Factory(handler)),
            session);
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
        new(Guid.NewGuid(), "a@example.test", isEnabled, false, 1, [], [], [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

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
