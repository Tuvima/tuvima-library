using MediaEngine.Domain.Contracts;
using Tuvima.Wikidata;

namespace MediaEngine.Providers.Services;

/// <summary>
/// Keeps Wikidata and Wikipedia answers in the data store as well as in memory (speed: #1360). The library's own cache forgets
/// everything when the Engine restarts and it is the only memory of "nothing found" for people, Series and ID lookups, so a
/// restart in the middle of a big ingestion used to repeat tens of thousands of requests. Memory is asked first; the data
/// store second; a hit from the data store is copied back to memory.
/// </summary>
public sealed class DurableWikidataResponseCache : IWikidataResponseCache
{
    /// <summary>One answer larger than this stays in memory only, so a few huge answers cannot bloat the data store.</summary>
    internal const int MaxStoredCharacters = 2_000_000;

    private const string ProviderId = "wikidata-library-cache";

    private readonly IWikidataResponseCache _memory;
    private volatile IProviderResponseCacheRepository? _store;

    public DurableWikidataResponseCache(IWikidataResponseCache? memory = null)
    {
        _memory = memory ?? new InMemoryWikidataResponseCache();
    }

    /// <summary>Connects the data store once the app's services exist (the library is configured before they do).</summary>
    public void Attach(IProviderResponseCacheRepository? store) => _store = store;

    public async ValueTask<string?> GetAsync(WikidataResponseCacheKey key, CancellationToken cancellationToken = default)
    {
        var inMemory = await _memory.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (inMemory is not null)
        {
            return inMemory;
        }

        var store = _store;
        if (store is null)
        {
            return null;
        }

        var stored = await store.FindAsync(StoreKey(key), cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        await _memory.SetAsync(key, stored.ResponseJson, TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false);
        return stored.ResponseJson;
    }

    public async ValueTask SetAsync(
        WikidataResponseCacheKey key,
        string response,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        await _memory.SetAsync(key, response, ttl, cancellationToken).ConfigureAwait(false);

        var store = _store;
        if (store is null || ttl <= TimeSpan.Zero || string.IsNullOrWhiteSpace(response) || response.Length > MaxStoredCharacters)
        {
            return;
        }

        var hours = Math.Max(1, (int)Math.Ceiling(ttl.TotalHours));
        await store.UpsertAsync(
            StoreKey(key), ProviderId, StoreKey(key), response, null, hours, cancellationToken).ConfigureAwait(false);
    }

    private static string StoreKey(WikidataResponseCacheKey key) => $"{ProviderId}:{key.Host}:{key.Endpoint}:{key.Key}";
}
