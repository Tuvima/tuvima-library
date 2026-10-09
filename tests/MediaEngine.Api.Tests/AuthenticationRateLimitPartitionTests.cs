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

    private static DefaultHttpContext SessionContext(ServiceProvider services, string? serviceKey, string? sessionToken)
    {
        var context = Context(services, "127.0.0.1", serviceKey);
        if (sessionToken is not null)
        {
            context.Request.Headers[TuvimaAuthDefaults.SessionHeader] = sessionToken;
        }

        return context;
    }

    [Fact]
    public void ProfileSwitching_GetsTheSessionsOwnAllowance_AndNeverTouchesTheSharedDashboardOne()
    {
        using var services = Services();
        using var switching = PartitionedRateLimiter.Create<HttpContext, string>(AuthenticationRateLimitPartition.ForSession);
        using var signIn = PartitionedRateLimiter.Create<HttpContext, string>(AuthenticationRateLimitPartition.For);

        for (var index = 0; index < AuthenticationRateLimitPartition.PerSessionPermitLimit; index++)
        {
            using var lease = switching.AttemptAcquire(SessionContext(services, DashboardToken, "session-a"));
            Assert.True(lease.IsAcquired, $"Switch {index + 1} was limited.");
        }

        using (var limited = switching.AttemptAcquire(SessionContext(services, DashboardToken, "session-a")))
        {
            Assert.False(limited.IsAcquired);
        }

        // Another session, and every Dashboard sign-in, keep their full share.
        using (var other = switching.AttemptAcquire(SessionContext(services, DashboardToken, "session-b")))
        {
            Assert.True(other.IsAcquired);
        }

        for (var index = 0; index < AuthenticationRateLimitPartition.DashboardPermitLimit; index++)
        {
            using var lease = signIn.AttemptAcquire(SessionContext(services, DashboardToken, "session-a"));
            Assert.True(lease.IsAcquired, $"Dashboard sign-in {index + 1} was limited.");
        }
    }

    [Fact]
    public void ProfileSwitching_WithoutTheDashboardCredentialOrASession_FallsBackToThePerAddressAllowance()
    {
        using var services = Services();
        using var switching = PartitionedRateLimiter.Create<HttpContext, string>(AuthenticationRateLimitPartition.ForSession);

        for (var index = 0; index < AuthenticationRateLimitPartition.PerAddressPermitLimit; index++)
        {
            using var lease = switching.AttemptAcquire(SessionContext(services, null, "session-a"));
            Assert.True(lease.IsAcquired);
        }

        using var limited = switching.AttemptAcquire(SessionContext(services, null, "session-a"));
        Assert.False(limited.IsAcquired);
        // A Dashboard call that carries no session is counted with the Dashboard, as before.
        using var dashboard = switching.AttemptAcquire(SessionContext(services, DashboardToken, null));
        Assert.True(dashboard.IsAcquired);
    }

    [Fact]
    public void SwitchProfileFailure_MapsALockedProfileTo429AndAMissingPinTo428()
    {
        var locked = Assert.IsAssignableFrom<IStatusCodeHttpResult>(
            MediaEngine.Api.Endpoints.AuthenticationEndpoints.SwitchProfileFailure(new MediaEngine.Identity.ProfilePinLockedException()));
        var required = Assert.IsAssignableFrom<IStatusCodeHttpResult>(
            MediaEngine.Api.Endpoints.AuthenticationEndpoints.SwitchProfileFailure(new MediaEngine.Identity.ProfilePinRequiredException()));
        var unknown = Assert.IsAssignableFrom<IStatusCodeHttpResult>(
            MediaEngine.Api.Endpoints.AuthenticationEndpoints.SwitchProfileFailure(new KeyNotFoundException("No such profile.")));
        var denied = Assert.IsAssignableFrom<IStatusCodeHttpResult>(
            MediaEngine.Api.Endpoints.AuthenticationEndpoints.SwitchProfileFailure(new UnauthorizedAccessException()));

        Assert.Equal(StatusCodes.Status429TooManyRequests, locked.StatusCode);
        Assert.Equal(StatusCodes.Status428PreconditionRequired, required.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, unknown.StatusCode);
        Assert.Equal(StatusCodes.Status401Unauthorized, denied.StatusCode);
    }
}
