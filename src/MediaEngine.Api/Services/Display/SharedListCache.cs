using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Api.Services.Display;

/// <summary>
/// A cache for lists that are expensive to build (1 to 3 s on a real library) and shared by many screens. A list older than
/// <see cref="RefreshAfter"/> is still returned at once while one background reload (shared by every reader) replaces it; a missing
/// list is loaded once, and every reader that arrives meanwhile waits for that same load instead of starting another. A list is
/// therefore never older than <see cref="HardExpiry"/> (30 s), the same freshness as the plain 30 second cache it replaces; the
/// difference is that a reader arriving in the last 10 seconds gets the list at once instead of waiting for a reload. A list that
/// reaches <see cref="HardExpiry"/> is dropped, so an idle Engine does not hold stale lists. The load ignores the caller's cancellation, because other readers share it.
/// </summary>
public sealed class SharedListCache(IMemoryCache cache, ILogger? logger = null, TimeProvider? time = null)
{
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan HardExpiry = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _loads = new(StringComparer.Ordinal);

    public async Task<T> GetAsync<T>(string key, Func<Task<T>> load, CancellationToken ct)
        where T : class
    {
        if (cache.TryGetValue(key, out SharedList<T>? entry) && entry is not null)
        {
            if (_time.GetElapsedTime(entry.LoadedAt) >= RefreshAfter)
            {
                var refresh = StartLoad(key, load);
                // Nobody awaits a background reload, so a failure is logged here; the old list then ages out and the next reader loads it.
                _ = refresh.ContinueWith(
                    failed => logger?.LogWarning(failed.Exception, "Reloading the shared list {Key} in the background failed.", key),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return entry.Value;
        }

        return (T)await StartLoad(key, load).WaitAsync(ct);
    }

    private Task<object> StartLoad<T>(string key, Func<Task<T>> load)
        where T : class =>
        // Task.Run: the data store reads run synchronously up to their first real wait, so without it the reader that starts a background
        // reload would pay for it on its own thread.
        _loads.GetOrAdd(key, _ => new Lazy<Task<object>>(() => Task.Run(() => RunLoadAsync(key, load)))).Value;

    private async Task<object> RunLoadAsync<T>(string key, Func<Task<T>> load)
        where T : class
    {
        try
        {
            var value = await load().ConfigureAwait(false);
            cache.Set(key, new SharedList<T>(value, _time.GetTimestamp()), HardExpiry);
            return value;
        }
        finally
        {
            _loads.TryRemove(key, out _);
        }
    }

    private sealed class SharedList<T>(T value, long loadedAt)
    {
        public T Value { get; } = value;

        public long LoadedAt { get; } = loadedAt;
    }
}
