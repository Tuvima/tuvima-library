using System.Collections.Concurrent;
using System.Diagnostics;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Providers.Services;

public interface IProviderRateLimiterCoordinator
{
    Task<T> ExecuteAsync<T>(
        string providerName,
        ProviderRateLimitConfiguration? rateLimit,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct);

    Task ExecuteAsync(
        string providerName,
        ProviderRateLimitConfiguration? rateLimit,
        Func<CancellationToken, Task> operation,
        CancellationToken ct);

    IReadOnlyList<ProviderActivitySnapshot> GetSnapshots();
}

public sealed record ProviderActivitySnapshot(
    string ProviderName,
    int ActiveRequests,
    int WaitingRequests,
    long RequestsTotal,
    int RequestsLastMinute,
    int MaxActiveLastMinute,
    long ErrorsTotal,
    int ErrorsLastMinute,
    long ThrottleWaitMsTotal,
    long WaitMsLastMinute,
    double AverageWaitMs,
    double AverageLatencyMs,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastRequestAt,
    string? LastError);

public sealed class ProviderRateLimiterCoordinator : IProviderRateLimiterCoordinator
{
    private readonly ConcurrentDictionary<string, ProviderRequestLimiter> _limiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProviderActivityCounter> _activity = new(StringComparer.OrdinalIgnoreCase);

    public async Task<T> ExecuteAsync<T>(
        string providerName,
        ProviderRateLimitConfiguration? rateLimit,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(operation);

        var limiter = _limiters.GetOrAdd(providerName, _ => new ProviderRequestLimiter(rateLimit));
        var activity = _activity.GetOrAdd(providerName, name => new ProviderActivityCounter(name));

        activity.RecordWaiting();
        ProviderRateLimitLease lease;
        try
        {
            // Waiting for a turn is not work: the stage timeout stands still meanwhile.
            using (StageTimeoutClock.PauseAmbient())
            {
                lease = await limiter.WaitAsync(ct).ConfigureAwait(false);
            }

            activity.RecordStarted(lease.WaitDuration);
        }
        catch
        {
            activity.RecordWaitAbandoned();
            throw;
        }

        try
        {
            return await ExecuteWithLeaseAsync(activity, operation, ct).ConfigureAwait(false);
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<T> ExecuteWithLeaseAsync<T>(
        ProviderActivityCounter activity,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await operation(ct).ConfigureAwait(false);
            activity.RecordCompleted(stopwatch.Elapsed, error: null, successful: true);
            return result;
        }
        catch (OperationCanceledException)
        {
            activity.RecordCompleted(stopwatch.Elapsed, error: null, successful: false);
            throw;
        }
        catch (Exception ex)
        {
            activity.RecordCompleted(stopwatch.Elapsed, ex, successful: false);
            throw;
        }
    }

    public async Task ExecuteAsync(
        string providerName,
        ProviderRateLimitConfiguration? rateLimit,
        Func<CancellationToken, Task> operation,
        CancellationToken ct)
    {
        await ExecuteAsync(
            providerName,
            rateLimit,
            async token =>
            {
                await operation(token).ConfigureAwait(false);
                return true;
            },
            ct).ConfigureAwait(false);
    }

    public IReadOnlyList<ProviderActivitySnapshot> GetSnapshots() =>
        _activity.Values
            .Select(counter => counter.Snapshot(DateTimeOffset.UtcNow))
            .OrderBy(snapshot => snapshot.ProviderName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private sealed class ProviderRequestLimiter
    {
        private readonly SemaphoreSlim _concurrency;
        private readonly object _tokenLock = new();
        private readonly double _requestsPerSecond;
        private readonly int _burst;
        private double _tokens;
        private DateTimeOffset _lastRefillUtc;

        public ProviderRequestLimiter(ProviderRateLimitConfiguration? rateLimit)
        {
            var maxConcurrency = Math.Max(1, rateLimit?.MaxConcurrency ?? 1);
            _concurrency = new SemaphoreSlim(maxConcurrency, maxConcurrency);

            _requestsPerSecond = ResolveRatePerSecond(rateLimit);
            _burst = Math.Max(1, rateLimit?.Burst ?? maxConcurrency);
            _tokens = _burst;
            _lastRefillUtc = DateTimeOffset.UtcNow;
        }

        public async Task<ProviderRateLimitLease> WaitAsync(CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            await _concurrency.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await WaitForTokenAsync(ct).ConfigureAwait(false);
                return new ProviderRateLimitLease(this, stopwatch.Elapsed);
            }
            catch
            {
                _concurrency.Release();
                throw;
            }
        }

        private async Task WaitForTokenAsync(CancellationToken ct)
        {
            if (_requestsPerSecond <= 0)
            {
                return;
            }

            while (true)
            {
                TimeSpan delay;
                lock (_tokenLock)
                {
                    RefillTokens(DateTimeOffset.UtcNow);
                    if (_tokens >= 1)
                    {
                        _tokens -= 1;
                        return;
                    }

                    var missingTokens = 1 - _tokens;
                    delay = TimeSpan.FromSeconds(missingTokens / _requestsPerSecond);
                    if (delay < TimeSpan.FromMilliseconds(25))
                    {
                        delay = TimeSpan.FromMilliseconds(25);
                    }
                }

                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        private void RefillTokens(DateTimeOffset now)
        {
            var elapsedSeconds = (now - _lastRefillUtc).TotalSeconds;
            if (elapsedSeconds <= 0)
            {
                return;
            }

            _tokens = Math.Min(_burst, _tokens + elapsedSeconds * _requestsPerSecond);
            _lastRefillUtc = now;
        }

        private static double ResolveRatePerSecond(ProviderRateLimitConfiguration? rateLimit)
        {
            if (rateLimit?.RequestsPerSecond is > 0)
            {
                return rateLimit.RequestsPerSecond.Value;
            }

            if (rateLimit?.RequestsPerMinute is > 0)
            {
                return rateLimit.RequestsPerMinute.Value / 60.0;
            }

            return 0;
        }

        public void Release() => _concurrency.Release();
    }

    private sealed class ProviderRateLimitLease : IAsyncDisposable
    {
        private readonly ProviderRequestLimiter _owner;
        private bool _disposed;

        public ProviderRateLimitLease(ProviderRequestLimiter owner, TimeSpan waitDuration)
        {
            _owner = owner;
            WaitDuration = waitDuration;
        }

        public TimeSpan WaitDuration { get; }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _owner.Release();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ProviderActivityCounter
    {
        private readonly ConcurrentQueue<DateTimeOffset> _requestStarts = new();
        private readonly ConcurrentQueue<DateTimeOffset> _errorStarts = new();
        private readonly ConcurrentQueue<ProviderActivitySample> _activeSamples = new();
        private readonly ConcurrentQueue<ProviderWaitSample> _waitSamples = new();
        private long _waitingRequests;
        private long _activeRequests;
        private long _requestsTotal;
        private long _errorsTotal;
        private long _throttleWaitMsTotal;
        private long _latencyMsTotal;
        private string? _lastError;
        private DateTimeOffset? _lastSuccessAt;
        private DateTimeOffset? _lastRequestAt;

        public ProviderActivityCounter(string providerName)
        {
            ProviderName = providerName;
        }

        private string ProviderName { get; }

        public void RecordWaiting()
        {
            Interlocked.Increment(ref _waitingRequests);
        }

        public void RecordWaitAbandoned()
        {
            Interlocked.Decrement(ref _waitingRequests);
        }

        public void RecordStarted(TimeSpan throttleWait)
        {
            var now = DateTimeOffset.UtcNow;
            Interlocked.Decrement(ref _waitingRequests);
            var activeRequests = Interlocked.Increment(ref _activeRequests);
            Interlocked.Increment(ref _requestsTotal);
            var waitMs = Math.Max(0, (long)throttleWait.TotalMilliseconds);
            Interlocked.Add(ref _throttleWaitMsTotal, waitMs);
            _lastRequestAt = now;
            _requestStarts.Enqueue(now);
            _activeSamples.Enqueue(new ProviderActivitySample(now, (int)Math.Min(int.MaxValue, activeRequests)));
            _waitSamples.Enqueue(new ProviderWaitSample(now, waitMs));
        }

        public void RecordCompleted(TimeSpan latency, Exception? error, bool successful)
        {
            Interlocked.Decrement(ref _activeRequests);
            Interlocked.Add(ref _latencyMsTotal, (long)latency.TotalMilliseconds);

            if (successful)
            {
                _lastSuccessAt = DateTimeOffset.UtcNow;
            }

            if (error is null)
            {
                return;
            }

            Interlocked.Increment(ref _errorsTotal);
            _lastError = $"{error.GetType().Name}: {error.Message}";
            _errorStarts.Enqueue(DateTimeOffset.UtcNow);
        }

        public ProviderActivitySnapshot Snapshot(DateTimeOffset now)
        {
            Trim(_requestStarts, now);
            Trim(_errorStarts, now);
            Trim(_activeSamples, now, sample => sample.Timestamp);
            Trim(_waitSamples, now, sample => sample.Timestamp);

            var totalRequests = Interlocked.Read(ref _requestsTotal);
            var totalLatency = Interlocked.Read(ref _latencyMsTotal);
            var totalWait = Interlocked.Read(ref _throttleWaitMsTotal);
            var activeRequests = Math.Max(0, Interlocked.Read(ref _activeRequests));
            var waitingRequests = Math.Max(0, Interlocked.Read(ref _waitingRequests));
            var maxActiveLastMinute = Math.Max(
                (int)Math.Min(int.MaxValue, activeRequests),
                _activeSamples.Select(sample => sample.ActiveRequests).DefaultIfEmpty(0).Max());
            var waitMsLastMinute = _waitSamples.Sum(sample => sample.WaitMs);

            return new ProviderActivitySnapshot(
                ProviderName,
                (int)Math.Min(int.MaxValue, activeRequests),
                (int)Math.Min(int.MaxValue, waitingRequests),
                totalRequests,
                _requestStarts.Count,
                maxActiveLastMinute,
                Interlocked.Read(ref _errorsTotal),
                _errorStarts.Count,
                totalWait,
                waitMsLastMinute,
                totalRequests > 0 ? (double)totalWait / totalRequests : 0,
                totalRequests > 0 ? (double)totalLatency / totalRequests : 0,
                _lastSuccessAt,
                _lastRequestAt,
                _lastError);
        }

        private static void Trim(ConcurrentQueue<DateTimeOffset> queue, DateTimeOffset now)
        {
            var cutoff = now.AddMinutes(-1);
            while (queue.TryPeek(out var value) && value < cutoff)
            {
                queue.TryDequeue(out _);
            }
        }

        private static void Trim<T>(
            ConcurrentQueue<T> queue,
            DateTimeOffset now,
            Func<T, DateTimeOffset> timestampSelector)
        {
            var cutoff = now.AddMinutes(-1);
            while (queue.TryPeek(out var value) && timestampSelector(value) < cutoff)
            {
                queue.TryDequeue(out _);
            }
        }

        private sealed record ProviderActivitySample(DateTimeOffset Timestamp, int ActiveRequests);

        private sealed record ProviderWaitSample(DateTimeOffset Timestamp, long WaitMs);
    }
}

public static class ProviderRateLimitDefaults
{
    public static readonly ProviderRateLimitConfiguration MusicBrainz = new()
    {
        RequestsPerSecond = 1,
        Burst = 1,
        MaxConcurrency = 1
    };

    public static readonly ProviderRateLimitConfiguration Apple = new()
    {
        RequestsPerMinute = 20,
        Burst = 5,
        MaxConcurrency = 2
    };

    public static readonly ProviderRateLimitConfiguration Tmdb = new()
    {
        RequestsPerSecond = 20,
        Burst = 20,
        MaxConcurrency = 8
    };
}
