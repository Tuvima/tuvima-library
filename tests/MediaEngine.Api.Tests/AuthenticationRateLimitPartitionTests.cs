using System.Net;
using System.Threading.RateLimiting;
using MediaEngine.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

public sealed class AuthenticationRateLimitPartitionTests
{
    private const string DashboardToken = "dashboard-service-token";

    private static ServiceProvider Services(bool rememberDashboard = true)
    {
        var recognizer = new DashboardServiceCredentialRecognizer();
        if (rememberDashboard)
        {
            recognizer.Remember(DashboardToken);
        }

        return new ServiceCollection().AddSingleton(recognizer).BuildServiceProvider();
    }

    private static DefaultHttpContext Context(ServiceProvider services, string address, string? serviceKey)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        if (serviceKey is not null)
        {
            context.Request.Headers[TuvimaAuthDefaults.ServiceHeader] = serviceKey;
        }

        return context;
    }

    [Fact]
    public void DashboardCredentialCalls_AreNotLimitedAtFiftyPerMinute()
    {
        using var services = Services();
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(AuthenticationRateLimitPartition.For);

        for (var index = 0; index < 50; index++)
        {
            using var lease = limiter.AttemptAcquire(Context(services, "127.0.0.1", DashboardToken));
            Assert.True(lease.IsAcquired, $"Dashboard call {index + 1} was limited.");
        }
    }

    [Fact]
    public void OtherCallers_AreLimitedAtTenPerMinutePerAddress()
    {
        using var services = Services();
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(AuthenticationRateLimitPartition.For);

        for (var index = 0; index < 10; index++)
        {
            using var lease = limiter.AttemptAcquire(Context(services, "203.0.113.9", null));
            Assert.True(lease.IsAcquired);
        }

        using var eleventh = limiter.AttemptAcquire(Context(services, "203.0.113.9", null));
        Assert.False(eleventh.IsAcquired);

        using var other = limiter.AttemptAcquire(Context(services, "203.0.113.10", null));
        Assert.True(other.IsAcquired);
    }

    [Fact]
    public void WrongServiceKey_GetsTheSmallPerAddressAllowance()
    {
        using var services = Services();
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(AuthenticationRateLimitPartition.For);

        for (var index = 0; index < 10; index++)
        {
            using var lease = limiter.AttemptAcquire(Context(services, "203.0.113.9", "not-the-key"));
            Assert.True(lease.IsAcquired);
        }

        using var eleventh = limiter.AttemptAcquire(Context(services, "203.0.113.9", "not-the-key"));
        Assert.False(eleventh.IsAcquired);
    }

    [Fact]
    public void BeforeTheCredentialIsKnown_EveryoneKeepsThePerAddressAllowance()
    {
        using var services = Services(rememberDashboard: false);
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(AuthenticationRateLimitPartition.For);

        for (var index = 0; index < 10; index++)
        {
            using var lease = limiter.AttemptAcquire(Context(services, "127.0.0.1", DashboardToken));
            Assert.True(lease.IsAcquired);
        }

        using var eleventh = limiter.AttemptAcquire(Context(services, "127.0.0.1", DashboardToken));
        Assert.False(eleventh.IsAcquired);
    }
}
