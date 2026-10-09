using System.Net;
using System.Net.Http.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Http;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Dashboard's own limit on profile-PIN guesses. It is kept separately for remote and home callers, so a remote
/// guess run (or the Engine's lock on it) can never stop someone at home switching into the same profile.
/// </summary>
public sealed class ProfileSwitchLimitTests
{
    private static readonly Guid Profile = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private sealed class CountingHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(response());
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("http://engine.test") };
    }

    private static DashboardIdentityClient Client(CountingHandler handler, SignInAttemptLimiter limiter, string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Connection.LocalPort = 5016;
        return new DashboardIdentityClient(
            new Factory(handler),
            new HttpContextAccessor { HttpContext = context },
            ingress: Classifier,
            signInLimiter: limiter);
    }

    private static readonly IngressClassifier Classifier = new(proxyPort: null, trustedLocalNetworks: null);

    private static SignInAttemptLimiter NewLimiter() => new(Classifier);

    private static HttpResponseMessage PinRequired() => new(HttpStatusCode.PreconditionRequired);

    private static HttpResponseMessage Switched() => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new SessionValidationResponse
        {
            Authority = new DashboardAuthorityResponse(Guid.Empty, Profile, true, true, 1, 1, false, false, null, 1, [], [], []),
        }),
    };

    private static Task<DashboardProfileSwitchResult> Guess(DashboardIdentityClient client, string secret = "0000") =>
        client.SwitchProfileAsync(new SwitchProfileRequest { ProfileId = Profile, Secret = secret });

    private static async Task ExhaustAsync(DashboardIdentityClient client)
    {
        for (var index = 0; index < DashboardIdentityClient.ProfilePinAttemptsPerMinute; index++)
        {
            Assert.Equal(DashboardProfileSwitchStatus.PinRequired, (await Guess(client)).Status);
        }

        Assert.Equal(DashboardProfileSwitchStatus.TooManyAttempts, (await Guess(client)).Status);
    }

    [Fact]
    public async Task RemoteGuessRun_DoesNotBlockHomeSwitchingIntoTheSameProfile()
    {
        var limiter = NewLimiter();
        var remoteHandler = new CountingHandler(PinRequired);
        var homeHandler = new CountingHandler(Switched);

        await ExhaustAsync(Client(remoteHandler, limiter, "203.0.113.9"));
        // Once the Dashboard limit is reached the Engine is not even asked.
        Assert.Equal(DashboardIdentityClient.ProfilePinAttemptsPerMinute, remoteHandler.Calls);

        var home = await Guess(Client(homeHandler, limiter, "192.168.1.20"), "2468");

        Assert.Equal(DashboardProfileSwitchStatus.Succeeded, home.Status);
        Assert.Equal(1, homeHandler.Calls);
    }

    [Fact]
    public async Task HomeSwitchSuccess_LeavesTheRemoteLockInPlace()
    {
        var limiter = NewLimiter();
        var remoteHandler = new CountingHandler(PinRequired);
        var remote = Client(remoteHandler, limiter, "203.0.113.9");
        await ExhaustAsync(remote);

        Assert.Equal(DashboardProfileSwitchStatus.Succeeded, (await Guess(Client(new CountingHandler(Switched), limiter, "192.168.1.20"), "2468")).Status);

        Assert.Equal(DashboardProfileSwitchStatus.TooManyAttempts, (await Guess(remote, "2468")).Status);
        Assert.Equal(DashboardIdentityClient.ProfilePinAttemptsPerMinute, remoteHandler.Calls);
    }

    [Fact]
    public async Task HomeGuessRun_IsStillLimitedForHomeCallers()
    {
        var limiter = NewLimiter();
        await ExhaustAsync(Client(new CountingHandler(PinRequired), limiter, "192.168.1.20"));
    }

    [Fact]
    public async Task EngineTooManyAttempts_MapsToTooManyAttempts()
    {
        var limiter = NewLimiter();
        var client = Client(new CountingHandler(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests)), limiter, "203.0.113.9");

        Assert.Equal(DashboardProfileSwitchStatus.TooManyAttempts, (await Guess(client)).Status);
        // A switch without a PIN is not a guess, so the Engine's answer is passed through the same way.
        Assert.Equal(DashboardProfileSwitchStatus.TooManyAttempts, (await client.SwitchProfileAsync(new SwitchProfileRequest { ProfileId = Profile })).Status);
    }
}
