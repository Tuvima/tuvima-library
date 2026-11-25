using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services;
using Tuvima.Wikidata;

namespace MediaEngine.Providers.Tests;

public sealed class DurableWikidataResponseCacheTests
{
    private static readonly WikidataResponseCacheKey Key = new("www.wikidata.org", "wbsearchentities", "q=zimmer");

    [Fact]
    public async Task Answer_SavedToTheDataStore_SurvivesANewMemory()
    {
        var store = new SpyStore();
        var first = new DurableWikidataResponseCache();
        first.Attach(store);
        await first.SetAsync(Key, """{"search":[]}""", TimeSpan.FromHours(24));

        // An Engine restart: a fresh memory over the same data store.
        var afterRestart = new DurableWikidataResponseCache();
        afterRestart.Attach(store);

        Assert.Equal("""{"search":[]}""", await afterRestart.GetAsync(Key));
        Assert.Equal(24, store.LastTtlHours);
    }

    [Fact]
    public async Task EmptyAnswer_IsSaved_SoNothingFoundIsNotAskedAgain()
    {
        var store = new SpyStore();
        var cache = new DurableWikidataResponseCache();
        cache.Attach(store);

        await cache.SetAsync(Key, """{"query":{"search":[]}}""", TimeSpan.FromHours(12));

        Assert.Single(store.Entries);
    }

    [Fact]
    public async Task HugeAnswer_StaysInMemoryOnly()
    {
        var store = new SpyStore();
        var cache = new DurableWikidataResponseCache();
        cache.Attach(store);

        await cache.SetAsync(Key, new string('x', DurableWikidataResponseCache.MaxStoredCharacters + 1), TimeSpan.FromHours(1));

        Assert.Empty(store.Entries);
        Assert.NotNull(await cache.GetAsync(Key));
    }

    [Fact]
    public async Task WithoutADataStore_BehavesLikeTheMemoryCache()
    {
        var cache = new DurableWikidataResponseCache();

        await cache.SetAsync(Key, "{}", TimeSpan.FromHours(1));

        Assert.Equal("{}", await cache.GetAsync(Key));
    }

    [Fact]
    public async Task FailingDataStore_NeverFailsTheRequest()
    {
        var cache = new DurableWikidataResponseCache();
        cache.Attach(new ThrowingStore());

        await cache.SetAsync(Key, "{}", TimeSpan.FromHours(1));

        Assert.Equal("{}", await cache.GetAsync(Key));
        Assert.Null(await cache.GetAsync(new WikidataResponseCacheKey("h", "e", "other")));
    }

    private sealed class ThrowingStore : IProviderResponseCacheRepository
    {
        public Task<CachedResponse?> FindAsync(string cacheKey, CancellationToken ct = default) => throw new InvalidOperationException("database is locked");
        public Task UpsertAsync(string cacheKey, string providerId, string queryHash, string responseJson, string? etag, int ttlHours, CancellationToken ct = default) => throw new InvalidOperationException("database is locked");
        public Task<string?> FindExpiredEtagAsync(string cacheKey, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task RefreshExpiryAsync(string cacheKey, int ttlHours, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> PurgeExpiredAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ClearAllAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<CacheStats> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(new CacheStats(0, 0, null));
    }

    private sealed class SpyStore : IProviderResponseCacheRepository
    {
        public Dictionary<string, string> Entries { get; } = [];

        public int LastTtlHours { get; private set; }

        public Task<CachedResponse?> FindAsync(string cacheKey, CancellationToken ct = default) =>
            Task.FromResult(Entries.TryGetValue(cacheKey, out var json) ? new CachedResponse(json, null) : null);

        public Task UpsertAsync(string cacheKey, string providerId, string queryHash, string responseJson, string? etag, int ttlHours, CancellationToken ct = default)
        {
            Entries[cacheKey] = responseJson;
            LastTtlHours = ttlHours;
            return Task.CompletedTask;
        }

        public Task<string?> FindExpiredEtagAsync(string cacheKey, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task RefreshExpiryAsync(string cacheKey, int ttlHours, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> PurgeExpiredAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ClearAllAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<CacheStats> GetStatsAsync(CancellationToken ct = default) => Task.FromResult(new CacheStats(0, 0, null));
    }
}
