using System.Net;
using System.Threading.RateLimiting;
using MediaEngine.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Every Dashboard tab asks the Engine for a short-lived live-updates token and then opens the live connection, and
/// all of it reaches the Engine from the Dashboard's own address. Ordinary use (finishing setup, reloading, several
/// tabs, a dropped connection coming back) must never be refused, while callers without the Dashboard credential
/// keep a small allowance.
/// </summary>
public sealed class LiveUpdatesRateLimitPoliciesTests
{
    private const string DashboardToken = "dashboard-service-token";
    private const string DashboardAddress = "127.0.0.1";

    [Fact]
    public void SetupReloadsTabsAndReconnects_WithinOneMinute_AreNeverRefused()
    {
        using var services = Services();
        using var engine = new EngineLimiters();
        var refused = new List<string>();

        // One person finishes setup (three full page loads), reloads four times and opens three more tabs; a second
        // person in the house opens two tabs. Every tab's live connection then drops once and comes back.
        var circuits = Enumerable.Range(0, 10).Select(index => ("session-a", $"tab-a{index}"))
            .Concat(Enumerable.Range(0, 2).Select(index => ("session-b", $"tab-b{index}")))
            .ToList();

        foreach (var (session, tab) in circuits)
        {
            Request(engine, LiveUpdatesRateLimitPolicies.TokenMint, TokenMint(services, session), $"{tab} token", refused);
            OpenHub(engine, LiveUpdatesRateLimitPolicies.IntercomHub, services, $"{tab} connect", refused);
        }

        foreach (var (_, tab) in circuits)
        {
            OpenHub(engine, LiveUpdatesRateLimitPolicies.IntercomHub, services, $"{tab} reconnect", refused);
        }

        // A paired app on the Dashboard's edge listens for its own events through the same address.
        OpenHub(engine, LiveUpdatesRateLimitPolicies.ApplicationEventsHub, services, "app events", refused);

        Assert.True(refused.Count == 0, "Refused: " + string.Join(", ", refused));
    }

    [Fact]
    public void OneSessionMintingRepeatedly_CannotUseUpAnotherSessionsAllowance()
    {
        using var services = Services();
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(LiveUpdatesRateLimitPolicies.ForTokenMint);

        for (var index = 0; index < LiveUpdatesRateLimitPolicies.TokenMintPerSessionPermitLimit; index++)
        {
            using var lease = limiter.AttemptAcquire(TokenMint(services, "busy-session"));
            Assert.True(lease.IsAcquired, $"Mint {index + 1} was refused.");
        }

        using var overLimit = limiter.AttemptAcquire(TokenMint(services, "busy-session"));
        Assert.False(overLimit.IsAcquired);

        using var otherSession = limiter.AttemptAcquire(TokenMint(services, "quiet-session"));
        Assert.True(otherSession.IsAcquired);
    }

    [Fact]
    public void TokenMintWithoutTheDashboardCredential_KeepsTheSmallPerAddressAllowance()
    {
        using var services = Services();
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(LiveUpdatesRateLimitPolicies.ForTokenMint);

        for (var index = 0; index < AuthenticationRateLimitPartition.PerAddressPermitLimit; index++)
        {
            using var lease = limiter.AttemptAcquire(Context(services, "203.0.113.9", "not-the-key", "some-session"));
            Assert.True(lease.IsAcquired);
        }

        using var overLimit = limiter.AttemptAcquire(Context(services, "203.0.113.9", "not-the-key", "another-session"));
        Assert.False(overLimit.IsAcquired);
    }

    [Fact]
    public void HubConnections_AreStillLimitedPerAddress()
    {
        using var services = Services();
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(LiveUpdatesRateLimitPolicies.ForHub);

        for (var index = 0; index < LiveUpdatesRateLimitPolicies.HubPerAddressPermitLimit; index++)
        {
            using var lease = limiter.AttemptAcquire(Context(services, "203.0.113.9", null, null));
            Assert.True(lease.IsAcquired);
        }

        using var overLimit = limiter.AttemptAcquire(Context(services, "203.0.113.9", null, null));
        Assert.False(overLimit.IsAcquired);

        using var otherAddress = limiter.AttemptAcquire(Context(services, "203.0.113.10", null, null));
        Assert.True(otherAddress.IsAcquired);
    }

    private static void OpenHub(EngineLimiters engine, string policy, ServiceProvider services, string label, List<string> refused)
    {
        // SignalR negotiates first and then opens the transport; both pass through the limiter.
        Request(engine, policy, Context(services, DashboardAddress, null, null), label + " (negotiate)", refused);
        Request(engine, policy, Context(services, DashboardAddress, null, null), label + " (transport)", refused);
    }

    private static void Request(EngineLimiters engine, string policy, HttpContext context, string label, List<string> refused)
    {
        using var lease = engine.For(policy).AttemptAcquire(context);
        if (!lease.IsAcquired)
        {
            refused.Add(label);
        }
    }

    private static HttpContext TokenMint(ServiceProvider services, string session) =>
        Context(services, DashboardAddress, DashboardToken, session);

    private static ServiceProvider Services()
    {
        var recognizer = new DashboardServiceCredentialRecognizer();
        recognizer.Remember(DashboardToken);
        return new ServiceCollection().AddSingleton(recognizer).BuildServiceProvider();
    }

    private static DefaultHttpContext Context(ServiceProvider services, string address, string? serviceKey, string? session)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        if (serviceKey is not null)
        {
            context.Request.Headers[TuvimaAuthDefaults.ServiceHeader] = serviceKey;
        }

        if (session is not null)
        {
            context.Request.Headers[TuvimaAuthDefaults.SessionHeader] = session;
        }

        return context;
    }

    /// <summary>One limiter per policy name, as ASP.NET Core keeps them: routes that share a name share a budget.</summary>
    private sealed class EngineLimiters : IDisposable
    {
        private readonly Dictionary<string, PartitionedRateLimiter<HttpContext>> _limiters = new(StringComparer.Ordinal);

        public PartitionedRateLimiter<HttpContext> For(string policy)
        {
            if (!_limiters.TryGetValue(policy, out var limiter))
            {
                limiter = PartitionedRateLimiter.Create(LiveUpdatesRateLimitPolicies.PartitionerFor(policy));
                _limiters[policy] = limiter;
            }

            return limiter;
        }

        public void Dispose()
        {
            foreach (var limiter in _limiters.Values)
            {
                limiter.Dispose();
            }
        }
    }
}
