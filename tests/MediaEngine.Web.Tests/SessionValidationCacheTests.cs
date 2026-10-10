using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A page load makes dozens of Dashboard requests, each of which used to ask the Engine whether the sign-in still
/// stands. These tests pin the shared answer: one Engine call per burst, never kept past a change to the sign-in,
/// never kept for a refusal, and never used when the Dashboard credential is missing or has been replaced.
/// </summary>
public sealed class SessionValidationCacheTests : IDisposable
{
    private const string Home = ClientIngressValues.HomeNetwork;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-validation-cache-{Guid.NewGuid():N}");
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);

    [Fact]
    public async Task ParallelRequests_ShareOneEngineCheck()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new EngineSpy { Gate = gate };
        var identity = CreateIdentity(engine, out _);

        var requests = Enumerable.Range(0, 40).Select(_ => identity.ValidateCookieAsync("token", Home)).ToArray();

        await engine.WaitForCallsAsync(1);
        Assert.Equal(1, engine.Calls);
        gate.SetResult();
        var results = await Task.WhenAll(requests);

        Assert.All(results, result => Assert.NotNull(result.Response));
        Assert.Equal(1, engine.Calls);

        // Still inside the window: the next request is answered from memory too.
        Assert.NotNull((await identity.ValidateCookieAsync("token", Home)).Response);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task ASharedCheck_ExpiresSoTheEngineIsAskedAgain()
    {
        var engine = new EngineSpy();
        var identity = CreateIdentity(engine, out _);

        await identity.ValidateCookieAsync("token", Home);
        _clock.Advance(SessionValidationCache.DefaultLifetime - TimeSpan.FromMilliseconds(1));
        await identity.ValidateCookieAsync("token", Home);
        Assert.Equal(1, engine.Calls);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await identity.ValidateCookieAsync("token", Home);
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task ASignInChange_EmptiesTheSharedCheckAtOnce()
    {
        var engine = new EngineSpy();
        var identity = CreateIdentity(engine, out var http);

        Assert.NotNull((await identity.ValidateCookieAsync("token", Home)).Response);
        // Signing out (or switching profile, revoking a device, changing access) goes through the identity client as a change.
        using var signedOut = await http.PostAsync("/auth/sessions/revoke", content: null);
        engine.RespondWith = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var afterRevoke = await identity.ValidateCookieAsync("token", Home);

        Assert.Null(afterRevoke.Response);
        Assert.True(afterRevoke.Invalid);
        Assert.Equal(2, engine.ValidateCalls);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task EveryKindOfChange_EmptiesTheSharedCheck(string method)
    {
        var engine = new EngineSpy();
        var identity = CreateIdentity(engine, out var http);
        await identity.ValidateCookieAsync("token", Home);

        using var changed = await http.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/access/profiles/x"));
        await identity.ValidateCookieAsync("token", Home);

        Assert.Equal(2, engine.ValidateCalls);
    }

    [Fact]
    public async Task ReadsAndTheCheckItself_LeaveTheSharedCheckAlone()
    {
        var engine = new EngineSpy();
        var identity = CreateIdentity(engine, out var http);
        await identity.ValidateCookieAsync("token", Home);

        using var read = await http.GetAsync("/auth/bootstrap/status");
        using var other = await http.PostAsync("/auth/session/validate", content: null);
        await identity.ValidateCookieAsync("token", Home);

        Assert.Equal(2, engine.ValidateCalls); // the one shared check plus the explicit POST above
    }

    [Fact]
    public async Task AChangeDuringACheck_IsNotJoinedAndItsAnswerIsNotKept()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new EngineSpy { Gate = gate };
        var identity = CreateIdentity(engine, out var http);

        var before = identity.ValidateCookieAsync("token", Home); // starts check #1, held at the gate
        await engine.WaitForCallsAsync(1);
        engine.ChangeCallsAreNotGated = true;
        using var changed = await http.PostAsync("/auth/sessions/revoke", content: null);
        var after = identity.ValidateCookieAsync("token", Home); // must not join check #1

        await engine.WaitForCallsAsync(3); // check #1, the change, check #2
        Assert.Equal(2, engine.ValidateCalls);
        gate.SetResult();
        await Task.WhenAll(before, after);

        // Check #1 started before the change, so its answer must not be remembered for later requests.
        engine.Gate = null;
        await identity.ValidateCookieAsync("token", Home);
        Assert.Equal(2, engine.ValidateCalls); // #2 (started after the change) is the one that was kept
        engine.ChangeCallsAreNotGated = false;
    }

    [Fact]
    public async Task ARefusalIsNeverRemembered_SoARecoveredSignInIsSeenAtOnce()
    {
        var engine = new EngineSpy { RespondWith = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        var identity = CreateIdentity(engine, out _);

        var refused = await identity.ValidateCookieAsync("token", Home);
        Assert.Null(refused.Response);
        Assert.True(refused.Invalid);

        engine.RespondWith = null;
        Assert.NotNull((await identity.ValidateCookieAsync("token", Home)).Response);
        Assert.Equal(2, engine.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task AnEngineThatCannotAnswer_IsNeverRemembered(HttpStatusCode status)
    {
        var engine = new EngineSpy { RespondWith = _ => new HttpResponseMessage(status) };
        var identity = CreateIdentity(engine, out _);

        Assert.Null((await identity.ValidateCookieAsync("token", Home)).Response);
        Assert.Null((await identity.ValidateCookieAsync("token", Home)).Response);

        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task DifferentSignInsAndDifferentPlaces_NeverShareACheck()
    {
        var engine = new EngineSpy();
        var identity = CreateIdentity(engine, out _);

        await identity.ValidateCookieAsync("token-a", Home);
        await identity.ValidateCookieAsync("token-b", Home);
        await identity.ValidateCookieAsync("token-a", ClientIngressValues.Remote);

        Assert.Equal(3, engine.Calls);
    }

    [Fact]
    public async Task ACheckIsNeverKeptPastTheSessionsOwnEnd()
    {
        var engine = new EngineSpy { SessionEndsAt = _clock.GetUtcNow().AddSeconds(2) };
        var identity = CreateIdentity(engine, out _);

        await identity.ValidateCookieAsync("token", Home);
        _clock.Advance(TimeSpan.FromSeconds(2));
        await identity.ValidateCookieAsync("token", Home);

        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task OneRequestGivingUp_DoesNotCancelTheCheckOthersAreWaitingOn()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new EngineSpy { Gate = gate };
        var identity = CreateIdentity(engine, out _);
        using var gaveUp = new CancellationTokenSource();

        var first = identity.ValidateCookieAsync("token", Home, gaveUp.Token);
        var second = identity.ValidateCookieAsync("token", Home);
        await engine.WaitForCallsAsync(1);
        gaveUp.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        gate.SetResult();
        Assert.NotNull((await second).Response);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task AReplacedDashboardCredential_NeverReusesAnEarlierCheck()
    {
        var protection = DataProtectionProvider.Create(Directory.CreateDirectory(Path.Combine(_root, "keys")));
        var credential = CreateCredentialProvider(protection);
        WriteBundle(protection, "first-credential");
        var engine = new EngineSpy();
        var identity = CreateIdentity(engine, out _, credential);

        await identity.ValidateCookieAsync("token", Home);
        await identity.ValidateCookieAsync("token", Home);
        Assert.Equal(1, engine.Calls);

        WriteBundle(protection, "rotated-credential");
        await identity.ValidateCookieAsync("token", Home);
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public async Task AMissingDashboardCredential_NeverAnswersFromMemory()
    {
        var protection = DataProtectionProvider.Create(Directory.CreateDirectory(Path.Combine(_root, "keys")));
        var credential = CreateCredentialProvider(protection);
        WriteBundle(protection, "first-credential");
        var engine = new EngineSpy();
        var identity = CreateIdentity(engine, out _, credential);

        await identity.ValidateCookieAsync("token", Home);
        Assert.Equal(1, engine.Calls);

        File.Delete(CredentialPath);
        await identity.ValidateCookieAsync("token", Home);

        // The call goes out (and the credential handler, absent in this test pipeline, is what refuses it in the app).
        Assert.Equal(2, engine.Calls);
    }

    [Fact]
    public void KeysNeverMatchAcrossTokenIngressOrCredential_AndNeverContainTheToken()
    {
        var baseKey = SessionValidationCache.KeyFor("token", Home, "credential");

        Assert.NotEqual(baseKey, SessionValidationCache.KeyFor("token2", Home, "credential"));
        Assert.NotEqual(baseKey, SessionValidationCache.KeyFor("token", ClientIngressValues.Remote, "credential"));
        Assert.NotEqual(baseKey, SessionValidationCache.KeyFor("token", Home, "credential2"));
        Assert.NotEqual(baseKey, SessionValidationCache.KeyFor("token", Home, null));
        Assert.DoesNotContain("token", baseKey, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(baseKey, SessionValidationCache.KeyFor("token", Home, "credential"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CredentialPath => Path.Combine(_root, "config", ".secrets", "dashboard-engine.credential.json");

    private DashboardServiceCredentialProvider CreateCredentialProvider(IDataProtectionProvider protection) =>
        new(protection, new DashboardServiceCredentialProviderOptions(Path.Combine(_root, "config")),
            NullLogger<DashboardServiceCredentialProvider>.Instance);

    private void WriteBundle(IDataProtectionProvider protection, string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CredentialPath)!);
        var bundle = new DashboardServiceCredentialBundle(
            Guid.NewGuid().ToString("N"),
            protection.CreateProtector("Tuvima.DashboardEngineCredential.v1").Protect(token),
            DateTimeOffset.UtcNow);
        File.WriteAllText(CredentialPath, JsonSerializer.Serialize(bundle));
    }

    // The identity client talks through the same invalidation handler the app puts in front of the "EngineIdentity" client.
    private DashboardIdentityClient CreateIdentity(
        EngineSpy engine,
        out HttpClient http,
        DashboardServiceCredentialProvider? credential = null)
    {
        var cache = new SessionValidationCache(_clock);
        engine.Clock = _clock;
        http = new HttpClient(new SessionValidationInvalidationHandler(cache) { InnerHandler = engine }, disposeHandler: false)
        {
            BaseAddress = new Uri("http://engine.test"),
        };
        return new DashboardIdentityClient(
            new SingleClientFactory(http), validationCache: cache, serviceCredential: credential);
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class EngineSpy : HttpMessageHandler
    {
        private int _calls;
        private int _validateCalls;

        public int Calls => _calls;

        /// <summary>Only the sign-in checks (POSTs to <c>/auth/session/validate</c>).</summary>
        public int ValidateCalls => _validateCalls;

        public TaskCompletionSource? Gate { get; set; }
        public bool ChangeCallsAreNotGated { get; set; }
        public Func<HttpRequestMessage, HttpResponseMessage>? RespondWith { get; set; }
        public DateTimeOffset? SessionEndsAt { get; set; }
        public TimeProvider? Clock { get; set; }

        public async Task WaitForCallsAsync(int expected)
        {
            for (var attempt = 0; attempt < 500 && Volatile.Read(ref _calls) < expected; attempt++)
            {
                await Task.Delay(10);
            }

            Assert.True(Volatile.Read(ref _calls) >= expected, $"The Engine saw {_calls} calls, expected at least {expected}.");
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            var isCheck = request.RequestUri!.AbsolutePath == "/auth/session/validate" && request.Method == HttpMethod.Post;
            if (isCheck)
            {
                Interlocked.Increment(ref _validateCalls);
            }

            if (Gate is { } gate && !(ChangeCallsAreNotGated && !isCheck))
            {
                await gate.Task.ConfigureAwait(false);
            }

            return RespondWith?.Invoke(request) ?? Valid();
        }

        private HttpResponseMessage Valid()
        {
            var profile = Guid.NewGuid();
            var authority = new DashboardAuthorityResponse(
                profile, profile, true, true, 1, 1, true, true, null, 1, [], ["settings.administration"], ["access.manage"]);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SessionValidationResponse
                {
                    SessionId = Guid.NewGuid(),
                    AccountId = profile,
                    ActiveProfileId = profile,
                    DisplayName = "Profile",
                    Authority = authority,
                    AuthenticationMethod = "test",
                    ExpiresAt = SessionEndsAt ?? (Clock ?? TimeProvider.System).GetUtcNow().AddHours(1),
                }),
            };
        }
    }
}
