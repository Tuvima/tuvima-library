using System.Net;
using MediaEngine.Api.Services;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Every enabled provider gets a cached connection check once per Engine start, after first-run
/// setup completes, so Settings never shows "not checked" until someone presses Test.
/// </summary>
public sealed class ProviderStartupCheckServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tuvima-provider-startup", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"tuvima-provider-startup-{Guid.NewGuid():N}.db");
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly DatabaseConnection _database;
    private readonly OnboardingRepository _onboarding;
    private readonly CheckHandler _handler = new();
    private readonly CollectingChecks _checks = new();
    private readonly RecordingHealth _health = new();

    public ProviderStartupCheckServiceTests()
    {
        Directory.CreateDirectory(_root);
        _loader = new ConfigurationDirectoryLoader(_root);
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _onboarding = new OnboardingRepository(_database);

        AddProvider("alpha", enabled: true, withEndpoint: true);
        AddProvider("beta", enabled: true, withEndpoint: true);
        AddProvider("local", enabled: true, withEndpoint: false);
        AddProvider("disabled", enabled: false, withEndpoint: true);
    }

    [Fact]
    public async Task RunChecks_RecordsAConnectionCheckForEveryEnabledProviderAndNoOthers()
    {
        var service = CreateService(gate: null);

        await service.RunChecksAsync(CancellationToken.None);

        Assert.Equal(["alpha", "beta", "local"], _checks.Checks.Keys.OrderBy(name => name).ToArray());
        Assert.Equal("valid", _checks.Checks["alpha"].Status);
        Assert.Equal("local_ready", _checks.Checks["local"].Status);
        Assert.Equal(2, _handler.RequestCount); // local and disabled providers make no request
    }

    [Fact]
    public async Task RunChecks_AFailingProviderDoesNotStopTheOthers()
    {
        _handler.FailFor = "alpha.example";
        var service = CreateService(gate: null);

        await service.RunChecksAsync(CancellationToken.None);

        Assert.Equal("connectivity_failure", _checks.Checks["alpha"].Status);
        Assert.Equal("valid", _checks.Checks["beta"].Status);
    }

    [Fact]
    public async Task RunChecks_SuccessfulCheckClearsStaleDownStatusButFailureDoesNot()
    {
        _health.Statuses["alpha"] = ProviderHealthStatus.Down;
        _health.Statuses["beta"] = ProviderHealthStatus.Down;
        _health.Statuses["local"] = ProviderHealthStatus.Down;
        _handler.FailFor = "beta.example";
        var service = CreateService(gate: null);

        await service.RunChecksAsync(CancellationToken.None);

        Assert.Contains("alpha", _health.Successes);
        Assert.DoesNotContain("beta", _health.Successes);   // the check failed
        Assert.DoesNotContain("local", _health.Successes);  // nothing remote was proven
    }

    [Fact]
    public async Task StartAsync_WaitsForSetupToCompleteBeforeCheckingProviders()
    {
        var gate = new OnboardingActivationGate(_onboarding);
        var service = CreateService(gate);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            Assert.Empty(_checks.Checks);
            Assert.Equal(0, _handler.RequestCount);

            await CompleteOnboardingAsync();

            await WaitUntilAsync(() => _checks.Checks.Count == 3);
            Assert.Equal(["alpha", "beta", "local"], _checks.Checks.Keys.OrderBy(name => name).ToArray());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private ProviderStartupCheckService CreateService(OnboardingActivationGate? gate)
    {
        var credentials = new ProviderCredentialService(
            _loader, new HandlerHttpClientFactory(_handler), [], [], _checks);
        return new ProviderStartupCheckService(
            credentials, _loader, _health, NullLogger<ProviderStartupCheckService>.Instance, gate)
        {
            InterProviderDelay = TimeSpan.Zero,
        };
    }

    private void AddProvider(string name, bool enabled, bool withEndpoint) =>
        _loader.SaveProvider(new ProviderConfiguration
        {
            Name = name,
            DisplayName = name,
            ProviderId = Guid.NewGuid().ToString(),
            Enabled = enabled,
            Endpoints = withEndpoint
                ? new Dictionary<string, string> { ["api"] = $"https://{name}.example/v1" }
                : new Dictionary<string, string>(),
            Onboarding = withEndpoint
                ? new ProviderOnboardingConfiguration
                {
                    Classification = "recommended",
                    SupportedLanes = ["read"],
                    AuthenticationProbe = new ProviderAuthenticationProbeConfiguration
                    {
                        Path = "/ping",
                        Method = "GET",
                        SuccessStatusCodes = [200],
                    },
                }
                : null,
        });

    private async Task CompleteOnboardingAsync()
    {
        await _onboarding.TryBeginAsync("session", Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None);
        foreach (var key in new[] { "preflight", "administrator" })
        {
            await _onboarding.SetStepAsync(key, "passed", null, null, null, CancellationToken.None);
        }

        foreach (var key in new[] { "media-locations", "providers" })
        {
            await _onboarding.SetStepAsync(key, "deferred", "Later", null, null, CancellationToken.None);
        }

        Assert.True(await _onboarding.CompleteAsync(CancellationToken.None));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the provider checks.");
            await Task.Delay(50);
        }
    }

    public void Dispose()
    {
        _loader.Dispose();
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CheckHandler : HttpMessageHandler
    {
        private int _requestCount;
        public string? FailFor { get; set; }
        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            if (FailFor is not null && request.RequestUri!.Host.StartsWith(FailFor, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class HandlerHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CollectingChecks : IProviderConnectionCheckRepository
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, ProviderConnectionCheck> _checks = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, ProviderConnectionCheck> Checks
        {
            get { lock (_gate) { return new Dictionary<string, ProviderConnectionCheck>(_checks, StringComparer.OrdinalIgnoreCase); } }
        }

        public Task<IReadOnlyList<ProviderConnectionCheck>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProviderConnectionCheck>>(Checks.Values.ToList());

        public Task UpsertAsync(ProviderConnectionCheck check, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _checks[check.ProviderName] = check;
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(string providerName, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _checks.Remove(providerName);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHealth : IProviderHealthMonitor
    {
        public Dictionary<string, ProviderHealthStatus> Statuses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Successes { get; } = [];

        public Task ReportSuccessAsync(string providerId, CancellationToken ct = default)
        {
            Successes.Add(providerId);
            Statuses[providerId] = ProviderHealthStatus.Healthy;
            return Task.CompletedTask;
        }

        public Task ReportFailureAsync(string providerId, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;

        public bool IsDown(string providerId) => GetStatus(providerId) == ProviderHealthStatus.Down;

        public ProviderHealthStatus GetStatus(string providerId) =>
            Statuses.GetValueOrDefault(providerId, ProviderHealthStatus.Healthy);
    }
}
