using System.Net;
using MediaEngine.Web.Services.Configuration;
using MediaEngine.Web.Services.Integration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    [InlineData(IngressKind.ThisComputer)]
    [InlineData(IngressKind.HomeNetwork)]
    public void NonRemoteCallerWithoutAnAddress_IsAllowedAndNotCounted(IngressKind kind)
    {
        var limiter = Limiter(new ManualClock());

        for (var index = 0; index < 50; index++)
        {
            Assert.Equal(SignInAttemptResult.Allowed, limiter.Acquire((IPAddress?)null, kind, out _));
        }

        Assert.Equal(0, limiter.TrackedKeyCount);
    }

    private sealed class CapturingLogger : ILogger<SignInAttemptLimiter>
    {
        public List<LogLevel> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(logLevel);
    }

    private static DefaultHttpContext ProxyContext(int localPort)
    {
        var context = Context("127.0.0.1");
        context.Connection.LocalPort = localPort;
        return context;
    }

    [Fact]
    public void ProxyPortAndDirectLoopbackVisitors_AreNotRefused()
    {
        var classifier = new IngressClassifier(proxyPort: 5017, trustedLocalNetworks: null, trustedProxies: ["127.0.0.1"]);
        var logger = new CapturingLogger();
        var limiter = new SignInAttemptLimiter(classifier, new ManualClock(), logger);

        // Through the proxy port, forwarded headers give the real visitor address and the proxy is fine.
        var viaProxyPort = Context("203.0.113.9");
        viaProxyPort.Connection.LocalPort = 5017;
        Assert.Null(limiter.PlaceRefusal(viaProxyPort));
        Assert.Equal(SignInAttemptResult.Allowed, limiter.Acquire(viaProxyPort, out _));

        // A visitor connecting straight to the main port (no trusted proxy is configured here), such as the owner on this computer.
        Assert.Null(new SignInAttemptLimiter(new IngressClassifier(5017, null), new ManualClock(), logger).PlaceRefusal(ProxyContext(5016)));
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData("127.0.0.1", 5016, null, true)]
    [InlineData("127.0.0.1", 5017, 5017, false)]
    [InlineData("127.0.0.2", 5016, null, false)]
    [InlineData("::ffff:127.0.0.1", 5016, 5017, true)]
    public void IsProxyOnMainPort_OnlyFlagsAConfiguredProxyOffTheProxyPort(string address, int localPort, int? proxyPort, bool expected)
    {
        var classifier = new IngressClassifier(proxyPort, null, trustedProxies: ["127.0.0.1"]);

        Assert.Equal(expected, classifier.IsProxyOnMainPort(IPAddress.Parse(address), localPort));
        Assert.False(classifier.IsProxyOnMainPort(null, localPort));
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

}
