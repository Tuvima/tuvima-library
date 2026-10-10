using System.Net;
using System.Threading.RateLimiting;
using MediaEngine.Api.Security;
using MediaEngine.Domain.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

public sealed class ReaderFileRateLimitPartitionTests
{
    private const string DashboardToken = "dashboard-service-token";
    private static readonly RateLimitPolicy PerSession = new() { PermitLimit = 600, WindowMinutes = 1 };
    private static readonly RateLimitPolicy PerAddress = new() { PermitLimit = 100, WindowMinutes = 1 };

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

    [Fact]
    public void ForwardedSessions_GetTheirOwnAllowance_NotTheSharedDashboardAddress()
    {
        using var services = Services();
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(
            context => ReaderFileRateLimitPartition.For(context, PerSession, PerAddress));

        // One person reading a big book uses far more than the per-address 100 a minute...
        for (var index = 0; index < 300; index++)
        {
            using var lease = limiter.AttemptAcquire(Context(services, "127.0.0.1", DashboardToken, "session-a"));
            Assert.True(lease.IsAcquired, $"Request {index + 1} for session A was limited.");
        }

        // ...and that never uses up another person's allowance from the same Dashboard address.
        using var other = limiter.AttemptAcquire(Context(services, "127.0.0.1", DashboardToken, "session-b"));
        Assert.True(other.IsAcquired);
    }

    [Fact]
    public void ASessionIsStillBounded()
    {
        using var services = Services();
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(
            context => ReaderFileRateLimitPartition.For(context, PerSession, PerAddress));

        for (var index = 0; index < PerSession.PermitLimit; index++)
        {
            using var lease = limiter.AttemptAcquire(Context(services, "127.0.0.1", DashboardToken, "session-a"));
            Assert.True(lease.IsAcquired);
        }

        using var over = limiter.AttemptAcquire(Context(services, "127.0.0.1", DashboardToken, "session-a"));
        Assert.False(over.IsAcquired);
    }

    [Theory]
    [InlineData(null, "session-a")]
    [InlineData("wrong-key", "session-a")]
    [InlineData(DashboardToken, null)]
    [InlineData(DashboardToken, "  ")]
    public void AnythingElse_IsCountedPerAddressAtTheStreamingAllowance(string? serviceKey, string? session)
    {
        using var services = Services();
        using var limiter = PartitionedRateLimiter.Create<HttpContext, string>(
            context => ReaderFileRateLimitPartition.For(context, PerSession, PerAddress));

        for (var index = 0; index < PerAddress.PermitLimit; index++)
        {
            using var lease = limiter.AttemptAcquire(Context(services, "203.0.113.9", serviceKey, session));
            Assert.True(lease.IsAcquired);
        }

        using var over = limiter.AttemptAcquire(Context(services, "203.0.113.9", serviceKey, session));
        Assert.False(over.IsAcquired);
        using var otherAddress = limiter.AttemptAcquire(Context(services, "203.0.113.10", serviceKey, session));
        Assert.True(otherAddress.IsAcquired);
    }

    [Fact]
    public void TheSessionTokenItselfIsNeverUsedAsAPartitionKey()
    {
        using var services = Services();

        var partition = ReaderFileRateLimitPartition.For(
            Context(services, "127.0.0.1", DashboardToken, "secret-session-token"), PerSession, PerAddress);

        Assert.StartsWith("reader-session:", partition.PartitionKey, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-session-token", partition.PartitionKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Defaults_GiveTheReaderFarMoreThanGeneralStreaming()
    {
        var defaults = new RateLimitingSettings();

        Assert.True(defaults.ReaderFiles.PermitLimit > defaults.Streaming.PermitLimit);
    }
}
