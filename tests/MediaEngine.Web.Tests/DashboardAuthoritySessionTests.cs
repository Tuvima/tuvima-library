using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

public sealed class DashboardAuthoritySessionTests
{
    [Fact]
    public void CircuitInitialization_SeedsOnlyCookieSessionIdentity()
    {
        var account = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var circuit = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "Owner"),
            new Claim(DashboardEngineAuthenticationHandler.SessionTokenClaim, "cookie-session"),
            new Claim("tuvima:account_id", account.ToString("D")),
            new Claim("tuvima:active_profile_id", profile.ToString("D")),
            new Claim("tuvima:session_id", circuit.ToString("D")),
        ], "cookie"));
        var session = new DashboardSessionAccessor();

        Assert.True(session.InitializeFromPrincipal(principal));
        Assert.Equal("cookie-session", session.SessionToken);
        Assert.Equal(account, session.AccountId);
        Assert.Equal(profile, session.ActiveProfileId);
        Assert.Equal(circuit, session.SessionId);
        Assert.Null(session.Authority);
        Assert.False(session.HasNavigation("settings.administration"));
    }

    [Fact]
    public void ClearedCircuitSessionCannotBeReseededFromRetainedPrincipal()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(DashboardEngineAuthenticationHandler.SessionTokenClaim, "stale-session"),
        ], "cookie"));
        var session = new DashboardSessionAccessor();
        Assert.True(session.InitializeFromPrincipal(principal));
        var refresh = session.SnapshotForRefresh();

        Assert.True(session.ClearIfCurrent(refresh));

        Assert.False(session.InitializeFromPrincipal(principal));
        Assert.Null(session.SessionToken);
    }

    [Fact]
    public void AccountVersionChange_NotifiesTheCircuitAndReplacesCapabilities()
    {
        var session = new DashboardSessionAccessor();
        var changes = 0;
        session.OnAuthorityChanged += () => changes++;
        session.Set("token", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Authority(1, true, "access.manage"));
        session.Set("token", session.AccountId, session.ActiveProfileId, session.SessionId, Authority(2, false));

        Assert.Equal(2, changes);
        Assert.False(session.HasAction("access.manage"));
    }

    [Fact]
    public void LockOnLeave_IsReadFromTheActiveGrant()
    {
        var account = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var session = new DashboardSessionAccessor();
        session.Set("token", account, profile, Guid.NewGuid(), Authority(1, true, "access.manage", profile, "LockOnLeave"));

        Assert.True(session.ShouldLockOnLeave);
    }

    [Theory]
    [InlineData("expired", false)]
    [InlineData("future", true)]
    [InlineData("until-switch", true)]
    [InlineData("denied", false)]
    public async Task AdministratorSurfaceAccess_UsesLiveExpiryAndEligibility(string mode, bool expected)
    {
        var authority = Authority(1, true, "access.manage");
        authority = mode switch
        {
            "expired" => authority with { AdministratorUnlockExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
            "future" => authority with { AdministratorUnlockExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) },
            "until-switch" => authority with { AdministratorUnlockExpiresAt = null },
            _ => authority with { EffectiveAdministrator = false, AdministratorSurfaceUnlocked = false },
        };
        var session = new DashboardSessionAccessor();
        session.Set("token", authority.AccountId, authority.ActiveProfileId, Guid.NewGuid(), authority);
        var client = new DashboardIdentityClient(new TestClientFactory(new AuthorityHandler(authority)));
        var service = new AdministratorSurfaceAccessService(client, session);

        Assert.Equal(expected, await service.EnsureUnlockedAsync());
    }

    [Fact]
    public async Task Revalidation_401ClearsOnlyTheMatchingCachedAuthority()
    {
        var session = AuthenticatedSession("old-token");
        var client = new DashboardIdentityClient(new TestClientFactory(new StaticHandler(HttpStatusCode.Unauthorized)));

        var result = await client.RevalidateAuthorityAsync(session);

        Assert.Null(result);
        Assert.Null(session.Authority);
        Assert.Null(session.SessionToken);
    }

    [Fact]
    public async Task LateRevalidation_CannotOverwriteANewerProfileSession()
    {
        var handler = new DelayedAuthorityHandler();
        var client = new DashboardIdentityClient(new TestClientFactory(handler));
        var session = AuthenticatedSession("old-token");
        var pending = client.RevalidateAuthorityAsync(session);

        session.Set("new-token", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Authority(9, true, "access.manage"));
        handler.Release();

        Assert.Null(await pending);
        Assert.Equal("new-token", session.SessionToken);
        Assert.Equal(9, session.Authority!.AccountAuthorizationVersion);
    }

    [Fact]
    public async Task InitialCircuitValidation_IsCoalescedForLayoutAndSettings()
    {
        var handler = new DelayedAuthorityHandler();
        var client = new DashboardIdentityClient(new TestClientFactory(handler));
        var session = AuthenticatedSession("token");
        var layout = client.EnsureInitialAuthorityAsync(session);
        var settings = client.EnsureInitialAuthorityAsync(session);

        Assert.Same(layout, settings);
        handler.Release();
        Assert.NotNull(await layout);
    }

    [Fact]
    public async Task CompletedInitialValidation_DoesNotReuseItsResultAfterRevocation()
    {
        var session = AuthenticatedSession("token");
        var client = new DashboardIdentityClient(new TestClientFactory(new SequenceHandler(
            AllowedResponse(3), new HttpResponseMessage(HttpStatusCode.Unauthorized))));

        Assert.NotNull(await client.EnsureInitialAuthorityAsync(session));
        Assert.Null(await client.EnsureInitialAuthorityAsync(session));
        Assert.Null(session.Authority);
        Assert.Null(session.SessionToken);
    }

    [Fact]
    public async Task OverlappingRefreshes_NewerDenyWinsWhenOlderAllowReturnsFirst()
    {
        var handler = new QueuedHandler();
        var client = new DashboardIdentityClient(new TestClientFactory(handler));
        var session = AuthenticatedSession("token");
        var older = client.RevalidateAuthorityAsync(session);
        var newer = client.RevalidateAuthorityAsync(session);
        handler.Complete(0, AllowedResponse(2));
        handler.Complete(1, new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Task.WhenAll(older, newer);
        Assert.Null(session.Authority);
    }

    [Fact]
    public async Task OverlappingRefreshes_NewerAllowWinsWhenOlderDenyReturnsLast()
    {
        var handler = new QueuedHandler();
        var client = new DashboardIdentityClient(new TestClientFactory(handler));
        var session = AuthenticatedSession("token");
        var older = client.RevalidateAuthorityAsync(session);
        var newer = client.RevalidateAuthorityAsync(session);
        handler.Complete(1, AllowedResponse(7));
        handler.Complete(0, new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Task.WhenAll(older, newer);
        Assert.Equal(7, session.Authority!.AccountAuthorizationVersion);
    }

    [Fact]
    public async Task MalformedSuccess_ClearsOnlyAuthorityAndNextValidResponseRecovers()
    {
        var session = AuthenticatedSession("token");
        var accountId = session.AccountId;
        var profileId = session.ActiveProfileId;
        var sessionId = session.SessionId;
        var handler = new SequenceHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{broken", Encoding.UTF8, "application/json"),
            },
            AllowedResponse(7));
        var client = new DashboardIdentityClient(new TestClientFactory(handler));

        Assert.Null(await client.RevalidateAuthorityAsync(session));
        Assert.Null(session.Authority);
        Assert.Equal("token", session.SessionToken);
        Assert.Equal(accountId, session.AccountId);
        Assert.Equal(profileId, session.ActiveProfileId);
        Assert.Equal(sessionId, session.SessionId);

        var recovered = await client.RevalidateAuthorityAsync(session);
        Assert.Equal(7, recovered!.AccountAuthorizationVersion);
        Assert.Equal(7, session.Authority!.AccountAuthorizationVersion);
        Assert.Equal("token", session.SessionToken);
    }

    [Fact]
    public async Task AccessClients_UseCanonicalApplicationAndSelfServiceRoutes()
    {
        var handler = new RecordingRouteHandler();
        var client = new DashboardIdentityClient(new TestClientFactory(handler));
        var loginId = Guid.NewGuid();

        await client.GetApplicationsAsync();
        await client.GetExternalLoginsAsync();
        Assert.True(await client.UnlinkExternalLoginAsync(loginId));

        Assert.Equal([
            "/access/applications",
            "/access/self-service/external-logins",
            $"/access/self-service/external-logins/{loginId:D}",
        ], handler.Paths);
    }

    private static DashboardAuthorityResponse Authority(long version, bool unlocked, params string[] actions) =>
        Authority(version, unlocked, actions, Guid.NewGuid(), "FixedDuration");

    private static DashboardAuthorityResponse Authority(long version, bool unlocked, string action, Guid profile, string mode) =>
        Authority(version, unlocked, [action], profile, mode);

    private static DashboardAuthorityResponse Authority(long version, bool unlocked, IReadOnlyList<string> actions, Guid profile, string mode) =>
        new(profile, profile, true, true, version, version, true, unlocked,
            unlocked ? null : DateTimeOffset.UtcNow.AddMinutes(-1), 1,
            [new AccountProfileGrantDto(profile, profile, "Profile", null, true, true, true,
                new GrantAdminProtectionDto(true, mode, 30, 1, false, null), version, DateTimeOffset.UtcNow)],
            ["settings.administration"], actions);

    private static DashboardSessionAccessor AuthenticatedSession(string token)
    {
        var session = new DashboardSessionAccessor();
        session.Set(token, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Authority(1, true, "access.manage"));
        return session;
    }

    private sealed class TestClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private sealed class StaticHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;
        public int CallCount => Volatile.Read(ref _index);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _index) - 1;
            return Task.FromResult(index < responses.Length
                ? responses[index]
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class RecordingRouteHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath.TrimEnd('/'));
            return Task.FromResult(request.Method == HttpMethod.Delete
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) });
        }
    }

    private sealed class DelayedAuthorityHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.SetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await _release.Task.WaitAsync(cancellationToken);
            var authority = Authority(2, true, "access.manage");
            var body = new SessionValidationResponse
            {
                SessionId = Guid.NewGuid(),
                AccountId = authority.AccountId,
                ActiveProfileId = authority.ActiveProfileId,
                DisplayName = "Profile",
                Authority = authority,
                AuthenticationMethod = "test",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }

    private sealed class QueuedHandler : HttpMessageHandler
    {
        private readonly List<TaskCompletionSource<HttpResponseMessage>> _responses = [];
        public void Complete(int index, HttpResponseMessage response) => _responses[index].SetResult(response);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _responses.Add(completion);
            return completion.Task;
        }
    }

    private sealed class AuthorityHandler(DashboardAuthorityResponse authority) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/admin-unlock", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new GrantAdminUnlockResponse(false, null, authority.AdministratorProtectionVersion)),
                });
            }

            return Task.FromResult(AllowedResponse(authority.AccountAuthorizationVersion, authority));
        }
    }

    private static HttpResponseMessage AllowedResponse(long version, DashboardAuthorityResponse? supplied = null)
    {
        var authority = supplied ?? Authority(version, true, "access.manage");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new SessionValidationResponse
            {
                SessionId = Guid.NewGuid(),
                AccountId = authority.AccountId,
                ActiveProfileId = authority.ActiveProfileId,
                DisplayName = "Profile",
                Authority = authority,
                AuthenticationMethod = "test",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            }),
        };
    }
}
