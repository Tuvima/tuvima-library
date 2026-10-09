using System.Net;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class SignInAttemptLimiterTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static DefaultHttpContext Context(string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Connection.LocalPort = 5016;
        return context;
    }

    private static SignInAttemptLimiter Limiter(ManualClock clock) =>
        new(new IngressClassifier(proxyPort: null, trustedLocalNetworks: null), clock);

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("127.0.0.1")]
    public void HomeAndThisComputerAddresses_GetTenAttemptsAMinute(string address)
    {
        var limiter = Limiter(new ManualClock());
        var context = Context(address);
        for (var index = 0; index < 10; index++)
        {
            Assert.True(limiter.TryAcquire(context, out _), $"Attempt {index + 1} was refused.");
        }

        Assert.False(limiter.TryAcquire(context, out var retryAfter));
        Assert.InRange(retryAfter, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquire(Context(address == "127.0.0.1" ? "192.168.1.21" : "192.168.1.22"), out _));
    }

    [Fact]
    public void RemoteAddresses_GetFiveAttemptsAMinute_AndOtherAddressesAreUnaffected()
    {
        var limiter = Limiter(new ManualClock());
        var attacker = Context("203.0.113.9");
        for (var index = 0; index < 5; index++)
        {
            Assert.True(limiter.TryAcquire(attacker, out _));
        }

        Assert.False(limiter.TryAcquire(attacker, out _));
        Assert.True(limiter.TryAcquire(Context("203.0.113.10"), out _));
        Assert.True(limiter.TryAcquire(Context("192.168.1.20"), out _));
    }

    [Fact]
    public void AllowanceReturnsAfterAMinute()
    {
        var clock = new ManualClock();
        var limiter = Limiter(clock);
        var context = Context("203.0.113.9");
        for (var index = 0; index < 5; index++)
        {
            limiter.TryAcquire(context, out _);
        }

        Assert.False(limiter.TryAcquire(context, out _));
        clock.Now += TimeSpan.FromSeconds(61);
        Assert.True(limiter.TryAcquire(context, out _));
    }

    [Fact]
    public void AddressThatCannotBePlaced_SharesOneRemoteKey()
    {
        var limiter = Limiter(new ManualClock());
        for (var index = 0; index < SignInAttemptLimiter.RemotePerMinute; index++)
        {
            Assert.True(limiter.TryAcquire((IPAddress?)null, IngressKind.Remote, out _));
        }

        Assert.False(limiter.TryAcquire((IPAddress?)null, IngressKind.Remote, out _));
    }

    [Fact]
    public void RemoteIpv6_IsKeyedByItsSlash64()
    {
        var limiter = Limiter(new ManualClock());
        for (var index = 0; index < SignInAttemptLimiter.RemotePerMinute; index++)
        {
            Assert.True(limiter.TryAcquire(IPAddress.Parse($"2001:db8:1:2::{index + 1}"), IngressKind.Remote, out _));
        }

        Assert.False(limiter.TryAcquire(IPAddress.Parse("2001:db8:1:2:ffff::9"), IngressKind.Remote, out _));
        Assert.True(limiter.TryAcquire(IPAddress.Parse("2001:db8:1:3::1"), IngressKind.Remote, out _));
    }

    [Fact]
    public void AllRemoteVisitorsTogether_AreCappedSoHomeSignInsKeepAShare()
    {
        var limiter = Limiter(new ManualClock());
        var granted = 0;
        for (var index = 0; index < 400; index++)
        {
            var address = IPAddress.Parse($"198.51.{index / 200}.{index % 200 + 1}");
            if (limiter.TryAcquire(address, IngressKind.Remote, out _))
            {
                granted++;
            }
        }

        Assert.Equal(SignInAttemptLimiter.RemoteTotalPerMinute, granted);
        Assert.True(limiter.TryAcquire(IPAddress.Parse("192.168.1.20"), IngressKind.HomeNetwork, out _));
    }

    [Fact]
    public void ExpiredEntries_AreSweptOncePerWindow()
    {
        var clock = new ManualClock();
        var limiter = Limiter(clock);
        for (var index = 0; index < 50; index++)
        {
            Assert.True(limiter.TryAcquire(IPAddress.Parse($"192.168.5.{index + 1}"), IngressKind.HomeNetwork, out _));
        }

        Assert.Equal(50, limiter.TrackedKeyCount);
        clock.Now += TimeSpan.FromSeconds(30);
        limiter.TryAcquire(IPAddress.Parse("192.168.9.9"), IngressKind.HomeNetwork, out _);
        Assert.Equal(51, limiter.TrackedKeyCount);

        clock.Now += TimeSpan.FromSeconds(61);
        limiter.TryAcquire(IPAddress.Parse("192.168.9.10"), IngressKind.HomeNetwork, out _);
        Assert.Equal(1, limiter.TrackedKeyCount);
    }

    [Fact]
    public void TableIsCapped_NewAddressesAreRefusedWhileExistingOnesKeepWorking()
    {
        var limiter = Limiter(new ManualClock());
        var first = IPAddress.Parse("10.0.0.1");
        Assert.True(limiter.TryAcquire(first, IngressKind.HomeNetwork, out _));
        for (var index = 0; limiter.TrackedKeyCount < SignInAttemptLimiter.MaxTrackedKeys; index++)
        {
            limiter.TryAcquireKey($"fill-{index}", 10, out _);
        }

        Assert.False(limiter.TryAcquire(IPAddress.Parse("10.9.9.9"), IngressKind.HomeNetwork, out _));
        Assert.True(limiter.TryAcquire(first, IngressKind.HomeNetwork, out _));
    }

    [Fact]
    public void KeyedAttempts_AreLimitedPerKey()
    {
        var limiter = Limiter(new ManualClock());
        for (var index = 0; index < 10; index++)
        {
            Assert.True(limiter.TryAcquireKey("switch-pin:a", 10, out _));
        }

        Assert.False(limiter.TryAcquireKey("switch-pin:a", 10, out _));
        Assert.True(limiter.TryAcquireKey("switch-pin:b", 10, out _));
    }

    [Fact]
    public void LoopbackReverseProxy_IsRemote_SoItsVisitorsCountTowardLockoutAndGetTheStricterLimit()
    {
        var classifier = new IngressClassifier(proxyPort: null, trustedLocalNetworks: null, trustedProxies: ["127.0.0.1"]);
        var context = Context("127.0.0.1");

        Assert.Equal(IngressKind.Remote, classifier.Classify(context));
        Assert.Equal(MediaEngine.Contracts.Authentication.ClientIngressValues.Remote, classifier.Classify(context).ToWireValue());

        var limiter = new SignInAttemptLimiter(classifier, new ManualClock());
        for (var index = 0; index < SignInAttemptLimiter.RemotePerMinute; index++)
        {
            Assert.True(limiter.TryAcquire(context, out _));
        }

        Assert.False(limiter.TryAcquire(context, out _));
    }

    [Fact]
    public void Endpoint_Returns429WithRetryAfterOnTheSixthRemoteAttempt()
    {
        var limiter = Limiter(new ManualClock());
        using var services = new ServiceCollection().AddSingleton(limiter).BuildServiceProvider();
        for (var index = 0; index < 5; index++)
        {
            var allowed = Context("203.0.113.9");
            allowed.RequestServices = services;
            Assert.Null(DashboardAuthenticationEndpoints.RejectIfTooManyAttempts(allowed));
        }

        var sixth = Context("203.0.113.9");
        sixth.RequestServices = services;
        var result = DashboardAuthenticationEndpoints.RejectIfTooManyAttempts(sixth);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, status.StatusCode);
        Assert.True(int.Parse(sixth.Response.Headers.RetryAfter.ToString()) >= 1);
    }
}
