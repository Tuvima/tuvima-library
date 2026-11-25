using MediaEngine.Domain.Contracts;
using Microsoft.Extensions.Logging;
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
    private volatile ILogger? _logger;

    public DurableWikidataResponseCache(IWikidataResponseCache? memory = null)
    {
        _memory = memory ?? new InMemoryWikidataResponseCache();
    }

    /// <summary>Connects the data store once the app's services exist (the library is configured before they do).</summary>
    public void Attach(IProviderResponseCacheRepository? store, ILogger? logger = null)
    {
        _logger = logger;
        _store = store;
    }

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

        CachedResponse? stored;
        try
        {
            stored = await store.FindAsync(StoreKey(key), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The data store is only a second memory: a busy or failing store must never fail the Wikidata request itself.
            _logger?.LogWarning(ex, "Wikidata answer could not be read from the data store; asking Wikidata instead");
            return null;
        }

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
        try
        {
            await store.UpsertAsync(
                StoreKey(key), ProviderId, StoreKey(key), response, null, hours, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same rule as reading: the answer is already in memory, so a store failure only costs a later repeat request.
            _logger?.LogWarning(ex, "Wikidata answer could not be saved to the data store; kept in memory only");
        }
    }

    private static string StoreKey(WikidataResponseCacheKey key) => $"{ProviderId}:{key.Host}:{key.Endpoint}:{key.Key}";
}
