using System.Collections.Concurrent;
using MediaEngine.Contracts.Realtime;
using MediaEngine.Domain;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Services;

/// <summary>
/// Monitors provider health, runs active probes for down providers,
/// and triggers recovery flushes when providers come back online.
/// </summary>
public sealed class ProviderHealthMonitorService : BackgroundService, IProviderHealthMonitor
{
    private readonly IProviderHealthRepository _repo;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEventPublisher _events;
    private readonly ILogger<ProviderHealthMonitorService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfigurationLoader _configLoader;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    // In-memory cache for fast IsDown() checks — refreshed from DB on changes.
    private readonly ConcurrentDictionary<string, ProviderHealthStatus> _statusCache = new(StringComparer.OrdinalIgnoreCase);

    // When each Down provider is next due a probe (half-open window). Missing or null means "due now".
    private readonly ConcurrentDictionary<string, DateTimeOffset?> _nextCheckCache = new(StringComparer.OrdinalIgnoreCase);

    // Track providers that need recovery flush.
    private readonly ConcurrentQueue<string> _recoveryQueue = new();

    public ProviderHealthMonitorService(
        IProviderHealthRepository repo,
        IHttpClientFactory httpClientFactory,
        IEventPublisher events,
        ILogger<ProviderHealthMonitorService> logger,
        IServiceScopeFactory scopeFactory,
        IConfigurationLoader configLoader)
    {
        _repo = repo;
        _httpClientFactory = httpClientFactory;
        _events = events;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _configLoader = configLoader;
    }

    // ── IProviderHealthMonitor implementation ────────────────────

    public async Task ReportSuccessAsync(string providerId, CancellationToken ct = default)
    {
        bool wasDown = await _repo.RecordSuccessAsync(providerId, ct);
        _statusCache[providerId] = ProviderHealthStatus.Healthy;
        _nextCheckCache.TryRemove(providerId, out _);

        if (wasDown)
        {
            _logger.LogInformation("Provider {Provider} recovered — queueing recovery flush", providerId);
            _recoveryQueue.Enqueue(providerId);

            // Notify Dashboard.
            await _events.PublishAsync(
                SignalREvents.ProviderStatusChanged,
                new ProviderStatusChangedEvent(
                    providerId,
                    "Healthy",
                    $"{providerId} is back online"),
                ct);
        }
    }

    public async Task ReportFailureAsync(string providerId, string reason, CancellationToken ct = default)
    {
        var previousStatus = GetStatus(providerId);
        var newStatus = await _repo.RecordFailureAsync(providerId, reason, ct);
        _statusCache[providerId] = newStatus;

        if (newStatus == ProviderHealthStatus.Down)
        {
            var record = await _repo.GetAsync(providerId, ct);
            _nextCheckCache[providerId] = record?.NextCheckAt ?? DateTimeOffset.UtcNow.AddMinutes(5);
        }
        else
        {
            _nextCheckCache.TryRemove(providerId, out _);
        }

        // Notify Dashboard on transition to Down.
        if (newStatus == ProviderHealthStatus.Down && previousStatus != ProviderHealthStatus.Down)
        {
            await _events.PublishAsync(
                SignalREvents.ProviderStatusChanged,
                new ProviderStatusChangedEvent(
                    providerId,
                    "Down",
                    $"{providerId} is unreachable"),
                ct);
        }
    }

    /// <summary>
    /// True while the provider is Down and its next probe is not yet due. Once <c>next_check_at</c> has
    /// passed (or was never set) the provider is half-open: real requests are allowed through again and
    /// the next success or failure decides whether it recovers or goes back to Down with a later check.
    /// </summary>
    public bool IsDown(string providerId)
    {
        if (!_statusCache.TryGetValue(providerId, out var status) || status != ProviderHealthStatus.Down)
        {
            return false;
        }

        return _nextCheckCache.TryGetValue(providerId, out var nextCheckAt)
            && nextCheckAt is { } due
            && due > DateTimeOffset.UtcNow;
    }

    public ProviderHealthStatus GetStatus(string providerId)
        => _statusCache.TryGetValue(providerId, out var status)
            ? status
            : ProviderHealthStatus.Healthy;

    // ── Background loop: active probes + recovery flush ─────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup delay — let the app finish initialising.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        // Load initial state from database.
        await RefreshCacheAsync(stoppingToken);

        _logger.LogInformation("ProviderHealthMonitor started — {Count} providers tracked",
            _statusCache.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 1. Process recovery flushes.
                await ProcessRecoveryQueueAsync(stoppingToken);

                // 2. Run active probes for down providers whose next_check_at has passed.
                await RunActiveProbesAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProviderHealthMonitor loop error");
            }

            // Check every 60 seconds.
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    internal async Task RefreshCacheAsync(CancellationToken ct)
    {
        var records = await _repo.GetAllAsync(ct);
        foreach (var r in records)
        {
            _statusCache[r.ProviderId] = r.Status;
            if (r.Status == ProviderHealthStatus.Down)
            {
                _nextCheckCache[r.ProviderId] = r.NextCheckAt;
            }
            else
            {
                _nextCheckCache.TryRemove(r.ProviderId, out _);
            }
        }
    }

    internal async Task RunActiveProbesAsync(CancellationToken ct)
    {
        var downProviders = await _repo.GetDownProvidersAsync(ct);
        var now = DateTimeOffset.UtcNow;

        foreach (var provider in downProviders)
        {
            // A missing next_check_at means the probe is due now.
            if (provider.NextCheckAt.HasValue && provider.NextCheckAt.Value > now)
            {
                continue; // Not time yet.
            }

            var probeUri = ResolveProbeUri(provider.ProviderId);
            if (probeUri is null)
            {
                // No endpoint to probe: IsDown() turns half-open once the check time passes, so the
                // next real request decides whether the provider has recovered.
                _logger.LogDebug(
                    "Provider {Provider} has no probe endpoint; recovery is detected on its next real request",
                    provider.ProviderId);
                continue;
            }

            _logger.LogDebug("Active health probe for {Provider} at {Uri}", provider.ProviderId, probeUri);

            try
            {
                // The provider's named HttpClient when one is registered (the factory falls back to its default client otherwise).
                using var client = _httpClientFactory.CreateClient(provider.ProviderId);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ProbeTimeout);

                using var response = await client.GetAsync(probeUri, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                // Any answer below 500 (other than 429) proves the service is reachable again, even if
                // the probe itself was not an authorised or valid request.
                if ((int)response.StatusCode < 500 && response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
                {
                    // Provider is back! Record success — triggers recovery.
                    await ReportSuccessAsync(provider.ProviderId, ct);
                    _logger.LogInformation("Active probe: {Provider} is back online (HTTP {Status})",
                        provider.ProviderId, (int)response.StatusCode);
                }
                else
                {
                    await ReportFailureAsync(provider.ProviderId,
                        $"HTTP {(int)response.StatusCode}", ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                await ReportFailureAsync(provider.ProviderId, ex.Message, ct);
                _logger.LogDebug("Active probe: {Provider} still down — {Error}",
                    provider.ProviderId, ex.Message);
            }
        }
    }

    /// <summary>
    /// The URL used to probe a Down provider: its configured "api" endpoint, otherwise the first
    /// absolute http(s) endpoint. Null when the provider has no usable endpoint.
    /// </summary>
    private Uri? ResolveProbeUri(string providerName)
    {
        var config = _configLoader.LoadProvider(providerName);
        if (config?.Endpoints is not { Count: > 0 } endpoints)
        {
            return null;
        }

        if (endpoints.TryGetValue("api", out var api) && TryParseHttpUri(api, out var apiUri))
        {
            return apiUri;
        }

        foreach (var endpoint in endpoints.Values)
        {
            if (TryParseHttpUri(endpoint, out var uri))
            {
                return uri;
            }
        }

        return null;
    }

    private static bool TryParseHttpUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https")
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private async Task ProcessRecoveryQueueAsync(CancellationToken ct)
    {
        while (_recoveryQueue.TryDequeue(out var providerId))
        {
            _logger.LogInformation("Recovery flush for {Provider} — loading waiting items", providerId);

            try
            {
                // Find all deferred items waiting for this provider.
                using var scope = _scopeFactory.CreateScope();
                var deferredRepo = scope.ServiceProvider.GetRequiredService<IDeferredEnrichmentRepository>();
                var pipeline = scope.ServiceProvider.GetRequiredService<IHydrationPipelineService>();

                var waitingItems = await deferredRepo.GetByFailedProviderAsync(providerId, limit: 50, ct: ct);

                if (waitingItems.Count == 0)
                {
                    _logger.LogDebug("No items waiting for {Provider}", providerId);
                    continue;
                }

                _logger.LogInformation("Flushing {Count} items waiting for {Provider}",
                    waitingItems.Count, providerId);

                // Notify Dashboard.
                await _events.PublishAsync(
                    SignalREvents.ProviderRecoveryFlush,
                    new ProviderRecoveryFlushEvent(
                        providerId,
                        waitingItems.Count,
                        $"{providerId} is back online — {waitingItems.Count} items queued for enrichment"),
                    ct);

                int processed = 0;
                foreach (var item in waitingItems)
                {
                    try
                    {
                        var request = new HarvestRequest
                        {
                            EntityId = item.EntityId,
                            EntityType = EntityType.MediaAsset,
                            MediaType = item.MediaType,
                            Pass = HydrationPass.Quick,
                            SuppressActivityEntry = true,
                        };

                        await pipeline.RunSynchronousAsync(request, ct);
                        await deferredRepo.MarkProcessedAsync(item.Id, ct);
                        processed++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Recovery flush failed for entity {Id}", item.EntityId);
                        // Leave as pending — will retry next cycle.
                    }
                }

                _logger.LogInformation("Recovery flush complete for {Provider}: {Processed}/{Total} items",
                    providerId, processed, waitingItems.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Recovery flush failed for provider {Provider}", providerId);
            }
        }
    }
}
