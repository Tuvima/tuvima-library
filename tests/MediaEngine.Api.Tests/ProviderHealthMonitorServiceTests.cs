using System.Net;
using MediaEngine.Api.Services;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A provider marked Down must be re-probed on its configured endpoint and recover without a
/// restart; a provider with no probe endpoint turns half-open when its check time passes.
/// </summary>
public sealed class ProviderHealthMonitorServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tuvima-provider-health", Guid.NewGuid().ToString("N"));
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly InMemoryHealthRepository _repo = new();
    private readonly ProbeHandler _handler = new(HttpStatusCode.OK);

    public ProviderHealthMonitorServiceTests()
    {
        Directory.CreateDirectory(_root);
        _loader = new ConfigurationDirectoryLoader(_root);
    }

    [Fact]
    public async Task Probe_UsesConfiguredApiEndpointAndRecoversProvider()
    {
        AddProvider("alpha", new Dictionary<string, string>
        {
            ["other"] = "https://other.example/x",
            ["api"] = "https://alpha.example/v1",
        });
        _repo.Seed(Down("alpha", nextCheckAt: null)); // a NULL next_check_at is due immediately
        var service = CreateService();
        await service.RefreshCacheAsync(CancellationToken.None);

        await service.RunActiveProbesAsync(CancellationToken.None);

        Assert.Equal(HttpMethod.Get, _handler.LastMethod);
        Assert.Equal("https://alpha.example/v1", _handler.LastUri?.GetLeftPart(UriPartial.Path));
        Assert.Equal(ProviderHealthStatus.Healthy, _repo.Records["alpha"].Status);
        Assert.Equal(ProviderHealthStatus.Healthy, service.GetStatus("alpha"));
        Assert.False(service.IsDown("alpha"));
    }

    [Fact]
    public async Task Probe_FallsBackToTheFirstAbsoluteHttpEndpointWhenThereIsNoApiEndpoint()
    {
        AddProvider("alpha", new Dictionary<string, string>
        {
            ["relative"] = "/not/absolute",
            ["files"] = "ftp://alpha.example/data",
            ["mirror"] = "https://mirror.example/lookup",
        });
        _repo.Seed(Down("alpha", nextCheckAt: DateTimeOffset.UtcNow.AddMinutes(-1)));
        var service = CreateService();

        await service.RunActiveProbesAsync(CancellationToken.None);

        Assert.Equal("https://mirror.example/lookup", _handler.LastUri?.GetLeftPart(UriPartial.Path));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Probe_AnyAnswerBelow500OtherThan429_CountsAsRecovered(HttpStatusCode status)
    {
        AddProvider("alpha", new Dictionary<string, string> { ["api"] = "https://alpha.example/v1" });
        _repo.Seed(Down("alpha", nextCheckAt: null));
        _handler.Status = status;
        var service = CreateService();

        await service.RunActiveProbesAsync(CancellationToken.None);

        Assert.Equal(ProviderHealthStatus.Healthy, _repo.Records["alpha"].Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Probe_ServerErrorOrRateLimit_KeepsProviderDownAndSchedulesNextCheck(HttpStatusCode status)
    {
        AddProvider("alpha", new Dictionary<string, string> { ["api"] = "https://alpha.example/v1" });
        _repo.Seed(Down("alpha", nextCheckAt: null));
        _handler.Status = status;
        var service = CreateService();

        await service.RunActiveProbesAsync(CancellationToken.None);

        var record = _repo.Records["alpha"];
        Assert.Equal(ProviderHealthStatus.Down, record.Status);
        Assert.True(record.NextCheckAt > DateTimeOffset.UtcNow);
        Assert.True(service.IsDown("alpha"));
    }

    [Fact]
    public async Task Probe_ConnectionFailure_KeepsProviderDownAndSchedulesNextCheck()
    {
        AddProvider("alpha", new Dictionary<string, string> { ["api"] = "https://alpha.example/v1" });
        _repo.Seed(Down("alpha", nextCheckAt: null));
        _handler.Failure = new HttpRequestException("connection refused");
        var service = CreateService();

        await service.RunActiveProbesAsync(CancellationToken.None);

        Assert.Equal(ProviderHealthStatus.Down, _repo.Records["alpha"].Status);
        Assert.True(_repo.Records["alpha"].NextCheckAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Probe_SkipsProvidersWhoseNextCheckIsStillInTheFuture()
    {
        AddProvider("alpha", new Dictionary<string, string> { ["api"] = "https://alpha.example/v1" });
        _repo.Seed(Down("alpha", nextCheckAt: DateTimeOffset.UtcNow.AddMinutes(10)));
        var service = CreateService();
        await service.RefreshCacheAsync(CancellationToken.None);

        await service.RunActiveProbesAsync(CancellationToken.None);

        Assert.Equal(0, _handler.RequestCount);
        Assert.True(service.IsDown("alpha"));
    }

    [Fact]
    public async Task NoProbeEndpoint_IsHalfOpenOnceTheCheckTimePasses()
    {
        AddProvider("alpha", new Dictionary<string, string>());
        _repo.Seed(Down("alpha", nextCheckAt: DateTimeOffset.UtcNow.AddMinutes(10)));
        _repo.Seed(Down("beta", nextCheckAt: DateTimeOffset.UtcNow.AddMinutes(-1)));
        _repo.Seed(Down("gamma", nextCheckAt: null));
        var service = CreateService();
        await service.RefreshCacheAsync(CancellationToken.None);

        await service.RunActiveProbesAsync(CancellationToken.None);

        Assert.True(service.IsDown("alpha"));   // check time not reached: still blocked
        Assert.False(service.IsDown("beta"));   // check time passed: let one real request through
        Assert.False(service.IsDown("gamma"));  // never scheduled: due now
        Assert.Equal(0, _handler.RequestCount); // nothing to probe without an endpoint
    }

    [Fact]
    public async Task HalfOpen_RealFailureBlocksAgainAndRealSuccessRecovers()
    {
        AddProvider("alpha", new Dictionary<string, string>());
        _repo.Seed(Down("alpha", nextCheckAt: DateTimeOffset.UtcNow.AddMinutes(-1)));
        var service = CreateService();
        await service.RefreshCacheAsync(CancellationToken.None);
        Assert.False(service.IsDown("alpha"));

        await service.ReportFailureAsync("alpha", "timeout");
        Assert.True(service.IsDown("alpha"));

        // Time passes again: the next real request is allowed through and succeeds.
        _repo.Records["alpha"].NextCheckAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await service.RefreshCacheAsync(CancellationToken.None);
        Assert.False(service.IsDown("alpha"));
        await service.ReportSuccessAsync("alpha");

        Assert.Equal(ProviderHealthStatus.Healthy, service.GetStatus("alpha"));
        Assert.False(service.IsDown("alpha"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private ProviderHealthMonitorService CreateService() => new(
        _repo,
        new HandlerHttpClientFactory(_handler),
        new NullEvents(),
        NullLogger<ProviderHealthMonitorService>.Instance,
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
        _loader);

    private void AddProvider(string name, Dictionary<string, string> endpoints) =>
        _loader.SaveProvider(new ProviderConfiguration
        {
            Name = name,
            DisplayName = name,
            ProviderId = Guid.NewGuid().ToString(),
            Enabled = true,
            Endpoints = endpoints,
        });

    private static ProviderHealthRecord Down(string name, DateTimeOffset? nextCheckAt) => new()
    {
        ProviderId = name,
        Status = ProviderHealthStatus.Down,
        ConsecutiveFailures = 3,
        DownSince = DateTimeOffset.UtcNow.AddHours(-2),
        NextCheckAt = nextCheckAt,
    };

    public void Dispose()
    {
        _loader.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ProbeHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;
        public Exception? Failure { get; set; }
        public int RequestCount { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastMethod = request.Method;
            LastUri = request.RequestUri;
            return Failure is not null
                ? Task.FromException<HttpResponseMessage>(Failure)
                : Task.FromResult(new HttpResponseMessage(Status));
        }
    }

    private sealed class HandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class NullEvents : IEventPublisher
    {
        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull => Task.CompletedTask;
    }

    private sealed class InMemoryHealthRepository : IProviderHealthRepository
    {
        public Dictionary<string, ProviderHealthRecord> Records { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Seed(ProviderHealthRecord record) => Records[record.ProviderId] = record;

        public Task<ProviderHealthRecord?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult(Records.GetValueOrDefault(providerId));

        public Task<IReadOnlyList<ProviderHealthRecord>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProviderHealthRecord>>(Records.Values.ToList());

        public Task<IReadOnlyList<ProviderHealthRecord>> GetDownProvidersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProviderHealthRecord>>(
                Records.Values.Where(record => record.Status == ProviderHealthStatus.Down).ToList());

        public Task UpsertAsync(ProviderHealthRecord record, CancellationToken ct = default)
        {
            Records[record.ProviderId] = record;
            return Task.CompletedTask;
        }

        public Task<bool> RecordSuccessAsync(string providerId, CancellationToken ct = default)
        {
            var wasDown = Records.TryGetValue(providerId, out var record) && record.Status == ProviderHealthStatus.Down;
            Records[providerId] = new ProviderHealthRecord
            {
                ProviderId = providerId,
                Status = ProviderHealthStatus.Healthy,
                LastSuccessAt = DateTimeOffset.UtcNow,
            };
            return Task.FromResult(wasDown);
        }

        // Mirrors ProviderHealthRepository: Down from the third consecutive failure, next check in 5 minutes.
        public Task<ProviderHealthStatus> RecordFailureAsync(string providerId, string reason, CancellationToken ct = default)
        {
            Records.TryGetValue(providerId, out var current);
            var failures = (current?.ConsecutiveFailures ?? 0) + 1;
            var status = failures >= 3 ? ProviderHealthStatus.Down : ProviderHealthStatus.Degraded;
            Records[providerId] = new ProviderHealthRecord
            {
                ProviderId = providerId,
                Status = status,
                ConsecutiveFailures = failures,
                LastFailureReason = reason,
                DownSince = current?.DownSince ?? DateTimeOffset.UtcNow,
                NextCheckAt = DateTimeOffset.UtcNow.AddMinutes(5),
            };
            return Task.FromResult(status);
        }
    }
}
