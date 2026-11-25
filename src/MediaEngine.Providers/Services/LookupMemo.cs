using System.Collections.Concurrent;

namespace MediaEngine.Providers.Services;

/// <summary>
/// A small in-memory memory of lookup answers that expire, shared by one running Engine (speed: #1360). It remembers answers
/// that cost outside requests, including "nothing found", so the same question is not asked again for every title that raises it.
/// Bounded: when it grows past the limit the expired entries are dropped, and if that is not enough the oldest half goes.
/// </summary>
/// <typeparam name="T">The remembered answer.</typeparam>
public sealed class LookupMemo<T>
{
    private const int MaxEntries = 20_000;

    private readonly ConcurrentDictionary<string, (T Value, DateTimeOffset ExpiresAt)> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _clock;

    public LookupMemo(TimeSpan lifetime, TimeProvider? clock = null)
    {
        _lifetime = lifetime;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Number of entries currently held (including any not yet pruned).</summary>
    public int Count => _entries.Count;

    /// <summary>Builds a key from its parts, trimmed and joined; case does not matter.</summary>
    public static string Key(params string?[] parts) =>
        string.Join('\u001f', parts.Select(part => (part ?? string.Empty).Trim()));

    public bool TryGet(string key, out T value)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > _clock.GetUtcNow())
            {
                value = entry.Value;
                return true;
            }

            _entries.TryRemove(key, out _);
        }

        value = default!;
        return false;
    }

    public void Set(string key, T value)
    {
        if (_entries.Count >= MaxEntries)
        {
            Prune();
        }

        _entries[key] = (value, _clock.GetUtcNow().Add(_lifetime));
    }

    /// <summary>Returns the remembered answer, or runs <paramref name="load"/> once and remembers what it returns.</summary>
    public async Task<T> GetOrLoadAsync(string key, Func<Task<T>> load)
    {
        if (TryGet(key, out var known))
        {
            return known;
        }

        var loaded = await load().ConfigureAwait(false);
        Set(key, loaded);
        return loaded;
    }

    public void Remove(string key) => _entries.TryRemove(key, out _);

    public void Clear() => _entries.Clear();

    private void Prune()
    {
        var now = _clock.GetUtcNow();
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }

        if (_entries.Count < MaxEntries)
        {
            return;
        }

        foreach (var pair in _entries.OrderBy(pair => pair.Value.ExpiresAt).Take(_entries.Count / 2))
        {
            _entries.TryRemove(pair.Key, out _);
        }
    }
}
