using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Profiles;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

public sealed class ActiveProfileSessionStartupTests
{
    [Fact]
    public async Task EarlyProfileLoadJoinsParentAuthorityValidationBeforeCallingProfilesApi()
    {
        var accountId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var principal = Principal(accountId, profileId, sessionId, "startup-token");
        var dashboard = new DashboardSessionAccessor();

        var handler = new DelayedValidationHandler(accountId, profileId, sessionId);
        var identity = new DashboardIdentityClient(new TestClientFactory(handler));
        var profileRequests = 0;
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ =>
        {
            Interlocked.Increment(ref profileRequests);
            return Task.FromResult(new List<ProfileViewModel> { Profile(profileId) });
        }));
        using var service = new ActiveProfileSessionService(
            new NullJsRuntime(), api, new ActiveProfileAccessor(), new TestAuthenticationStateProvider(principal),
            dashboard, identity);

        var profilesTask = service.GetProfilesAsync();
        await handler.RequestStarted.Task;
        Assert.True(dashboard.InitializeFromPrincipal(principal));
        var parentAuthority = identity.EnsureInitialAuthorityAsync(dashboard);

        Assert.Equal("startup-token", dashboard.SessionToken);
        Assert.Equal(0, profileRequests);
        handler.Release();
        var authority = await parentAuthority;
        var profiles = await profilesTask;

        Assert.NotNull(authority);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, profileRequests);
        Assert.Equal(profileId, Assert.Single(profiles).Id);
        Assert.Equal(profileId, service.CurrentProfile?.Id);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task UnavailableAuthorityFailsClosedWithoutRequestingProfiles(HttpStatusCode statusCode)
    {
        var profileId = Guid.NewGuid();
        var principal = Principal(Guid.NewGuid(), profileId, Guid.NewGuid(), "startup-token");
        var dashboard = new DashboardSessionAccessor();
        var identity = new DashboardIdentityClient(new TestClientFactory(new StatusHandler(statusCode)));
        var profileRequests = 0;
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ =>
        {
            Interlocked.Increment(ref profileRequests);
            return Task.FromResult(new List<ProfileViewModel> { Profile(profileId) });
        }));
        using var service = new ActiveProfileSessionService(
            new NullJsRuntime(), api, new ActiveProfileAccessor(), new TestAuthenticationStateProvider(principal),
            dashboard, identity);

        var profiles = await service.GetProfilesAsync();

        Assert.Empty(profiles);
        Assert.Equal(0, profileRequests);
        Assert.Null(service.CurrentProfile);
        Assert.Null(dashboard.Authority);
    }

    [Fact]
    public async Task EmptyProfileResponseIsRetriedOnNextLoad()
    {
        var profileId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var dashboard = AuthorizedDashboard(accountId, profileId);
        var profileRequests = 0;
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ =>
        {
            var request = Interlocked.Increment(ref profileRequests);
            return Task.FromResult(request == 1
                ? new List<ProfileViewModel>()
                : new List<ProfileViewModel> { Profile(profileId) });
        }));
        using var service = new ActiveProfileSessionService(new NullJsRuntime(), api,
            dashboardSession: dashboard);

        Assert.Empty(await service.GetProfilesAsync());
        var retry = await service.GetProfilesAsync();

        Assert.Equal(2, profileRequests);
        Assert.Equal(profileId, Assert.Single(retry).Id);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task DisabledAccountOrGrantDoesNotRequestProfiles(
        bool accountEnabled, bool grantEnabled, bool profileGrantEnabled)
    {
        var profileId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var authority = Authority(profileId, accountId) with
        {
            AccountEnabled = accountEnabled,
            GrantEnabled = grantEnabled,
            ProfileGrants = [Authority(profileId, accountId).ProfileGrants.Single() with { IsEnabled = profileGrantEnabled }],
        };
        var dashboard = new DashboardSessionAccessor();
        dashboard.Set("current-token", accountId, profileId, Guid.NewGuid(), authority);
        var profileRequests = 0;
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ =>
        {
            Interlocked.Increment(ref profileRequests);
            return Task.FromResult(new List<ProfileViewModel> { Profile(profileId) });
        }));
        using var service = new ActiveProfileSessionService(new NullJsRuntime(), api,
            dashboardSession: dashboard);

        Assert.Empty(await service.GetProfilesAsync());
        Assert.Equal(0, profileRequests);
        Assert.Null(service.CurrentProfile);
    }

    [Fact]
    public async Task LateProfileResponseCannotPublishAfterSessionSwitch()
    {
        var firstProfileId = Guid.NewGuid();
        var secondProfileId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var dashboard = AuthorizedDashboard(accountId, firstProfileId);
        var oldResponse = new TaskCompletionSource<List<ProfileViewModel>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var profileRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var profileRequests = 0;
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ =>
        {
            var request = Interlocked.Increment(ref profileRequests);
            profileRequestStarted.TrySetResult();
            return request == 1
                ? oldResponse.Task
                : Task.FromResult(new List<ProfileViewModel> { Profile(secondProfileId) });
        }));
        var principal = Principal(Guid.NewGuid(), firstProfileId, Guid.NewGuid(), "retained-token");
        var active = new ActiveProfileAccessor();
        using var service = new ActiveProfileSessionService(new NullJsRuntime(), api, active,
            new TestAuthenticationStateProvider(principal), dashboard);

        var oldLoad = service.GetProfilesAsync();
        await profileRequestStarted.Task;
        dashboard.Set("new-token", accountId, secondProfileId, Guid.NewGuid(), Authority(secondProfileId, accountId));
        oldResponse.SetResult([Profile(firstProfileId)]);

        Assert.Empty(await oldLoad);
        Assert.Empty(service.Profiles);
        Assert.Null(service.CurrentProfile);

        var currentProfiles = await service.GetProfilesAsync();
        Assert.Equal(secondProfileId, Assert.Single(currentProfiles).Id);
        Assert.Equal(secondProfileId, service.CurrentProfile?.Id);
        Assert.Equal(secondProfileId, active.ProfileId);
    }

    [Fact]
    public async Task EquivalentAuthorityRefreshDoesNotDiscardPendingProfileResponse()
    {
        var profileId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var dashboard = AuthorizedDashboard(accountId, profileId, sessionId);
        var oldResponse = new TaskCompletionSource<List<ProfileViewModel>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var profileRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ =>
        {
            profileRequestStarted.TrySetResult();
            return oldResponse.Task;
        }));
        var principal = Principal(accountId, profileId, sessionId, "current-token");
        using var service = new ActiveProfileSessionService(new NullJsRuntime(), api,
            authenticationStateProvider: new TestAuthenticationStateProvider(principal), dashboardSession: dashboard);

        var load = service.GetProfilesAsync();
        await profileRequestStarted.Task;
        dashboard.Set("current-token", accountId, profileId, sessionId, Authority(profileId, accountId));
        oldResponse.SetResult([Profile(profileId)]);

        Assert.Equal(profileId, Assert.Single(await load).Id);
        Assert.Equal(profileId, service.CurrentProfile?.Id);
    }

    [Fact]
    public async Task LateProfileResponseCannotPublishAfterSessionIdentityChanges()
    {
        var profileId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var dashboard = AuthorizedDashboard(accountId, profileId);
        var oldResponse = new TaskCompletionSource<List<ProfileViewModel>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var profileRequestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ =>
        {
            profileRequestStarted.TrySetResult();
            return oldResponse.Task;
        }));
        using var service = new ActiveProfileSessionService(new NullJsRuntime(), api, dashboardSession: dashboard);

        var load = service.GetProfilesAsync();
        await profileRequestStarted.Task;
        dashboard.Set("replacement-token", accountId, profileId, Guid.NewGuid(), Authority(profileId, accountId));
        oldResponse.SetResult([Profile(profileId)]);

        Assert.Empty(await load);
        Assert.Empty(service.Profiles);
        Assert.Null(service.CurrentProfile);
    }

    [Fact]
    public async Task DisabledGrantDuringProfileRequestRejectsLateResponse()
    {
        var profileId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var dashboard = AuthorizedDashboard(accountId, profileId, sessionId);
        var response = new TaskCompletionSource<List<ProfileViewModel>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ =>
        {
            requestStarted.TrySetResult();
            return response.Task;
        }));
        using var service = new ActiveProfileSessionService(new NullJsRuntime(), api,
            dashboardSession: dashboard);

        var load = service.GetProfilesAsync();
        await requestStarted.Task;
        var enabledGrant = Authority(profileId, accountId).ProfileGrants.Single();
        var disabled = Authority(profileId, accountId) with
        {
            ProfileGrants = [enabledGrant with { IsEnabled = false }],
        };
        dashboard.Set("current-token", accountId, profileId, sessionId, disabled);
        response.SetResult([Profile(profileId)]);

        Assert.Empty(await load);
        Assert.Empty(service.Profiles);
        Assert.Null(service.CurrentProfile);
    }

    private static DashboardSessionAccessor AuthorizedDashboard(Guid accountId, Guid profileId, Guid? sessionId = null)
    {
        var dashboard = new DashboardSessionAccessor();
        dashboard.Set("current-token", accountId, profileId, sessionId ?? Guid.NewGuid(), Authority(profileId, accountId));
        return dashboard;
    }

    private static ClaimsPrincipal Principal(Guid accountId, Guid profileId, Guid sessionId, string token) => new(
        new ClaimsIdentity(
        [
            new Claim(DashboardEngineAuthenticationHandler.SessionTokenClaim, token),
            new Claim("tuvima:account_id", accountId.ToString("D")),
            new Claim("tuvima:active_profile_id", profileId.ToString("D")),
            new Claim("tuvima:session_id", sessionId.ToString("D")),
        ], "test"));

    private static DashboardAuthorityResponse Authority(
        Guid profileId,
        Guid? accountId = null,
        bool accountEnabled = true,
        bool grantEnabled = true,
        bool profileGrantEnabled = true)
    {
        var ownerAccountId = accountId ?? Guid.NewGuid();
        return new DashboardAuthorityResponse(
            ownerAccountId, profileId, accountEnabled, grantEnabled, 1, 1, true, true, null, 1,
            [new AccountProfileGrantDto(ownerAccountId, profileId, "Profile", null, true, profileGrantEnabled, true,
                new GrantAdminProtectionDto(false, "UntilProfileSwitch", 30, 1, false, null), 1, DateTimeOffset.UnixEpoch)],
            [], []);
    }

    private static ProfileViewModel Profile(Guid id) => new(
        id, $"Profile {id:N}", "#C9922E", "User", DateTimeOffset.UtcNow);

    private sealed class TestAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class TestClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false)
        {
            BaseAddress = new Uri("http://engine.test"),
        };
    }

    private sealed class DelayedValidationHandler(Guid accountId, Guid profileId, Guid sessionId) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestCount { get; private set; }
        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SessionValidationResponse
                {
                    SessionId = sessionId,
                    AccountId = accountId,
                    ActiveProfileId = profileId,
                    DisplayName = "Current profile",
                    Authority = Authority(profileId, accountId),
                    AuthenticationMethod = "test",
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                }),
            };
        }
    }

    private sealed class StatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private sealed class NullJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }
}
