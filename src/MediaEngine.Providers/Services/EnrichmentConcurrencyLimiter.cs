using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Providers.Services;

/// <summary>
/// App-wide semaphore set for shared external enrichment dependencies.
/// </summary>
public sealed class EnrichmentConcurrencyLimiter : IEnrichmentConcurrencyLimiter, IDisposable
{
    /// <summary>Slots for the slow retail lane; matches the comics lane batch size.</summary>
    internal const int SlowRetailProviderSlots = 3;

    private readonly Dictionary<EnrichmentWorkKind, SemaphoreSlim> _limiters;
    private readonly ILogger<EnrichmentConcurrencyLimiter> _logger;

    public EnrichmentConcurrencyLimiter(
        HydrationSettings settings,
        ILogger<EnrichmentConcurrencyLimiter> logger)
    {
        _logger = logger;
        _limiters = new Dictionary<EnrichmentWorkKind, SemaphoreSlim>
        {
            [EnrichmentWorkKind.RetailProvider] = new(NormalizeLimit(settings.MaxConcurrentRetailProviderJobs), NormalizeLimit(settings.MaxConcurrentRetailProviderJobs)),
            [EnrichmentWorkKind.SlowRetailProvider] = new(SlowRetailProviderSlots, SlowRetailProviderSlots),
            [EnrichmentWorkKind.Wikidata] = new(NormalizeLimit(settings.MaxConcurrentWikidataJobs), NormalizeLimit(settings.MaxConcurrentWikidataJobs)),
            [EnrichmentWorkKind.TmdbArtwork] = new(NormalizeLimit(settings.MaxConcurrentTmdbArtworkJobs), NormalizeLimit(settings.MaxConcurrentTmdbArtworkJobs)),
            [EnrichmentWorkKind.WriteBack] = new(NormalizeLimit(settings.MaxConcurrentWriteBackJobs), NormalizeLimit(settings.MaxConcurrentWriteBackJobs)),
        };
    }

    public async Task RunAsync(
        EnrichmentWorkKind kind,
        Func<CancellationToken, Task> operation,
        CancellationToken ct = default)
    {
        await RunAsync<object?>(kind, async token =>
        {
            await operation(token).ConfigureAwait(false);
            return null;
        }, ct).ConfigureAwait(false);
    }

    public async Task<T> RunAsync<T>(
        EnrichmentWorkKind kind,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        var limiter = _limiters[kind];
        var waitStarted = DateTimeOffset.UtcNow;
        await limiter.WaitAsync(ct).ConfigureAwait(false);
        var waitMs = (DateTimeOffset.UtcNow - waitStarted).TotalMilliseconds;
        if (waitMs > 1000)
        {
            _logger.LogDebug(
                "Waited {WaitMs:F0}ms for {WorkKind} concurrency slot",
                waitMs,
                kind);
        }

        try
        {
            return await operation(ct).ConfigureAwait(false);
        }
        finally
        {
            limiter.Release();
        }
    }

    public int GetAvailableSlots(EnrichmentWorkKind kind) => _limiters[kind].CurrentCount;

    public void Dispose()
    {
        foreach (var limiter in _limiters.Values)
        {
            limiter.Dispose();
        }
    }

    private static int NormalizeLimit(int configured) => Math.Max(1, configured);
}
